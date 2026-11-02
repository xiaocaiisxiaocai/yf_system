using System.Net.Sockets;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class SmtpTestServiceTests
{
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

    private sealed class Delivery(Exception? failure) : ISmtpDelivery
    {
        public SmtpEnvelope? Envelope { get; private set; }
        public SmtpOptions? Options { get; private set; }
        public Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
        {
            Options = options;
            Envelope = envelope;
            return failure is null ? Task.FromResult(new SmtpDeliveryResult("cleanup-warning")) : Task.FromException<SmtpDeliveryResult>(failure);
        }
    }
}
