using System.Globalization;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

/// <summary>
/// Caches the authenticated actor projection across requests. Every lookup is guarded by a
/// database revision probe; database triggers advance that revision in the same transaction as
/// changes to users, suppliers or refresh tokens.
/// </summary>
public sealed class IdentityProjectionCache
{
    internal const string RevisionConfigKey = "security.identity_revision";
    internal const int DefaultCapacity = 4096;

    private readonly object sync = new();
    private readonly Dictionary<IdentityCacheKey, IdentityProjection> entries = [];
    private readonly int capacity;
    private ulong latestRevision;
    private bool hasRevision;

    public IdentityProjectionCache() : this(DefaultCapacity) { }

    internal IdentityProjectionCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    internal int Count
    {
        get { lock (sync) return entries.Count; }
    }

    internal async Task<IdentityProjection?> ResolveAsync(
        AppDb db, AccessClaims claims, CancellationToken ct)
    {
        RequireInternalRealm(claims);
        await using var connection = await db.OpenAsync(ct);
        var probe = await ProbeAsync(connection, ct);
        return await ResolveAsync(probe, claims,
            token => LoadAsync(connection, claims.UserId, claims.SessionId, token), ct);
    }

    internal async Task<IdentityProjection?> ResolveAsync(
        IdentityRevisionProbe probe,
        AccessClaims claims,
        Func<CancellationToken, Task<IdentityProjection?>> loader,
        CancellationToken ct)
    {
        RequireInternalRealm(claims);
        var key = new IdentityCacheKey(probe.Revision, claims.UserId, claims.SessionId);
        var revisionIsCurrent = Observe(probe.Revision);
        if (revisionIsCurrent && TryGet(key, probe.DatabaseNow, out var cached)) return cached;

        var loaded = await loader(ct);
        if (loaded?.ValidUntil is DateTime validUntil)
        {
            if (validUntil > probe.DatabaseNow) Publish(key, loaded);
            else loaded = loaded with { ValidUntil = null };
        }
        return loaded;
    }

    private static void RequireInternalRealm(AccessClaims claims)
    {
        if (claims.Realm != IdentityRealms.Internal)
            throw new ArgumentException("The shared identity cache only accepts the internal realm.", nameof(claims));
    }

    private bool Observe(ulong revision)
    {
        lock (sync)
        {
            if (!hasRevision || revision > latestRevision)
            {
                latestRevision = revision;
                hasRevision = true;
                entries.Clear();
                return true;
            }

            // A request that probed before a concurrent commit may arrive here after a newer
            // revision was observed. It may finish under normal request/transaction semantics,
            // but it must neither consume nor repopulate the current cache.
            return revision == latestRevision;
        }
    }

    private bool TryGet(IdentityCacheKey key, DateTime databaseNow, out IdentityProjection? projection)
    {
        lock (sync)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                if (cached.ValidUntil is DateTime validUntil && validUntil > databaseNow)
                {
                    projection = cached;
                    return true;
                }
                entries.Remove(key);
            }
        }
        projection = null;
        return false;
    }

    private void Publish(IdentityCacheKey key, IdentityProjection projection)
    {
        lock (sync)
        {
            if (!hasRevision || key.Revision != latestRevision) return;
            if (entries.Count >= capacity && !entries.ContainsKey(key)) entries.Clear();
            entries[key] = projection;
        }
    }

    private static async Task<IdentityRevisionProbe> ProbeAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cfg_value, UTC_TIMESTAMP(6)
            FROM system_configs
            WHERE cfg_key = @key
            """;
        command.Parameters.AddWithValue("@key", RevisionConfigKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Required system configuration '{RevisionConfigKey}' is missing.");
        var rawRevision = reader.GetString(0);
        if (!ulong.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            throw new InvalidOperationException($"System configuration '{RevisionConfigKey}' is invalid.");
        return new(revision, AsUtc(reader.GetDateTime(1)));
    }

    private static async Task<IdentityProjection?> LoadAsync(
        MySqlConnection connection, ulong userId, string sessionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.employee_no, u.user_type, u.supplier_id, u.status,
                   u.must_change_password,
                   CASE WHEN u.supplier_id IS NOT NULL AND s.status = 'ACTIVE' THEN 1 ELSE 0 END,
                   (
                       SELECT MAX(LEAST(rt.expires_at, rt.session_expires_at))
                       FROM refresh_tokens rt
                       WHERE rt.user_id = u.id
                         AND rt.session_id = @sessionId
                         AND rt.revoked = 0
                         AND rt.expires_at > UTC_TIMESTAMP(6)
                         AND rt.session_expires_at > UTC_TIMESTAMP(6)
                   ) AS valid_until
            FROM users u
            LEFT JOIN suppliers s ON s.id = u.supplier_id
            WHERE u.id = @userId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(
            reader.GetUInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetUInt64(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : AsUtc(reader.GetDateTime(7)));
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal readonly record struct IdentityRevisionProbe(ulong Revision, DateTime DatabaseNow);
internal readonly record struct IdentityCacheKey(ulong Revision, ulong UserId, string SessionId);

internal sealed record IdentityProjection(
    ulong Id,
    string EmployeeNo,
    string UserType,
    ulong? SupplierId,
    string Status,
    bool MustChangePassword,
    bool SupplierActive,
    DateTime? ValidUntil)
{
    internal bool SessionActive => ValidUntil.HasValue;
}
