using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class IdentitySecurityTests
{
    [Fact]
    public async Task ExistingWeakPasswordHashStillVerifies()
    {
        var password = "Old123";
        var salt = Encoding.ASCII.GetBytes("historical-salt!");
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = 4096, Iterations = 3, DegreeOfParallelism = 1
        };
        var digest = await argon.GetBytesAsync(32);
        var phc = $"$argon2id$v=19$m=4096,t=3,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(digest).TrimEnd('=')}";

        Assert.True(await PasswordService.VerifyAsync(password, phc, TestContext.Current.CancellationToken));
        Assert.False(PasswordService.StrongEnough(password));
    }

    [Fact]
    public void PasswordPolicyCountsUnicodeScalarsAndCapsUtf8Bytes()
    {
        var boundary = string.Concat(Enumerable.Repeat("😀abcdeFG", 8));
        Assert.Equal(64, boundary.EnumerateRunes().Count());
        Assert.True(PasswordService.StrongEnough(boundary));
        Assert.False(PasswordService.StrongEnough(boundary + "x"));
        Assert.False(PasswordService.StrongEnough("Password123456!"));
        Assert.False(PasswordService.StrongEnough("abcdabcdabcd"));
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
    public void CaptchaIsPngAndSingleUse()
    {
        var observer = new RecordingCaptchaObserver();
        var service = new CaptchaService([observer]);
        var response = service.Issue("127.0.0.1");
        Assert.StartsWith("data:image/png;base64,", response.Svg);
        var png = Convert.FromBase64String(response.Svg["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(response.CaptchaId, observer.CaptchaId);
        Assert.Equal(6, observer.Answer.Length);
        Assert.All(observer.Answer, value => Assert.Contains(value, "ACDEFHJKLMNPRTUVWXY347"));
        Assert.Contains(observer.Answer, char.IsAsciiLetterUpper);
        Assert.True(service.Verify(response.CaptchaId, observer.Answer));
        Assert.False(service.Verify(response.CaptchaId, observer.Answer));
    }

    private sealed class RecordingCaptchaObserver : ICaptchaChallengeObserver
    {
        public string CaptchaId { get; private set; } = "";
        public string Answer { get; private set; } = "";
        public void OnIssued(string captchaId, string answer) => (CaptchaId, Answer) = (captchaId, answer);
    }
}
