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

    [Fact]
    public async Task AuthenticationAndNetworkFailuresAreUsefulAndNeverExposeServerSecrets()
    {
        var auth = await SmtpTestService.SendAsync(new Delivery(new MailKit.Security.AuthenticationException("credential=secret")), new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.False(auth.Success);
        Assert.Contains("认证失败", auth.Message);
        Assert.DoesNotContain("secret", auth.Message);
        var network = await SmtpTestService.SendAsync(new Delivery(new SocketException()), new(), "r@example.invalid", TestContext.Current.CancellationToken);
        Assert.False(network.Success);
        Assert.Contains("连接", network.Message);
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
