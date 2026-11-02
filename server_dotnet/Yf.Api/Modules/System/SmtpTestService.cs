using System.Net.Mail;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

public sealed record SmtpTestRequest(string Recipient);
public sealed record SmtpTestResponse(bool Success, string Message, bool DeliveryUncertain = false);

public sealed class SmtpTestService : IDisposable
{
    private readonly AppDb db;
    private readonly SmtpSettingsService settings;
    private readonly AuditService audit;
    private readonly ILogger<SmtpTestService> logger;
    private readonly ISmtpDelivery delivery;
    private readonly SemaphoreSlim sending = new(1, 1);
    public SmtpTestService(AppDb db, SmtpSettingsService settings, AuditService audit, ILogger<SmtpTestService> logger)
        : this(db, settings, audit, logger, new MailKitSmtpDelivery()) { }

    internal SmtpTestService(AppDb db, SmtpSettingsService settings, AuditService audit, ILogger<SmtpTestService> logger, ISmtpDelivery delivery)
    {
        this.db = db;
        this.settings = settings;
        this.audit = audit;
        this.logger = logger;
        this.delivery = delivery;
    }
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
                if (!resolved.Configured) throw ApiException.BadRequest("请先填写并保存完整的 SMTP 配置；授权码无法读取时请重新填写");
                await audit.WriteAsync(connection, null, actor.Id, "EMAIL_TEST_STARTED", "system_config", null,
                    new { recipient = MailService.MaskEmail(recipient), reason = "管理员手动测试，立即发送，不入队且不自动重试" }, null, ct);
            }
            SmtpTestResponse result;
            try { result = await SendAsync(delivery, resolved.Options, recipient, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await RecordResultAsync(actor, recipient, new(false, "测试请求中断，发送结果不确定，请检查收件箱后再决定是否重试", true));
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning("SMTP test ended unexpectedly ({ErrorType}).", error.GetType().Name);
                result = new(false, "测试发送未完成，结果不确定，请检查收件箱后再决定是否重试", true);
            }
            var recorded = await RecordResultAsync(actor, recipient, result);
            return recorded ? result : result with { Message = result.Message + " 本次结果日志写入失败，请勿仅因此重复发送。" };
        }
        finally { sending.Release(); }
    }

    private async Task<bool> RecordResultAsync(CurrentUser actor, string recipient, SmtpTestResponse result)
    {
        // A disconnect or audit failure after acceptance must never trigger another SMTP send.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = await db.OpenAsync(completion.Token);
            var action = result.Success ? "EMAIL_TEST_SENT" : result.DeliveryUncertain ? "EMAIL_TEST_UNKNOWN" : "EMAIL_TEST_FAILED";
            await audit.WriteAsync(connection, null, actor.Id, action, "system_config", null,
                new { recipient = MailService.MaskEmail(recipient), reason = result.Message }, null, completion.Token);
            return true;
        }
        catch (Exception error)
        {
            logger.LogWarning("SMTP test outcome audit could not be saved ({ErrorType}).", error.GetType().Name);
            return false;
        }
    }

    internal static string ValidateRecipient(string? value)
    {
        var recipient = value?.Trim() ?? "";
        if (recipient.Length > 320 || recipient.Any(char.IsControl) || !MailAddress.TryCreate(recipient, out var address) || address.Address != recipient)
            throw ApiException.BadRequest("请输入一个有效的测试收件邮箱");
        return recipient;
    }

    /// <summary>
    /// The server's rejection text is never echoed (it may carry arbitrary content); only its SMTP status code and
    /// a fixed explanation for well-known Microsoft 365 reasons are shown, e.g. "（5.7.139，被组织安全默认值阻止）".
    /// </summary>
    internal static string AuthenticationDiagnosis(string? serverText)
    {
        var text = serverText ?? "";
        var code = Regex.Match(text, @"\b[45]\.\d{1,3}\.\d{1,3}\b").Value;
        if (code.Length == 0) code = Regex.Match(text, @"\b5\d\d\b").Value;
        var reason = text switch
        {
            _ when text.Contains("security defaults", StringComparison.OrdinalIgnoreCase) => "被组织的安全默认值阻止，需管理员为该账号放行 SMTP AUTH",
            _ when text.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase) => "该邮箱已禁用 SMTP AUTH，需管理员在 Exchange 中开启",
            _ when text.Contains("did not meet the criteria", StringComparison.OrdinalIgnoreCase)
                || text.Contains("conditional access", StringComparison.OrdinalIgnoreCase) => "被组织的登录策略（如条件访问、受信任位置）阻止",
            _ when text.Contains("credentials were incorrect", StringComparison.OrdinalIgnoreCase)
                || text.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase) => "账号或密码错误；已开启多重验证时需使用应用密码",
            _ => "",
        };
        var parts = new[] { code, reason }.Where(part => part.Length > 0).ToArray();
        return parts.Length == 0 ? "" : "（" + string.Join("，", parts) + "）";
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
        catch (MailKit.Security.AuthenticationException error) { return new(false, "SMTP 认证失败" + AuthenticationDiagnosis(error.Message) + "：请检查登录账号、密码或授权码，以及邮箱是否允许 SMTP AUTH；当前支持账号密码认证。"); }
        catch (SslHandshakeException) { return new(false, "TLS 握手失败：请检查端口、连接加密方式和服务器证书。"); }
        catch (SmtpSenderAddressException) { return new(false, "发件邮箱地址无效，请检查并重新保存 SMTP 配置。"); }
        catch (SmtpCommandException error) { return new(false, $"SMTP 服务器拒绝发送（{(int)error.StatusCode} / {error.ErrorCode}）：请检查发件权限和收件邮箱。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "测试发送超时，结果不确定：请先检查收件箱，再检查 SMTP 地址、端口和网络连接。", true); }
        catch (Exception error) when (error is SocketException or IOException or SmtpProtocolException)
        { return new(false, "无法连接或完成 SMTP 通信，发送结果不确定：请检查服务器地址、端口、网络和加密方式；如已进入发送阶段，请先检查收件箱。", true); }
    }
}
