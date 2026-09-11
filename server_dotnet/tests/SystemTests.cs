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
    [InlineData("notify.enabled", "yes")]
    [InlineData("upload.allowed_exts", "pdf,../exe")]
    [InlineData("security.management_lock", "anything")]
    public void UnsafeConfigValuesAreRejected(string key, string value)
        => Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, value));

    [Fact]
    public void ExtensionsAreNormalizedAndDeduplicated()
        => Assert.Equal("jpg,pdf", SystemService.NormalizeConfig("upload.allowed_exts", " PDF,jpg,pdf "));

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
}
