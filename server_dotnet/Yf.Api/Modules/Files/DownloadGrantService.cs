using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

/// <summary>
/// In-memory, single-instance download grants. A process restart safely invalidates every outstanding
/// grant/session. The URL carries only a public lookup handle; the bearer secret remains in an
/// HttpOnly, same-site cookie scoped to that one download URL.
/// </summary>
public sealed class DownloadGrantService
{
    internal const int GrantLifetimeSeconds = 60;
    internal const int SessionLifetimeSeconds = 15 * 60;
    private const int MaximumEntries = 4096;
    private const int MaximumEntriesPerUser = 32;
    private static readonly TimeSpan AuditWindow = TimeSpan.FromMinutes(2);
    private readonly object gate = new();
    private readonly Dictionary<string, GrantEntry> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionEntry> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, int> entriesByUser = new();
    private readonly Dictionary<string, WindowAuditEntry> auditWindows = new(StringComparer.Ordinal);

    internal DownloadGrantIssue Issue(ulong userId, string authSessionId, IReadOnlyList<ulong> fileIds, bool batch) =>
        Issue(userId, authSessionId, fileIds, batch, DateTimeOffset.UtcNow);

    internal DownloadGrantIssue Issue(
        ulong userId, string authSessionId, IReadOnlyList<ulong> fileIds, bool batch, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(authSessionId) || fileIds.Count == 0)
            throw new ArgumentException("A download grant requires an authenticated session and at least one file.");
        var ids = fileIds.Distinct().ToArray();
        lock (gate)
        {
            CleanupExpired(now);
            if (grants.Count + sessions.Count >= MaximumEntries)
                throw ApiException.TooManyRequests("下载请求繁忙，请稍后重试");
            if (entriesByUser.GetValueOrDefault(userId) >= MaximumEntriesPerUser)
                throw ApiException.TooManyRequests("当前账号下载请求过多，请稍后重试");
            string handle;
            do { handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); }
            while (grants.ContainsKey(handle) || sessions.ContainsKey(handle));
            var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            grants.Add(handle, new(userId, authSessionId, ids, batch, Hash(secret), now.AddSeconds(GrantLifetimeSeconds)));
            entriesByUser[userId] = entriesByUser.GetValueOrDefault(userId) + 1;
            return new(handle, secret, GrantLifetimeSeconds);
        }
    }

    internal DownloadSession Redeem(string handle, string secret) =>
        Redeem(handle, secret, DateTimeOffset.UtcNow);

    internal DownloadSession Redeem(string handle, string secret, DateTimeOffset now)
    {
        lock (gate)
        {
            CleanupExpired(now);
            // Removing before validation makes every grant a single redemption attempt and avoids races.
            if (!grants.Remove(handle, out var grant) || !Matches(grant.SecretHash, secret))
                throw InvalidGrant();
            DecrementUser(grant.UserId);
            var sessionSecret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            var expiresAt = now.AddSeconds(SessionLifetimeSeconds);
            sessions[handle] = new(grant.UserId, grant.AuthSessionId, grant.FileIds, grant.Batch,
                Hash(sessionSecret), expiresAt);
            entriesByUser[grant.UserId] = entriesByUser.GetValueOrDefault(grant.UserId) + 1;
            return new(handle, sessionSecret, grant.UserId, grant.AuthSessionId, grant.FileIds, grant.Batch,
                expiresAt.ToUnixTimeSeconds());
        }
    }

    internal DownloadSession GetSession(string handle, string secret) =>
        GetSession(handle, secret, DateTimeOffset.UtcNow);

    internal DownloadSession GetSession(string handle, string secret, DateTimeOffset now)
    {
        lock (gate)
        {
            CleanupExpired(now);
            if (!sessions.TryGetValue(handle, out var session) || !Matches(session.SecretHash, secret))
                throw InvalidSession();
            return new(handle, secret, session.UserId, session.AuthSessionId, session.FileIds, session.Batch,
                session.ExpiresAt.ToUnixTimeSeconds());
        }
    }

    internal async ValueTask<AuditLease> AcquireSessionAuditAsync(string handle, CancellationToken ct)
    {
        SessionEntry session;
        lock (gate)
        {
            if (!sessions.TryGetValue(handle, out session!))
                throw InvalidSession();
        }
        await session.AuditGate.WaitAsync(ct);
        if (session.Audited)
        {
            session.AuditGate.Release();
            return AuditLease.Noop;
        }
        return new AuditLease(session.AuditGate, () => session.Audited = true);
    }

    internal async ValueTask<AuditLease> AcquireWindowAuditAsync(
        string key, CancellationToken ct, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        WindowAuditEntry entry;
        lock (gate)
        {
            CleanupExpired(now);
            if (!auditWindows.TryGetValue(key, out entry!) || entry.ExpiresAt <= now)
            {
                entry = new(now.Add(AuditWindow));
                auditWindows[key] = entry;
            }
        }
        await entry.AuditGate.WaitAsync(ct);
        if (entry.Audited)
        {
            entry.AuditGate.Release();
            return AuditLease.Noop;
        }
        return new AuditLease(entry.AuditGate, () => entry.Audited = true);
    }

    internal static string GrantCookieName(string handle) => "yf_dlg_" + handle;
    internal static string SessionCookieName(string handle) => "yf_dls_" + handle;
    internal static bool IsValidHandle(string handle) => handle.Length == 32
        && handle.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var handle in grants.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            if (grants.Remove(handle, out var grant)) DecrementUser(grant.UserId);
        }
        foreach (var handle in sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            if (sessions.Remove(handle, out var session)) DecrementUser(session.UserId);
        }
        foreach (var key in auditWindows.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            auditWindows.Remove(key);
        // Audit keys are best-effort deduplication only; cap them independently so they cannot grow without bound.
        if (auditWindows.Count > MaximumEntries)
            foreach (var key in auditWindows.OrderBy(pair => pair.Value.ExpiresAt).Take(auditWindows.Count - MaximumEntries).Select(pair => pair.Key).ToArray())
                auditWindows.Remove(key);
    }

    private static byte[] Hash(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    private void DecrementUser(ulong userId)
    {
        if (!entriesByUser.TryGetValue(userId, out var count)) return;
        if (count <= 1) entriesByUser.Remove(userId);
        else entriesByUser[userId] = count - 1;
    }

    private static bool Matches(byte[] expected, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var actual = Hash(value);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static ApiException InvalidGrant() => ApiException.Unauthorized("下载凭证无效、已使用或已过期");
    private static ApiException InvalidSession() => ApiException.Unauthorized("下载会话无效或已过期，请重新发起下载");

    private sealed record GrantEntry(
        ulong UserId, string AuthSessionId, ulong[] FileIds, bool Batch, byte[] SecretHash, DateTimeOffset ExpiresAt);
    private sealed class SessionEntry(
        ulong userId, string authSessionId, ulong[] fileIds, bool batch, byte[] secretHash, DateTimeOffset expiresAt)
    {
        internal ulong UserId { get; } = userId;
        internal string AuthSessionId { get; } = authSessionId;
        internal ulong[] FileIds { get; } = fileIds;
        internal bool Batch { get; } = batch;
        internal byte[] SecretHash { get; } = secretHash;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal SemaphoreSlim AuditGate { get; } = new(1, 1);
        internal bool Audited { get; set; }
    }

    private sealed class WindowAuditEntry(DateTimeOffset expiresAt)
    {
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal SemaphoreSlim AuditGate { get; } = new(1, 1);
        internal bool Audited { get; set; }
    }
}

internal sealed class AuditLease : IDisposable
{
    internal static AuditLease Noop { get; } = new();
    private readonly SemaphoreSlim? gate;
    private readonly Action? markCompleted;
    private int disposed;

    private AuditLease() { ShouldWrite = false; }

    internal AuditLease(SemaphoreSlim gate, Action markCompleted)
    {
        this.gate = gate;
        this.markCompleted = markCompleted;
        ShouldWrite = true;
    }

    internal bool ShouldWrite { get; }
    internal void Complete()
    {
        if (ShouldWrite) markCompleted!();
    }

    public void Dispose()
    {
        if (gate is not null && Interlocked.Exchange(ref disposed, 1) == 0) gate.Release();
    }
}

internal sealed record DownloadGrantIssue(string Handle, string Secret, int ExpiresInSeconds);
internal sealed record DownloadSession(
    string Handle, string Secret, ulong UserId, string AuthSessionId, IReadOnlyList<ulong> FileIds, bool Batch,
    long ExpiresAt);
