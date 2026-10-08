using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class TokenService(AppOptions options)
{
    internal const string Issuer = "yf-api";
    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
    private readonly ConcurrentDictionary<string, SymmetricSecurityKey> realmKeys = new(StringComparer.Ordinal);

    /// <summary>The audience a realm's access tokens carry and must be presented with.</summary>
    internal static string AudienceFor(string realm) => "yf:" + realm;

    /// <summary>
    /// Each realm signs with its own key derived from App:JwtSecret, so a token minted for one realm
    /// cannot verify in another even if its realm claim is rewritten.
    /// </summary>
    internal static byte[] SigningKeyFor(string secret, string realm) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("yf-access-token/" + realm));

    private SymmetricSecurityKey RealmKey(string realm) =>
        realmKeys.GetOrAdd(realm, value => new SymmetricSecurityKey(SigningKeyFor(options.JwtSecret, value)));
    public (string Token, long ExpiresAt) IssueAccess(ulong userId, string employeeNo, string sessionId) =>
        IssueAccess(userId, employeeNo, sessionId, IdentityRealms.Internal);

    public (string Token, long ExpiresAt) IssueAccess(
        ulong userId, string employeeNo, string sessionId, string realm)
    {
        if (string.IsNullOrWhiteSpace(realm)) throw new ArgumentException("Identity realm is required.", nameof(realm));
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(options.AccessTtlMinutes);
        // Serialize the API's token contract directly so uid/iat/exp remain JSON numbers;
        // Claim(string, string) would encode uid as a JSON string.
        var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = AudienceFor(realm),
            ["sub"] = employeeNo,
            ["uid"] = userId,
            ["sid"] = sessionId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds()
        };
        // The historical internal token has no realm claim. Keeping one canonical
        // encoding also prevents an attacker from presenting an explicit internal realm.
        if (realm != IdentityRealms.Internal) claims["rlm"] = realm;
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(SigningKeyFor(options.JwtSecret, realm));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return (signingInput + "." + signature, expires.ToUnixTimeSeconds());
    }

    public AccessClaims ParseAccess(string token)
    {
        // The realm only selects which key and audience to verify against; a rewritten realm
        // claim fails the signature because the other realm's key signed nothing here.
        var realm = ClaimedRealm(token) ?? throw ApiException.Unauthorized("登录状态无效");
        // Keep canonical JWT claim names so the API can read its own `sub` contract directly.
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Issuer,
            ValidateAudience = true, ValidAudience = AudienceFor(realm),
            ValidateIssuerSigningKey = true, IssuerSigningKey = RealmKey(realm),
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        }, out var validated);
        if (validated is not JwtSecurityToken jwt || !string.Equals(jwt.Header.Typ, "JWT", StringComparison.Ordinal)
            || !TryReadClaims(jwt.RawPayload, out var claims))
            throw ApiException.Unauthorized("登录状态无效");
        return claims;
    }

    /// <summary>Reads the unverified realm claim; null when the payload is malformed.</summary>
    private static string? ClaimedRealm(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("rlm", out var realm)) return IdentityRealms.Internal;
            var value = realm.ValueKind == JsonValueKind.String ? realm.GetString() : null;
            return string.IsNullOrWhiteSpace(value) || value == IdentityRealms.Internal ? null : value;
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static bool TryReadClaims(string rawPayload, out AccessClaims claims)
    {
        claims = default!;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(rawPayload));
            var root = document.RootElement;
            if (!root.TryGetProperty("uid", out var uidValue) || uidValue.ValueKind != JsonValueKind.Number || !uidValue.TryGetUInt64(out var uid) ||
                !root.TryGetProperty(JwtRegisteredClaimNames.Sub, out var subValue) || subValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("sid", out var sidValue) || sidValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty(JwtRegisteredClaimNames.Exp, out var expiresValue) ||
                expiresValue.ValueKind != JsonValueKind.Number || !expiresValue.TryGetInt64(out var expiresAt))
                return false;
            var employeeNo = subValue.GetString();
            var sessionId = sidValue.GetString();
            if (string.IsNullOrWhiteSpace(employeeNo) || string.IsNullOrWhiteSpace(sessionId)) return false;
            var realm = IdentityRealms.Internal;
            if (root.TryGetProperty("rlm", out var realmValue))
            {
                if (realmValue.ValueKind != JsonValueKind.String) return false;
                realm = realmValue.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(realm) || realm == IdentityRealms.Internal) return false;
            }
            claims = new(uid, employeeNo, sessionId, expiresAt, realm);
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
    }
}

public sealed record AccessClaims(
    ulong UserId,
    string EmployeeNo,
    string SessionId,
    long ExpiresAt,
    string Realm = IdentityRealms.Internal);

public static class IdentityRealms
{
    /// <summary>The shared users table realm for internal staff and supplier accounts.</summary>
    public const string Internal = "internal";
}
