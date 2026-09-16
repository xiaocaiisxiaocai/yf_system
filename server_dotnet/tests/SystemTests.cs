using System.Net;
using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public class SystemTests
{
    [Fact]
    public void BusinessStorageCannotBeUnderPublicApplicationFiles()
    {
        var app = Path.Combine(Path.GetTempPath(), "yf_location_test", "app");
        var options = new AppOptions { StorageRoot = Path.Combine(app, "wwwroot", "uploads") };
        Assert.Throws<InvalidOperationException>(() => options.ValidateStorageLocation(app));
        options.StorageRoot = Path.Combine(Path.GetTempPath(), "yf_location_test", "storage");
        options.ValidateStorageLocation(app);
    }

    [Theory]
    [InlineData("upload.chunk_size", "0")]
    [InlineData("upload.max_file_size", "21474836481")]
    [InlineData("storage.warn_percent", "100")]
    [InlineData("storage.warn_percent", "85")]
    [InlineData(" STORAGE.WARN_PERCENT ", "85")]
    [InlineData("notify.enabled", "yes")]
    [InlineData("notify.internal.enabled", "yes")]
    [InlineData("upload.allowed_exts", "pdf,../exe")]
    [InlineData("security.management_lock", "anything")]
    [InlineData("SECURITY.MANAGEMENT_LOCK", "anything")]
    [InlineData(" security.management_lock ", "anything")]
    [InlineData("security.management_lóck", "anything")]
    [InlineData("UPLOAD.CHUNK_SIZE", "0")]
    public void UnsafeConfigValuesAreRejected(string key, string value)
        => Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, value));

    [Fact]
    public void ExtensionsAreNormalizedAndDeduplicated()
        => Assert.Equal("jpg,pdf", SystemService.NormalizeConfig("upload.allowed_exts", " PDF,jpg,pdf "));

    [Theory]
    [InlineData("notify.enabled")]
    [InlineData("notify.internal.enabled")]
    [InlineData("notify.supplier.enabled")]
    [InlineData("notify.event.message_created")]
    [InlineData("notify.event.file_uploaded")]
    [InlineData("notify.event.project_submitted")]
    [InlineData("notify.event.project_confirmed")]
    [InlineData("notify.event.project_rejected")]
    [InlineData("notify.event.project_withdrawn")]
    public void NotificationConfigValuesAreCanonicalBooleans(string key)
    {
        Assert.Equal("true", SystemService.NormalizeConfig(key, "1"));
        Assert.Equal("false", SystemService.NormalizeConfig(key, "0"));
        Assert.Equal("true", SystemService.NormalizeConfig(key, "TrUe"));
        Assert.Equal("false", SystemService.NormalizeConfig(key, "false"));
    }

    [Fact]
    public async Task UnknownAuditCategoryDoesNotFailOpenToAllLogs()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?category=UNKNOWN");
        var service = new SystemService(null!, new AuditService([]));

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.ListLogsAsync(context.Request, TestContext.Current.CancellationToken));

        Assert.Equal(400, error.Status);
        Assert.Equal("日志分类参数无效", error.Message);
    }

    [Fact]
    public void ForwardedAddressIsTrustedOnlyFromExplicitLoopbackProxy()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        var options = new AppOptions { TrustLoopbackProxy = true };
        Assert.Equal("192.0.2.10", ClientIp.Resolve(context, options));
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.Equal("203.0.113.7", ClientIp.Resolve(context, options));
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7, 198.51.100.8";
        Assert.Equal("127.0.0.1", ClientIp.Resolve(context, options));
    }

    [Fact]
    public void MailDiagnosticsDoNotExposeCredentials()
    {
        Assert.Equal("a***@example.invalid", MailService.MaskEmail("alice@example.invalid"));
        Assert.Equal("SMTP 认证失败", MailService.SanitizeError("authentication failed alice@example.invalid password=my-secret"));
        Assert.Equal("SMTP 连接失败", MailService.SanitizeError("TLS connection smtp.private.invalid"));
    }

    [Fact]
    public void EmptySmtpHostKeepsMailDisabled()
    {
        var options = ValidOptions();
        options.Smtp = new SmtpOptions { Host = "", Port = 0 };

        options.Validate();

        Assert.False(options.Smtp.IsConfigured);
    }

    [Theory]
    [InlineData(" ", 465, "sender@example.invalid", "secret", "sender@example.invalid")]
    [InlineData("smtp.example.invalid", 0, "sender@example.invalid", "secret", "sender@example.invalid")]
    [InlineData("smtp.example.invalid", 65536, "sender@example.invalid", "secret", "sender@example.invalid")]
    [InlineData("smtp.example.invalid", 465, " ", "secret", "sender@example.invalid")]
    [InlineData("smtp.example.invalid", 465, "sender@example.invalid", " ", "sender@example.invalid")]
    [InlineData("smtp.example.invalid", 465, "sender@example.invalid", "secret", "not-an-address")]
    public void InvalidEnabledSmtpConfigurationIsRejected(
        string host,
        int port,
        string username,
        string password,
        string from)
    {
        var options = ValidOptions();
        options.Smtp = new SmtpOptions
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            From = from
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void CompleteSmtpConfigurationIsAccepted()
    {
        var options = ValidOptions();
        options.Smtp = new SmtpOptions
        {
            Host = "smtp.example.invalid",
            Port = 465,
            Username = "sender@example.invalid",
            Password = "secret",
            From = "sender@example.invalid"
        };

        options.Validate();

        Assert.True(options.Smtp.IsConfigured);
    }

    private static AppOptions ValidOptions() => new()
    {
        ConnectionString = "Server=127.0.0.1;Database=yf_system;User ID=test;Password=test",
        StorageRoot = Path.Combine(Path.GetTempPath(), "yf_options_test", "storage"),
        JwtSecret = "test-jwt-secret-with-at-least-32-bytes"
    };
}
