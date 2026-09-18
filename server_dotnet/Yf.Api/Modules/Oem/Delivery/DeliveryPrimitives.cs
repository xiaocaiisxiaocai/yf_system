using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Delivery;

/// <summary>
/// Set of delivered byte ranges (inclusive bounds) with overlap/adjacency merging. A
/// download counts as complete only when the merged set covers the whole file, so
/// repeated or overlapping Range requests never inflate the delivered byte count.
/// </summary>
public sealed class ByteRangeSet
{
    private readonly List<(ulong Start, ulong End)> ranges = [];

    public ByteRangeSet(IEnumerable<(ulong Start, ulong End)>? initial = null)
    {
        foreach (var (start, end) in initial ?? []) Add(start, end);
    }

    public IReadOnlyList<(ulong Start, ulong End)> Ranges => ranges;

    public void Add(ulong start, ulong end)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        var merged = (Start: start, End: end);
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var existing = ranges[i];
            // Overlapping or directly adjacent ranges merge into one.
            if (existing.Start <= (merged.End == ulong.MaxValue ? merged.End : merged.End + 1)
                && merged.Start <= (existing.End == ulong.MaxValue ? existing.End : existing.End + 1))
            {
                merged = (Math.Min(existing.Start, merged.Start), Math.Max(existing.End, merged.End));
                ranges.RemoveAt(i);
            }
        }
        ranges.Add(merged);
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
    }

    public bool Covers(ulong size) => size > 0 && ranges.Count == 1 && ranges[0].Start == 0 && ranges[0].End >= size - 1;
}

/// <summary>A single requested byte range; multi-range requests are refused.</summary>
public readonly record struct ByteRange(ulong Start, ulong End)
{
    public ulong Length => End - Start + 1;

    /// <summary>Parses an HTTP Range header against a file size (null header means the whole file).</summary>
    public static ByteRange? Parse(string? header, ulong size, out bool unsatisfiable)
    {
        unsatisfiable = false;
        if (string.IsNullOrWhiteSpace(header)) return new ByteRange(0, size - 1);
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(','))
        {
            unsatisfiable = true;
            return null;
        }
        var spec = value[6..].Split('-', 2);
        if (spec.Length != 2) { unsatisfiable = true; return null; }
        ulong start, end;
        if (spec[0].Length == 0)
        {
            // Suffix range: the last N bytes.
            if (!ulong.TryParse(spec[1], out var suffix) || suffix == 0) { unsatisfiable = true; return null; }
            start = suffix >= size ? 0 : size - suffix;
            end = size - 1;
        }
        else
        {
            if (!ulong.TryParse(spec[0], out start)) { unsatisfiable = true; return null; }
            if (spec[1].Length == 0) end = size - 1;
            else if (ulong.TryParse(spec[1], out var parsed)) end = Math.Min(parsed, size - 1);
            else { unsatisfiable = true; return null; }
        }
        if (start >= size || end < start) { unsatisfiable = true; return null; }
        return new ByteRange(start, end);
    }
}

public sealed record DownloadGrant(string Realm, ulong ActorId, string LoginSessionId, string DownloadSessionId, ulong FileId, string StoredName, long ExpiresAt);

/// <summary>
/// Signed, short-lived download credential carried in an HttpOnly cookie scoped to a
/// single file's download path, so large files can be fetched by the browser natively
/// without putting a token in the URL. Every request still re-validates the live
/// account, session, permissions and file state; the grant only proves intent.
/// </summary>
public sealed class OemDownloadGrantService(AppOptions options)
{
    private const string TokenType = "YF-OEM-DOWNLOAD";
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.JwtSecret);

    public static string CookieName(ulong fileId) => "oem_dl_" + fileId;
    public static string CookiePath(ulong fileId) => $"/api/v1/oem/files/{fileId}/download";

    public string Issue(DownloadGrant grant)
    {
        var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "HS256", typ = TokenType }));
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new
        {
            rlm = grant.Realm, uid = grant.ActorId, sid = grant.LoginSessionId, dsid = grant.DownloadSessionId,
            fid = grant.FileId, sto = grant.StoredName, exp = grant.ExpiresAt,
        }));
        var input = header + "." + payload;
        using var hmac = new HMACSHA256(key);
        return input + "." + Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(input)));
    }

    public DownloadGrant Parse(string token, ulong fileId, DateTimeOffset now)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw Invalid();
            using var hmac = new HMACSHA256(key);
            var expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            var actual = Base64UrlEncoder.DecodeBytes(parts[2]);
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected)) throw Invalid();
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            if (header.RootElement.GetProperty("typ").GetString() != TokenType) throw Invalid();
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            var root = payload.RootElement;
            var grant = new DownloadGrant(
                root.GetProperty("rlm").GetString()!, root.GetProperty("uid").GetUInt64(), root.GetProperty("sid").GetString()!,
                root.GetProperty("dsid").GetString()!, root.GetProperty("fid").GetUInt64(), root.GetProperty("sto").GetString()!,
                root.GetProperty("exp").GetInt64());
            if (grant.FileId != fileId || grant.ExpiresAt <= now.ToUnixTimeSeconds()) throw Invalid();
            return grant;
        }
        catch (ApiException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw Invalid();
        }
    }

    private static ApiException Invalid() => ApiException.Unauthorized("下载凭证无效或已过期，请重新发起下载");
}

/// <summary>Per-account request budget for downloads (sliding one-minute window, in memory: single node).</summary>
public sealed class OemDownloadRateLimiter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Start, int Count)> windows = new();

    public bool Allow(string realm, ulong actorId, int perMinute)
    {
        var now = DateTime.UtcNow;
        var key = realm + ":" + actorId;
        if (windows.Count > 10_000)
            foreach (var stale in windows.Where(item => now - item.Value.Start > TimeSpan.FromMinutes(1)).ToArray())
                windows.TryRemove(stale.Key, out _);
        var window = windows.AddOrUpdate(key, _ => (now, 1),
            (_, old) => now - old.Start >= TimeSpan.FromMinutes(1) ? (now, 1) : (old.Start, old.Count + 1));
        return window.Count <= perMinute;
    }
}
