using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>Storage location kinds, statuses and migration phases. See 存储路径页面配置与在线迁移方案.</summary>
internal static class StorageKinds
{
    internal const string Collaboration = "COLLAB";
    internal const string Oem = "OEM";
    internal const string Log = "LOG";
    internal static readonly string[] All = [Collaboration, Oem, Log];

    internal static string Normalize(string? kind) => kind?.Trim().ToUpperInvariant() switch
    {
        Collaboration => Collaboration,
        Oem => Oem,
        Log => Log,
        _ => throw ApiException.NotFound("存储位置不存在"),
    };

    internal static string Label(string kind) => kind switch
    {
        Collaboration => "协作文件存储",
        Oem => "OEM 文件存储",
        Log => "日志目录",
        _ => kind,
    };
}

internal static class StorageStatuses
{
    internal const string Idle = "IDLE";
    internal const string Migrating = "MIGRATING";
    internal const string Failed = "FAILED";
}

internal static class StoragePhases
{
    /// <summary>Copies and verifies every file from the old root.</summary>
    internal const string Copying = "COPYING";
    /// <summary>Everything is copied; waits for upload sessions that started in the old root to end.</summary>
    internal const string Waiting = "WAITING";
    /// <summary>Re-verifies each copy and deletes it from the old root.</summary>
    internal const string Deleting = "DELETING";
}

/// <summary>
/// Startup side of the storage locations: seeds missing rows from the configured paths, promotes a pending OEM path,
/// and applies the effective paths (and the old root of a running migration) to <see cref="AppOptions"/>.
/// </summary>
internal static class StorageLocationBootstrap
{
    internal static async Task ApplyAsync(AppOptions options, string applicationRoot, bool seed, CancellationToken ct = default)
    {
        await using var conn = await new AppDb(options).OpenAsync(ct);
        await using var db = EfDb.Use(conn);
        Dictionary<string, StorageLocation> rows;
        try
        {
            rows = await db.StorageLocations.ToDictionaryAsync(row => row.Kind, StringComparer.Ordinal, ct);
        }
        catch (MySqlException error) when (!seed && error.Number == 1146)
        {
            return; // Offline tools on a database that predates storage_locations keep the configured paths.
        }
        if (seed)
        {
            var now = await DbClock.UtcNowAsync(db, ct, 3);
            var configured = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StorageKinds.Collaboration] = Path.GetFullPath(options.StorageRoot),
                [StorageKinds.Oem] = options.OemStorageRoot.Length == 0 ? "" : Path.GetFullPath(options.OemStorageRoot),
                [StorageKinds.Log] = string.IsNullOrWhiteSpace(options.LogDirectory) ? "" : Path.GetFullPath(options.LogDirectory),
            };
            foreach (var (kind, path) in configured)
            {
                if (rows.TryGetValue(kind, out var existing))
                {
                    if (!SamePath(existing.Path, path) && existing.PreviousPath is null)
                        Console.Error.WriteLine(
                            $"Storage location {kind} is managed in the system settings; the configured path is ignored.");
                    continue;
                }
                var row = new StorageLocation { Kind = kind, Path = path, Status = StorageStatuses.Idle, UpdatedAt = now };
                db.StorageLocations.Add(row);
                rows[kind] = row;
            }
            // A first OEM path set on the page only takes effect after a restart: OEM workers start with the process.
            if (rows[StorageKinds.Oem] is { PendingPath: { Length: > 0 } pending } oem && oem.Path.Length == 0)
            {
                oem.Path = pending;
                oem.PendingPath = null;
                oem.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
        }
        foreach (var row in rows.Values) StorageLocationState.Apply(options, row);
        options.ValidateStorageLocation(applicationRoot);
    }

    internal static bool SamePath(string? left, string? right) =>
        string.Equals(
            string.IsNullOrEmpty(left) ? "" : Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            string.IsNullOrEmpty(right) ? "" : Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// <c>--set-storage-path &lt;kind&gt; &lt;path&gt;</c>: points a kind at an existing directory without moving files, for
    /// a site restored onto another server. Refused while the site runs or a migration is unfinished.
    /// </summary>
    internal static async Task SetPathOfflineAsync(AppOptions options, string kindArgument, string pathArgument, CancellationToken ct = default)
    {
        var kind = StorageKinds.Normalize(kindArgument);
        var path = StoragePathPolicy.NormalizeFormat(pathArgument);
        if (!Directory.Exists(path)) throw new InvalidOperationException("The storage directory must already exist.");
        await using var conn = await new AppDb(options).OpenAsync(ct);
        await using (var lease = await AppRunningLease.TryAcquireAsync(conn, ct))
        {
            if (lease is null) throw new InvalidOperationException("Stop the site before changing a storage path offline.");
            await using var db = EfDb.Use(conn);
            var row = await db.StorageLocations.SingleOrDefaultAsync(item => item.Kind == kind, ct)
                ?? throw new InvalidOperationException("Start the site once so the storage locations exist, then retry.");
            if (row.PreviousPath is not null)
                throw new InvalidOperationException("A storage migration is unfinished; complete or cancel it on the settings page first.");
            var previous = row.Path;
            row.Path = path;
            row.PendingPath = null;
            row.Status = StorageStatuses.Idle;
            row.Phase = null;
            row.LastError = null;
            row.UpdatedAt = await DbClock.UtcNowAsync(db, ct, 3);
            await db.SaveChangesAsync(ct);
            await new AuditService([]).WriteAsync(conn, null, null, "STORAGE_PATH_SET", "storage_location", null,
                new { kind, previous, path, source = "cli" }, null, ct);
        }
    }
}

/// <summary>Applies one storage location row to the in-memory options every storage call site reads.</summary>
internal static class StorageLocationState
{
    internal static void Apply(AppOptions options, StorageLocation row)
    {
        var fallback = row.PreviousPath is { Length: > 0 } previous ? previous : null;
        switch (row.Kind)
        {
            case StorageKinds.Collaboration:
                options.StorageRoot = row.Path;
                options.StorageFallbackRoot = fallback;
                break;
            case StorageKinds.Oem:
                options.OemStorageRoot = row.Path;
                options.OemStorageFallbackRoot = row.Path.Length == 0 ? null : fallback;
                break;
            case StorageKinds.Log:
                options.LogDirectory = row.Path;
                break;
        }
    }
}
