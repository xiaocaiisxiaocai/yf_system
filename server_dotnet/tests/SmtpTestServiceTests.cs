using System.Net.Sockets;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class SmtpTestServiceTests
{
    [Fact(Timeout = 180_000)]
    public async Task ManualTestsWriteMaskedActorAuditAndAppearInMailStatusWithoutQueueOrRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        scope.Options.Smtp = new SmtpOptions { Host = "smtp.example.invalid", Port = 587, Username = "sender@example.invalid", From = "sender@example.invalid", Password = "private-test-secret", Security = "StartTls" };
        var audit = new AuditService([]);
        var settings = new SmtpSettingsService(scope.Database, scope.Options, audit);
        var actor = new CurrentUser(1, "admin", "INTERNAL", null);
        var blockedDelivery = new Delivery(null);
        using (var blocked = new SmtpTestService(scope.Database, settings, audit, NullLogger<SmtpTestService>.Instance, blockedDelivery))
            await Assert.ThrowsAsync<ApiException>(() => blocked.TestAsync(new("recipient@example.invalid"), actor, ct));
        Assert.Equal(0, blockedDelivery.Calls);
        await scope.ExecuteAsync("UPDATE users SET must_change_password=0 WHERE id=1", null, ct);
        foreach (var failure in new Exception?[] { null, new MailKit.Security.AuthenticationException("private-test-secret"), new SocketException() })
        {
            var delivery = new Delivery(failure);
            using var service = new SmtpTestService(scope.Database, settings, audit, NullLogger<SmtpTestService>.Instance, delivery);
            var result = await service.TestAsync(new("recipient@example.invalid"), actor, ct);
            Assert.Equal(failure is null, result.Success);
            Assert.Equal(failure is SocketException, result.DeliveryUncertain);
            Assert.Equal(1, delivery.Calls);
        }
        using (var interruptedRequest = new CancellationTokenSource())
        {
            var interruptedDelivery = new Delivery(new OperationCanceledException(), () => interruptedRequest.Cancel());
            using var interrupted = new SmtpTestService(scope.Database, settings, audit, NullLogger<SmtpTestService>.Instance, interruptedDelivery);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.TestAsync(new("recipient@example.invalid"), actor, interruptedRequest.Token));
            Assert.Equal(1, interruptedDelivery.Calls);
        }
        await using var connection = await scope.Database.OpenAsync(ct);
        var logs = (await connection.QueryAsync<(string Action, ulong UserId, string Detail)>("SELECT action, user_id AS UserId, detail FROM audit_logs WHERE action LIKE 'EMAIL_TEST_%' ORDER BY id")).ToArray();
        Assert.Equal(["EMAIL_TEST_STARTED", "EMAIL_TEST_SENT", "EMAIL_TEST_STARTED", "EMAIL_TEST_FAILED", "EMAIL_TEST_STARTED", "EMAIL_TEST_UNKNOWN", "EMAIL_TEST_STARTED", "EMAIL_TEST_UNKNOWN"], logs.Select(log => log.Action));
        Assert.All(logs, log => { Assert.Equal(1UL, log.UserId); Assert.Contains("r***@example.invalid", log.Detail); Assert.DoesNotContain("private-test-secret", log.Detail); Assert.DoesNotContain("recipient@example.invalid", log.Detail); });
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM email_outbox"));
        await connection.DisposeAsync();
        var status = await new MailService(scope.Database, scope.Options, audit, NullLogger<MailService>.Instance).StatusAsync(ct);
        Assert.Equal(8, status.Recent.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("one@example.invalid,two@example.invalid")]
    [InlineData("Name <one@example.invalid>")]
    [InlineData("one@example.invalid\r\nBcc: two@example.invalid")]
    public void TestRecipientMustBeOnePlainMailbox(string value) =>
        Assert.Throws<ApiException>(() => SmtpTestService.ValidateRecipient(value));

    [Fact]
    public async Task TestSendsToTheSelectedMailboxAndReportsServerAcceptance()
    {
        var delivery = new Delivery(null);
        var options = new SmtpOptions { Host = "smtp.example.invalid", Port = 587, Username = "sender@example.invalid", From = "sender@example.invalid", Password = "secret", Security = "StartTls" };
        var result = await SmtpTestService.SendAsync(delivery, options, "recipient@example.invalid", TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Contains("服务器已接受", result.Message);
        Assert.Equal("recipient@example.invalid", delivery.Envelope!.Recipient);
        Assert.Contains("测试邮件", delivery.Envelope.Subject);
        Assert.DoesNotContain("secret", delivery.Envelope.Body);
        Assert.Same(options, delivery.Options);
    }

    [Fact(Timeout = 180_000)]
    public async Task ConnectionCheckReportsEachStepAuditsOnceAndNeverSendsMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        scope.Options.Smtp = new SmtpOptions { Host = "smtp.example.invalid", Port = 587, Username = "sender@example.invalid", From = "sender@example.invalid", Password = "private-test-secret", Security = "StartTls" };
        await scope.ExecuteAsync("UPDATE users SET must_change_password=0 WHERE id=1", null, ct);
        var audit = new AuditService([]);
        var settings = new SmtpSettingsService(scope.Database, scope.Options, audit);
        var actor = new CurrentUser(1, "admin", "INTERNAL", null);
        var delivery = new Delivery(null);
        var diagnoser = new Diagnoser([
            new("连接", "OK", 12, "已连接 smtp.example.invalid:587"),
            new("加密", "OK", 30, "Tls13；服务器支持的登录方式：LOGIN"),
            new("登录", "FAILED", 40, "服务器中途断开连接（ConnectionReset）"),
        ]);
        using var service = new SmtpTestService(scope.Database, settings, audit, NullLogger<SmtpTestService>.Instance, delivery, diagnoser);

        var result = await service.DiagnoseAsync(actor, ct);
        Assert.False(result.Success);
        Assert.Equal(["连接", "加密", "登录"], result.Steps.Select(step => step.Name));
        Assert.Equal(1, diagnoser.Calls);
        Assert.Equal("private-test-secret", diagnoser.Options!.Password);
        Assert.Equal(0, delivery.Calls);

        await using var connection = await scope.Database.OpenAsync(ct);
        var detail = await connection.QuerySingleAsync<string>("SELECT detail FROM audit_logs WHERE action='EMAIL_TEST_DIAGNOSE'");
        Assert.Contains("登录", detail);
        Assert.DoesNotContain("private-test-secret", detail);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM email_outbox"));
        await connection.DisposeAsync();
        var status = await new MailService(scope.Database, scope.Options, audit, NullLogger<MailService>.Instance).StatusAsync(ct);
        var row = Assert.Single(status.Recent, item => item.Action == "EMAIL_TEST_DIAGNOSE");
        Assert.Equal("登录", row.Detail["failedStep"]?.ToString());
    }

    [Fact]
    public void ConnectionCheckFailuresAreExplainedWithoutServerText()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = MailKitSmtpDiagnoser.Explain("连接", new SocketException((int)SocketError.ConnectionRefused), ct);
        Assert.Contains("无法连接（ConnectionRefused）", refused);
        var dropped = MailKitSmtpDiagnoser.Explain("登录", new MailKit.Net.Smtp.SmtpProtocolException("421 4.7.66 secret=abc",
            new IOException("reset", new SocketException((int)SocketError.ConnectionReset))), ct);
        Assert.Equal("服务器中途断开连接（ConnectionReset，4.7.66）", dropped);
        var auth = MailKitSmtpDiagnoser.Explain("登录", new MailKit.Security.AuthenticationException("535 5.7.3 user credentials were incorrect secret=abc"), ct);
        Assert.Contains("认证失败（5.7.3，账号或密码错误", auth);
        Assert.DoesNotContain("secret", dropped + auth);
        Assert.Contains("20 秒内无响应", MailKitSmtpDiagnoser.Explain("加密", new OperationCanceledException(), ct));
    }

    private sealed class Diagnoser(IReadOnlyList<SmtpDiagnosisStep> steps) : ISmtpDiagnoser
    {
        public int Calls { get; private set; }
        public SmtpOptions? Options { get; private set; }
        public Task<IReadOnlyList<SmtpDiagnosisStep>> RunAsync(SmtpOptions options, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
        {
            Calls++;
            Options = options;
            return Task.FromResult(steps);
        }
    }

    [Fact]
    public async Task AuthenticationAndNetworkFailuresAreUsefulAndNeverExposeServerSecrets()
    {
        var auth = await SmtpTestService.SendAsync(new Delivery(new MailKit.Security.AuthenticationException("credential=secret")), new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.False(auth.Success);
        Assert.Contains("认证失败", auth.Message);
        Assert.DoesNotContain("secret", auth.Message);
        var blocked = await SmtpTestService.SendAsync(new Delivery(new MailKit.Security.AuthenticationException(
            "5.7.139 Authentication unsuccessful, user is locked by your organization's security defaults policy. secret=abc")),
            new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.Contains("5.7.139", blocked.Message);
        Assert.Contains("安全默认值", blocked.Message);
        Assert.DoesNotContain("secret", blocked.Message);
        Assert.DoesNotContain("locked", blocked.Message);
        Assert.Equal("（5.7.3，账号或密码错误；已开启多重验证时需使用应用密码）",
            SmtpTestService.AuthenticationDiagnosis("535 5.7.3 Authentication unsuccessful: user credentials were incorrect"));
        Assert.Equal("（5.7.139，该邮箱已禁用 SMTP AUTH，需管理员在 Exchange 中开启）",
            SmtpTestService.AuthenticationDiagnosis("5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Mailbox."));
        Assert.Equal("", SmtpTestService.AuthenticationDiagnosis("credential=secret"));
        // A disconnect during login reports the step on screen; the log line keeps only types and status codes.
        var dropped = new MailKit.Net.Smtp.SmtpProtocolException("421 4.7.66 secret=abc server closed",
            new IOException("reset", new SocketException((int)SocketError.ConnectionReset)));
        SmtpStage.Tag(dropped, SmtpStage.Authenticate);
        var stageResult = await SmtpTestService.SendAsync(new Delivery(dropped), new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.Contains("在登录阶段中断", stageResult.Message);
        var diagnostic = SmtpStage.Describe(dropped);
        Assert.Contains("stage=登录", diagnostic);
        Assert.Contains("SmtpProtocolException", diagnostic);
        Assert.Contains("SocketException(ConnectionReset)", diagnostic);
        Assert.Contains("enhanced=4.7.66", diagnostic);
        Assert.DoesNotContain("secret", diagnostic);
        Assert.DoesNotContain("server closed", diagnostic);
        var network = await SmtpTestService.SendAsync(new Delivery(new SocketException()), new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.False(network.Success);
        Assert.Contains("连接", network.Message);
        Assert.Contains("结果不确定", network.Message);
    }

    private sealed class Delivery(Exception? failure, Action? beforeSend = null) : ISmtpDelivery
    {
        public int Calls { get; private set; }
        public SmtpEnvelope? Envelope { get; private set; }
        public SmtpOptions? Options { get; private set; }
        public Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
        {
            Calls++;
            beforeSend?.Invoke();
            Options = options;
            Envelope = envelope;
            return failure is null ? Task.FromResult(new SmtpDeliveryResult("cleanup-warning")) : Task.FromException<SmtpDeliveryResult>(failure);
        }
    }
}
