using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class TokenService(AppOptions options)
{
    public (string Token, long ExpiresAt) IssueAccess(ulong userId, string employeeNo, string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(options.AccessTtlMinutes);
        // Serialize the API's token contract directly so uid/iat/exp remain JSON numbers;
        // Claim(string, string) would encode uid as a JSON string.
        var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var claims = new Dictionary<string, object>
        {
            ["sub"] = employeeNo,
            ["uid"] = userId,
            ["sid"] = sessionId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds()
        };
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.JwtSecret));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return (signingInput + "." + signature, expires.ToUnixTimeSeconds());
    }

    public AccessClaims ParseAccess(string token)
    {
        // Keep canonical JWT claim names so the API can read its own `sub` contract directly.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = false, ValidateAudience = false, ValidateIssuerSigningKey = true,
            IssuerSigningKey = Key(), ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        }, out var validated);
        if (validated is not JwtSecurityToken jwt || !TryReadClaims(jwt.RawPayload, out var claims))
            throw ApiException.Unauthorized("登录状态无效");
        return claims;
    }

    public static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(options.JwtSecret));

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
            claims = new(uid, employeeNo, sessionId, expiresAt);
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
    }
}

public sealed record AccessClaims(ulong UserId, string EmployeeNo, string SessionId, long ExpiresAt);
