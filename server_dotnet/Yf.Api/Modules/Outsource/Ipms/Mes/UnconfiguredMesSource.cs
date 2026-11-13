using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>Registered in <c>Fake</c> mode only; the mirror never runs then, so this only refuses stray calls.</summary>
public sealed class UnconfiguredMesSource : IMesSource
{
    public Task<MesTestResult> TestAsync(MesConnection connection, CancellationToken ct) =>
        Task.FromResult(new MesTestResult(false, "当前使用测试资料，不连接 IPMS", null, 0));

    public Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct) =>
        throw new IpmsUnavailableException("当前使用测试资料，不连接 IPMS");
}

internal static class MesSourceFactory
{
    internal static IMesSource Create(AppOptions options, IServiceProvider services)
    {
        if (options.Ipms.Mode == IpmsOptions.FakeMode) return new UnconfiguredMesSource();
        // One long-lived client: the per-page timeout is enforced by the source, so the client itself never times out.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new MesHttpSource(http, options.Ipms, services.GetRequiredService<IMesSettings>());
    }
}

/// <summary>
/// The IPMS connection saved on the settings page (<c>mes_mirror_state</c>): each value set there wins over its
/// <c>App:Ipms</c> counterpart. The password is stored encrypted with <see cref="SecretBox"/>.
/// </summary>
public sealed class DbMesSettings(IDbContextFactory<YfDbContext> dbFactory, AppOptions options, ILogger<DbMesSettings>? logger = null) : IMesSettings
{
    internal const string PasswordPurpose = "yf.ipms.mes.v1";

    public async Task<MesConnection> ConnectionAsync(CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var saved = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => new { x.BaseUrl, x.Account, x.ProtectedPassword }).SingleOrDefaultAsync(ct);
        var ipms = options.Ipms;
        var url = !string.IsNullOrWhiteSpace(saved?.BaseUrl) ? saved.BaseUrl : string.IsNullOrWhiteSpace(ipms.MesBaseUrl) ? null : ipms.MesBaseUrl;
        var account = !string.IsNullOrWhiteSpace(saved?.Account) ? saved.Account : ipms.MesAccount;
        var password = string.IsNullOrEmpty(saved?.ProtectedPassword) ? ipms.MesPassword : Unprotect(saved.ProtectedPassword);
        return new(url, account ?? "", password ?? "");
    }

    internal string Protect(string password) => SecretBox.Protect(PasswordPurpose, options.JwtSecret, password);

    /// <summary>A password that no longer decrypts (the application secret changed) counts as not set.</summary>
    private string Unprotect(string stored)
    {
        try { return SecretBox.Unprotect(PasswordPurpose, options.JwtSecret, stored); }
        catch (CryptographicException)
        {
            logger?.LogWarning("The saved IPMS password can no longer be decrypted; enter it again on the outsourcing settings page.");
            return "";
        }
    }
}
