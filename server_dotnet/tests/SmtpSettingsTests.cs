using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class SmtpSettingsTests
{
    [Fact]
    public void OmittedSmtpSectionBindsToDisabledFallbackAndPassesStartupValidation()
    {
        var json = JsonSerializer.Serialize(new
        {
            App = new
            {
                ConnectionString = "Server=127.0.0.1;Database=fixture;User ID=fixture",
                JwtSecret = "smtp-omitted-section-private-key-for-tests",
                StorageRoot = Path.Combine(Path.GetTempPath(), "yf-smtp-config-fixture"),
                WebBaseUrl = "https://fixture.example.invalid",
            },
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = configuration.GetSection("App").Get<AppOptions>()!;
        options.Validate();
        Assert.False(options.Smtp.IsConfigured);
        Assert.Empty(options.Smtp.Password);
    }

    [Fact]
    public void CredentialEncryptionSurvivesNewServiceInstanceAndRejectsTampering()
    {
        var options = new AppOptions { JwtSecret = "smtp-settings-private-application-key-for-tests" };
        var first = new SmtpSettingsService(null!, options, new AuditService([]));
        var second = new SmtpSettingsService(null!, options, new AuditService([]));
        const string secret = "fixture-mail-authorization-code";
        var encrypted = first.Protect(secret);
        Assert.DoesNotContain(secret, encrypted);
        Assert.NotEqual(encrypted, first.Protect(secret));
        Assert.Equal(secret, second.Unprotect(encrypted));
        var bytes = Convert.FromBase64String(encrypted[3..]);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect("v1." + Convert.ToBase64String(bytes)));
        var rotated = new SmtpSettingsService(null!, new AppOptions { JwtSecret = "another-private-application-key-for-tests" }, new AuditService([]));
        Assert.ThrowsAny<CryptographicException>(() => rotated.Unprotect(encrypted));
    }

    [Theory]
    [InlineData("smtp.example.invalid", 465, "SslOnConnect")]
    [InlineData("smtp.example.invalid", 587, "StartTls")]
    [InlineData("127.0.0.1", 2525, "Auto")]
    public void NormalizationPreservesAuthorizationCode(string host, int port, string security)
    {
        var cfg = SmtpSettingsService.Normalize(new(host, port, " sender@example.invalid ", " notice@example.invalid ", security, null), " code with spaces ");
        Assert.Equal("sender@example.invalid", cfg.Username);
        Assert.Equal("notice@example.invalid", cfg.From);
        Assert.Equal(" code with spaces ", cfg.Password);
        Assert.Equal(security, cfg.Security);
        Assert.Equal(port, cfg.Port);
    }

    [Theory]
    [InlineData("https://smtp.example.invalid", 465, "Auto")]
    [InlineData("smtp.example.invalid/path", 465, "Auto")]
    [InlineData("smtp.example.invalid", 0, "Auto")]
    [InlineData("smtp.example.invalid", 65536, "Auto")]
    [InlineData("smtp.example.invalid", 465, "None")]
    public void InvalidConnectionSettingsAreRejected(string host, int port, string security)
        => Assert.Throws<ApiException>(() => SmtpSettingsService.Normalize(new(host, port, "sender@example.invalid", "sender@example.invalid", security, null), "secret"));

    [Fact]
    public void PublicViewAndGenericConfigDoNotExposeCredential()
    {
        var resolved = new ResolvedSmtpSettings(new SmtpOptions { Host = "smtp.example.invalid", Password = "authorization-code" }, false);
        var json = JsonSerializer.Serialize(resolved.View, TestJson.Web);
        Assert.DoesNotContain("authorization-code", json);
        Assert.DoesNotContain("\"password\":", json);
        Assert.True(resolved.View.HasPassword);
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(" MAIL.SMTP ", "{}"));
        Assert.Throws<ApiException>(() => SmtpSettingsService.Normalize(new("smtp.example.invalid", 465, "sender@example.invalid", "sender@example.invalid", "Auto", null), ""));
    }
}
