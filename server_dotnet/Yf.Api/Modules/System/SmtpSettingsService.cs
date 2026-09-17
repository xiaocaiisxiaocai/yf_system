using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

public sealed record SmtpSettingsUpdate(string Host, int Port, string Username, string From, string Security, string? Password);
public sealed record SmtpSettingsView(string Host, int Port, string Username, string From, string Security, bool HasPassword, bool Configured, bool PasswordNeedsUpdate);
internal sealed record ResolvedSmtpSettings(SmtpOptions Options, bool PasswordNeedsUpdate)
{
    public bool Configured => Options.IsConfigured && !PasswordNeedsUpdate;
    public SmtpSettingsView View => new(Options.Host, Options.Port, Options.Username, Options.From, Options.Security, Options.Password.Length > 0, Configured, PasswordNeedsUpdate);
}

public sealed class SmtpSettingsService(AppDb db, AppOptions options, AuditService audit)
{
    internal const string ConfigKey = "mail.smtp";
    private sealed record StoredSettings(string Host, int Port, string Username, string From, string Security, string ProtectedPassword);

    public async Task<SmtpSettingsView> GetAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await ResolveAsync(conn, null, ct)).View;
    }

    internal async Task<ResolvedSmtpSettings> ResolveAsync(MySqlConnection conn, MySqlTransaction? tx, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        var json = await context.SystemConfigs.Where(config => config.CfgKey == ConfigKey)
            .Select(config => config.CfgValue).SingleOrDefaultAsync(ct);
        if (json is null)
        {
            var source = options.Smtp;
            return new(new() { Host = source.Host, Port = source.Port, Username = source.Username, From = source.From, Security = source.Security, Password = source.Password }, false);
        }
        var stored = JsonSerializer.Deserialize<StoredSettings>(json) ?? throw new InvalidOperationException("Invalid SMTP settings.");
        var cfg = new SmtpOptions { Host = stored.Host, Port = stored.Port, Username = stored.Username, From = stored.From, Security = stored.Security };
        try { cfg.Password = Unprotect(stored.ProtectedPassword); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return new(cfg, true); }
        return new(cfg, false);
    }

    public async Task<SmtpSettingsView> SaveAsync(SmtpSettingsUpdate request, CurrentUser actor, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "config:manage", ct);
        var previous = await ResolveAsync(conn, tx, ct);
        if (string.IsNullOrEmpty(request.Password) && previous.Options.Password.Length > 0 &&
            (!string.Equals(request.Host?.Trim(), previous.Options.Host, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(request.Username?.Trim(), previous.Options.Username, StringComparison.Ordinal)))
            throw ApiException.BadRequest("更改 SMTP 服务器或登录账号时，请重新填写授权码");
        var next = Normalize(request, string.IsNullOrEmpty(request.Password) ? previous.Options.Password : request.Password);
        var stored = new StoredSettings(next.Host, next.Port, next.Username, next.From, next.Security, Protect(next.Password));
        await using var context = EfDb.Use(conn, tx);
        var serialized = JsonSerializer.Serialize(stored);
        var updatedAt = await context.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        var updated = await context.SystemConfigs.Where(config => config.CfgKey == ConfigKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(config => config.CfgValue, serialized)
                .SetProperty(config => config.Description, "邮件发送连接配置")
                .SetProperty(config => config.UpdatedAt, updatedAt), ct);
        if (updated == 0)
        {
            context.SystemConfigs.Add(new SystemConfig
            {
                CfgKey = ConfigKey,
                CfgValue = serialized,
                Description = "邮件发送连接配置",
                UpdatedAt = updatedAt,
            });
            await context.SaveChangesAsync(ct);
        }
        await audit.WriteAsync(conn, tx, actor.Id, "CONFIG_UPDATE", "system_config", null,
            new
            {
                keys = new[] { ConfigKey },
                passwordChanged = !string.IsNullOrEmpty(request.Password),
                targetName = "SMTP 邮件设置",
                changes = AuditChange.OnlyChanged(
                    new AuditChange("host", "SMTP 服务器", previous.Options.Host, next.Host),
                    new AuditChange("port", "端口", previous.Options.Port, next.Port),
                    new AuditChange("username", "登录账号", previous.Options.Username, next.Username),
                    new AuditChange("from", "发件邮箱", previous.Options.From, next.From),
                    new AuditChange("security", "加密方式", previous.Options.Security, next.Security))
            }, null, ct);
        await tx.CommitAsync(ct);
        return new ResolvedSmtpSettings(next, false).View;
    }

    internal static SmtpOptions Normalize(SmtpSettingsUpdate request, string password)
    {
        var host = request.Host?.Trim() ?? "";
        var username = request.Username?.Trim() ?? "";
        var from = request.From?.Trim() ?? "";
        if (host.Length is < 1 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw ApiException.BadRequest("请输入有效的 SMTP 服务器域名或 IP 地址，不要包含协议或路径");
        if (request.Port is < 1 or > 65535) throw ApiException.BadRequest("SMTP 端口需为 1–65535");
        if (username.Length is < 1 or > 320 || username.Any(char.IsControl)) throw ApiException.BadRequest("请输入有效的 SMTP 登录账号");
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024) throw ApiException.BadRequest("请填写邮箱密码或授权码（最多 1024 字符）");
        if (from.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(from, out var address) || address.Address != from)
            throw ApiException.BadRequest("请输入有效的发件邮箱地址");
        if (request.Security is not ("Auto" or "SslOnConnect" or "StartTls")) throw ApiException.BadRequest("请选择有效的 SMTP 加密方式");
        return new() { Host = host, Port = request.Port, Username = username, From = from, Security = request.Security, Password = password };
    }

    // Derive a purpose-specific encryption key from the private application key, which is backed up with the application configuration.
    private byte[] EncryptionKey => SHA256.HashData(Encoding.UTF8.GetBytes("yf.smtp.settings.v1\0" + options.JwtSecret));
    internal string Protect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(EncryptionKey, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return "v1." + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    internal string Unprotect(string value)
    {
        if (!value.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Invalid SMTP credential envelope.");
        var data = Convert.FromBase64String(value[3..]);
        if (data.Length < 28) throw new CryptographicException("Invalid SMTP credential envelope.");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(EncryptionKey, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
