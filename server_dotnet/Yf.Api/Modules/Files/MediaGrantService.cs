using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

public sealed class MediaGrantService(AppOptions options)
{
    internal const int LifetimeSeconds = 300;
    private const string TokenType = "YF-MEDIA";
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.JwtSecret);

    public string Issue(ulong userId, string sessionId, ulong fileId) =>
        Issue(userId, sessionId, fileId, DateTimeOffset.UtcNow, LifetimeSeconds);

    internal string Issue(ulong userId, string sessionId, ulong fileId, DateTimeOffset now, int lifetimeSeconds)
    {
        var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "HS256", typ = TokenType }));
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            uid = userId,
            sid = sessionId,
            fid = fileId,
            exp = now.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds()
        }));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(key);
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    public MediaGrant Parse(string token) => Parse(token, DateTimeOffset.UtcNow);

    internal MediaGrant Parse(string token, DateTimeOffset now)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw InvalidGrant();
            using var hmac = new HMACSHA256(key);
            var expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            var actual = Base64UrlEncoder.DecodeBytes(parts[2]);
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
                throw InvalidGrant();

            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            var headerRoot = header.RootElement;
            if (!headerRoot.TryGetProperty("alg", out var algorithm)
                || algorithm.GetString() != "HS256"
                || !headerRoot.TryGetProperty("typ", out var type)
                || type.GetString() != TokenType)
                throw InvalidGrant();

            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            var root = payload.RootElement;
            if (!root.TryGetProperty("uid", out var userValue) || !userValue.TryGetUInt64(out var userId)
                || !root.TryGetProperty("sid", out var sessionValue) || sessionValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("fid", out var fileValue) || !fileValue.TryGetUInt64(out var fileId)
                || !root.TryGetProperty("exp", out var expiresValue) || !expiresValue.TryGetInt64(out var expiresAt))
                throw InvalidGrant();
            var sessionId = sessionValue.GetString();
            if (string.IsNullOrWhiteSpace(sessionId) || expiresAt <= now.ToUnixTimeSeconds()) throw InvalidGrant();
            return new(userId, sessionId, fileId, expiresAt);
        }
        catch (ApiException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw InvalidGrant();
        }
    }

    internal static string CookieName(ulong fileId) => "yf_media_" + fileId;
    private static ApiException InvalidGrant() => ApiException.Unauthorized("媒体预览凭证无效或已过期");
}

public sealed record MediaGrant(ulong UserId, string SessionId, ulong FileId, long ExpiresAt);
