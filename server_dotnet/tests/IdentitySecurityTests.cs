using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class IdentitySecurityTests
{
    [Fact]
    public async Task ExistingWeakPasswordHashStillVerifies()
    {
        const string password = "admin1";
        var phc = await LegacyHashAsync(password);

        Assert.True(await PasswordService.VerifyAsync(password, phc, TestContext.Current.CancellationToken));
        Assert.False(PasswordService.StrongEnough(password));
    }

    [Fact]
    public async Task ExistingPasswordLongerThanNewPolicyStillVerifies()
    {
        const string password = "HistoricalPassword#2025!";
        var phc = await LegacyHashAsync(password);

        Assert.True(password.EnumerateRunes().Count() > 20);
        Assert.True(await PasswordService.VerifyAsync(password, phc, TestContext.Current.CancellationToken));
        Assert.False(PasswordService.StrongEnough(password));
        Assert.False(await PasswordService.VerifyAsync(new string('界', 86), phc, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PasswordPolicyUsesSixToTwentyUnicodeScalarsAndRejectsWeakValues()
    {
        const string five = "😀Q7!x";
        const string six = "😀Q7!xz";
        const string twenty = "😀Ab1!cD2@eF3#gH4$iJ5";

        Assert.Equal(5, five.EnumerateRunes().Count());
        Assert.Equal(6, six.EnumerateRunes().Count());
        Assert.Equal(20, twenty.EnumerateRunes().Count());
        Assert.False(PasswordService.StrongEnough(five));
        Assert.True(PasswordService.StrongEnough(six));
        Assert.True(PasswordService.StrongEnough(twenty));
        Assert.False(PasswordService.StrongEnough(twenty + "K"));
        Assert.False(PasswordService.StrongEnough("Password123456!"));
        Assert.False(PasswordService.StrongEnough("abcdabcdabcd"));
    }

    [Fact]
    public void PasswordChangeAttemptsAreLimitedPerAccountRegardlessOfLoginBudget()
    {
        var limiter = new LoginRateLimiter();
        Assert.All(Enumerable.Range(1, LoginRateLimiter.MaximumPasswordChangeAttempts),
            _ => Assert.True(limiter.AllowPasswordChange(7)));
        Assert.False(limiter.AllowPasswordChange(7));
        Assert.True(limiter.AllowPasswordChange(8));
        Assert.True(limiter.AllowLogin("192.0.2.1", "target"));
    }

    [Fact]
    public void LoginHashQueueUsesTheLoginAndDownloadRateLimitCode()
    {
        var error = PasswordService.LoginBusyException();
        Assert.Equal(429, error.Status);
        Assert.Equal(42902, error.Code);
    }

    [Fact]
    public void LoginRateLimiterKeepsIpAndIpAccountLimits()
    {
        var accountLimiter = new LoginRateLimiter();
        Assert.All(Enumerable.Range(1, 10), _ => Assert.True(accountLimiter.AllowLogin("192.0.2.1", "target")));
        Assert.False(accountLimiter.AllowLogin("192.0.2.1", "target"));
        Assert.True(accountLimiter.AllowLogin("192.0.2.1", "other"));
        Assert.True(accountLimiter.AllowLogin("192.0.2.2", "target"));

        // The rejected 11th attempt still consumes the IP-wide budget. IP limiting must run
        // before the narrower account bucket so account rotation cannot avoid the total cap.
        Assert.All(Enumerable.Range(1, 48), attempt =>
            Assert.True(accountLimiter.AllowLogin("192.0.2.1", "other-" + attempt)));
        Assert.False(accountLimiter.AllowLogin("192.0.2.1", "ip-limit"));

        var ipLimiter = new LoginRateLimiter();
        Assert.All(Enumerable.Range(1, 60), attempt =>
            Assert.True(ipLimiter.AllowLogin("198.51.100.1", "account-" + attempt)));
        Assert.False(ipLimiter.AllowLogin("198.51.100.1", "account-61"));
        Assert.True(ipLimiter.AllowLogin("198.51.100.2", "account-61"));
    }

    [Fact]
    public void LoginRateLimiterGroupsIpv6ByPrefixAndFailsClosedAtCapacity()
    {
        Assert.Equal(LoginRateLimiter.NormalizeIp("2001:db8:1:2::1"),
            LoginRateLimiter.NormalizeIp("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        Assert.NotEqual(LoginRateLimiter.NormalizeIp("2001:db8:1:2::1"),
            LoginRateLimiter.NormalizeIp("2001:db8:1:3::1"));
        Assert.Equal("192.0.2.10", LoginRateLimiter.NormalizeIp("::ffff:192.0.2.10"));

        var limiter = new LoginRateLimiter();
        for (var index = 0; index < LoginRateLimiter.MaximumKeysPerPurpose; index++)
        {
            var address = $"2001:db8:{index >> 16:x}:{index & 0xffff:x}::1";
            Assert.True(limiter.AllowLogin(address, "capacity-user"));
        }
        Assert.False(limiter.AllowLogin("2001:db8:ffff:ffff::1", "new-key"));
        Assert.True(limiter.AllowLogin("2001:db8:0:0::2", "capacity-user"));
    }

    [Fact]
    public void RefreshCookieLifetimeCannotOutliveTheRefreshRowOrConfiguredTtl()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(12),
            IdentityModule.RefreshCookieMaxAge(now.AddHours(12), now, 7));
        Assert.Equal(TimeSpan.FromDays(7),
            IdentityModule.RefreshCookieMaxAge(now.AddDays(30), now, 7));
        Assert.Equal(TimeSpan.Zero,
            IdentityModule.RefreshCookieMaxAge(now.AddSeconds(-1), now, 7));
    }

    [Fact]
    public void MissingOriginRejectsAnExplicitCrossSiteBrowserRequestButKeepsNonBrowserClients()
    {
        var crossSite = new DefaultHttpContext();
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(IdentityModule.OriginAllowed(crossSite.Request, "https://yf.example.test"));

        var sameOrigin = new DefaultHttpContext();
        sameOrigin.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.True(IdentityModule.OriginAllowed(sameOrigin.Request, "https://yf.example.test"));
        Assert.True(IdentityModule.OriginAllowed(new DefaultHttpContext().Request, "https://yf.example.test"));
    }

    [Fact]
    public void AccessTokenRoundTripsPersistentSessionId()
    {
        var service = new TokenService(new AppOptions
        {
            JwtSecret = "identity-test-secret-with-at-least-32-bytes",
            AccessTtlMinutes = 30
        });
        var issued = service.IssueAccess(42, "E00042", "session-family");
        var parsed = service.ParseAccess(issued.Token);
        Assert.Equal((ulong)42, parsed.UserId);
        Assert.Equal("E00042", parsed.EmployeeNo);
        Assert.Equal("session-family", parsed.SessionId);

        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(issued.Token.Split('.')[1]));
        Assert.Equal(JsonValueKind.Number, payload.RootElement.GetProperty("uid").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.RootElement.GetProperty("iat").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.RootElement.GetProperty("exp").ValueKind);
        Assert.False(payload.RootElement.TryGetProperty("eno", out _));
        Assert.False(payload.RootElement.TryGetProperty("typ", out _));
    }

    [Fact]
    public void ParsesApiClaimsWithNumericUidAndSessionContract()
    {
        const string secret = "identity-test-secret-with-at-least-32-bytes";
        var service = new TokenService(new AppOptions { JwtSecret = secret, AccessTtlMinutes = 30 });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var body = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            sub = "API001", uid = 18446744073709551614UL, sid = "api-session-family",
            jti = "api-jti", iat = now, exp = now + 1800
        }));
        var signingInput = header + "." + body;
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));

        var parsed = service.ParseAccess(signingInput + "." + signature);
        Assert.Equal(18446744073709551614UL, parsed.UserId);
        Assert.Equal("API001", parsed.EmployeeNo);
        Assert.Equal("api-session-family", parsed.SessionId);
    }

    [Fact]
    public void AccessTokenRejectsAValidSignatureWithANonJwtType()
    {
        const string secret = "identity-test-secret-with-at-least-32-bytes";
        var service = new TokenService(new AppOptions { JwtSecret = secret, AccessTtlMinutes = 30 });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("{\"alg\":\"HS256\",\"typ\":\"MEDIA\"}");
        var body = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            sub = "API001", uid = 42, sid = "api-session-family", iat = now, exp = now + 1800
        }));
        var signingInput = header + "." + body;
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));

        var error = Assert.Throws<ApiException>(() => service.ParseAccess(signingInput + "." + signature));
        Assert.Equal(40101, error.Code);
    }

    private static async Task<string> LegacyHashAsync(string password)
    {
        var salt = Encoding.ASCII.GetBytes("historical-salt!");
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = 4096, Iterations = 3, DegreeOfParallelism = 1
        };
        var digest = await argon.GetBytesAsync(32);
        return $"$argon2id$v=19$m=4096,t=3,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(digest).TrimEnd('=')}";
    }
}
