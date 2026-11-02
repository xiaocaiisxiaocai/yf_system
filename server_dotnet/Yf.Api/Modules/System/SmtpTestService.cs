using System.Net.Mail;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

public sealed record SmtpTestRequest(string Recipient);
public sealed record SmtpTestResponse(bool Success, string Message);

public sealed class SmtpTestService(AppDb db, SmtpSettingsService settings) : IDisposable
{
    private readonly SemaphoreSlim sending = new(1, 1);
    public void Dispose() => sending.Dispose();

    public async Task<SmtpTestResponse> TestAsync(SmtpTestRequest request, CurrentUser actor, CancellationToken ct)
    {
        var recipient = ValidateRecipient(request.Recipient);
        if (!await sending.WaitAsync(0, ct)) throw ApiException.Conflict("正在发送测试邮件，请稍后再试");
        try
        {
            ResolvedSmtpSettings resolved;
            await using (var connection = await db.OpenAsync(ct))
            {
                actor = await AccessService.RecheckActorAsync(connection, null, actor, ct);
                AccessService.RequireInternal(actor);
                await AccessService.RequirePermissionAsync(connection, null, actor, "config:manage", ct);
                resolved = await settings.ResolveAsync(connection, null, ct);
            }
            if (!resolved.Configured) throw ApiException.BadRequest("请先填写并保存完整的 SMTP 配置；授权码无法读取时请重新填写");
            return await SendAsync(new MailKitSmtpDelivery(), resolved.Options, recipient, ct);
        }
        finally { sending.Release(); }
    }

    internal static string ValidateRecipient(string? value)
    {
        var recipient = value?.Trim() ?? "";
        if (recipient.Length > 320 || recipient.Any(char.IsControl) || !MailAddress.TryCreate(recipient, out var address) || address.Address != recipient)
            throw ApiException.BadRequest("请输入一个有效的测试收件邮箱");
        return recipient;
    }

    internal static async Task<SmtpTestResponse> SendAsync(ISmtpDelivery delivery, SmtpOptions options, string recipient, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await delivery.SendAsync(options, new(recipient, "[供应商协作平台] SMTP 测试邮件",
                "这是一封 SMTP 配置测试邮件。收到此邮件说明当前配置已成功完成邮件发送。\n此邮件由管理员手动触发，请勿回复。"), timeout.Token);
            return new(true, "SMTP 服务器已接受测试邮件，请检查收件箱或垃圾邮件；最终送达以实际收件为准。");
        }
        catch (MailKit.Security.AuthenticationException) { return new(false, "SMTP 认证失败：请检查登录账号、密码或授权码，以及邮箱是否允许 SMTP AUTH；当前支持账号密码认证。"); }
        catch (SslHandshakeException) { return new(false, "TLS 握手失败：请检查端口、连接加密方式和服务器证书。"); }
        catch (SmtpSenderAddressException) { return new(false, "发件邮箱地址无效，请检查并重新保存 SMTP 配置。"); }
        catch (SmtpCommandException error) { return new(false, $"SMTP 服务器拒绝发送（{(int)error.StatusCode} / {error.ErrorCode}）：请检查发件权限和收件邮箱。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "测试发送超时：请检查 SMTP 地址、端口和网络连接。"); }
        catch (Exception error) when (error is SocketException or IOException or SmtpProtocolException)
        { return new(false, "无法连接或完成 SMTP 通信：请检查服务器地址、端口、网络和加密方式。"); }
    }
}
