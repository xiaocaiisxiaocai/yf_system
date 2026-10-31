using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

public sealed record StorageLocationResponse(
    string Kind,
    string Label,
    // Empty when the kind is not configured (OEM storage, log directory).
    string Path,
    // Old root still serving reads while a migration runs.
    string? PreviousPath,
    // First OEM path waiting for a restart.
    string? PendingPath,
    // IDLE | MIGRATING | FAILED
    string Status,
    // COPYING | WAITING | DELETING while migrating
    string? Phase,
    long TotalFiles,
    long TotalBytes,
    long CopiedFiles,
    long CopiedBytes,
    long DeletedFiles,
    long SkippedFiles,
    string? LastError,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    // Free space of the disk holding Path; null when it cannot be read.
    long? FreeBytes);

public sealed record StorageLocationsResponse(IReadOnlyList<StorageLocationResponse> Items);

public sealed class StorageMigrationRequest
{
    public string? Path { get; set; }
}

/// <summary>Wakes the migration worker and cancels the copy of a location whose migration was redirected.</summary>
public sealed class StorageMigrationSignal
{
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly object gate = new();
    private CancellationTokenSource? current;

    internal void Wake() => wake.Release();

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await wake.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    internal CancellationTokenSource Begin(CancellationToken ct)
    {
        lock (gate)
        {
            current = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return current;
        }
    }

    internal void End(CancellationTokenSource source)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, source)) current = null;
        }
        source.Dispose();
    }

    /// <summary>Stops the running pass; the worker re-reads the row and continues with the new direction.</summary>
    internal void Interrupt()
    {
        lock (gate)
        {
            try { current?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        Wake();
    }
}

/// <summary>
/// System settings → 存储位置: lists the three storage locations and starts, cancels or retries a migration. The
/// copy itself runs in <see cref="StorageMigrationWorker"/>. See 存储路径页面配置与在线迁移方案.
/// </summary>
public sealed class StorageLocationService(
    AppDb db,
    AppOptions options,
    AuditService audit,
    StorageMigrationSignal signal,
    IWebHostEnvironment environment,
    LogDirectorySink? logSink = null)
{
    internal const string Permission = "storage:manage";

    public async Task<StorageLocationsResponse> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var rows = await context.StorageLocations.AsNoTracking().ToArrayAsync(ct);
        return new(StorageKinds.All.Select(kind => rows.FirstOrDefault(row => row.Kind == kind))
            .Where(row => row is not null).Select(row => ToResponse(row!)).ToArray());
    }

    public async Task<StorageLocationResponse> MigrateAsync(
        CurrentUser actor, string kindArgument, StorageMigrationRequest request, string? ip, CancellationToken ct)
    {
        var kind = StorageKinds.Normalize(kindArgument);
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await LockAsync(conn, tx, actor, ct);
        await using var context = EfDb.Use(conn, tx);
        var rows = await LockRowsAsync(context, ct);
        if (rows.Any(row => row.PreviousPath is not null || row.Status != StorageStatuses.Idle))
            throw ApiException.Conflict("已有存储迁移正在进行，请等待完成或先取消");
        var row = rows.SingleOrDefault(item => item.Kind == kind) ?? throw ApiException.NotFound("存储位置不存在");

        var others = rows.SelectMany(item => new[] { item.Path, item.PreviousPath, item.PendingPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray();
        var required = row.Path.Length == 0 ? 0 : await Task.Run(() => StoragePathPolicy.Measure(row.Path, ct).Bytes, ct);
        var target = StoragePathPolicy.ValidateTarget(
            request.Path, environment.ContentRootPath, environment.WebRootPath, others, required);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var from = row.Path;
        string action;
        if (kind == StorageKinds.Oem && row.Path.Length == 0)
        {
            // OEM workers start with the process, so the first OEM path waits for a restart.
            row.PendingPath = target;
            action = "STORAGE_PATH_SET";
        }
        else if (row.Path.Length == 0)
        {
            row.Path = target;
            row.CompletedAt = now;
            action = "STORAGE_PATH_SET";
        }
        else
        {
            row.PreviousPath = row.Path;
            row.Path = target;
            row.Status = StorageStatuses.Migrating;
            row.Phase = StoragePhases.Copying;
            row.TotalFiles = row.TotalBytes = row.CopiedFiles = row.CopiedBytes = row.DeletedFiles = row.SkippedFiles = 0;
            row.LastError = null;
            row.StartedAt = now;
            row.CompletedAt = null;
            row.RequestedBy = current.Id;
            action = "STORAGE_MIGRATION_START";
        }
        row.UpdatedAt = now;
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, current.Id, action, "storage_location", null,
            new { kind, label = StorageKinds.Label(kind), from, to = target, pending = row.PendingPath is not null }, ip, ct);
        await tx.CommitAsync(ct);
        ApplyRuntime(row);
        signal.Wake();
        return ToResponse(row);
    }

    public async Task<StorageLocationResponse> CancelAsync(CurrentUser actor, string kindArgument, string? ip, CancellationToken ct)
    {
        var kind = StorageKinds.Normalize(kindArgument);
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await LockAsync(conn, tx, actor, ct);
        await using var context = EfDb.Use(conn, tx);
        var row = (await LockRowsAsync(context, ct)).SingleOrDefault(item => item.Kind == kind)
            ?? throw ApiException.NotFound("存储位置不存在");
        if (row.PreviousPath is null) throw ApiException.Conflict("该存储位置没有进行中的迁移");
        if (kind == StorageKinds.Log) throw ApiException.Conflict("日志目录迁移无法取消，请等待完成");
        if (row.Phase == StoragePhases.Deleting)
            throw ApiException.Conflict("旧目录的文件已开始删除，迁移无法取消，请等待完成");
        // Cancelling is a migration back: content written to the new root meanwhile is copied home and verified.
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        (row.Path, row.PreviousPath) = (row.PreviousPath, row.Path);
        row.Status = StorageStatuses.Migrating;
        row.Phase = StoragePhases.Copying;
        row.TotalFiles = row.TotalBytes = row.CopiedFiles = row.CopiedBytes = row.DeletedFiles = row.SkippedFiles = 0;
        row.LastError = null;
        row.StartedAt = now;
        row.CompletedAt = null;
        row.RequestedBy = current.Id;
        row.UpdatedAt = now;
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, current.Id, "STORAGE_MIGRATION_CANCEL", "storage_location", null,
            new { kind, label = StorageKinds.Label(kind), restoredPath = row.Path, abandonedPath = row.PreviousPath }, ip, ct);
        await tx.CommitAsync(ct);
        ApplyRuntime(row);
        signal.Interrupt();
        return ToResponse(row);
    }

    public async Task<StorageLocationResponse> RetryAsync(CurrentUser actor, string kindArgument, string? ip, CancellationToken ct)
    {
        var kind = StorageKinds.Normalize(kindArgument);
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await LockAsync(conn, tx, actor, ct);
        await using var context = EfDb.Use(conn, tx);
        var row = (await LockRowsAsync(context, ct)).SingleOrDefault(item => item.Kind == kind)
            ?? throw ApiException.NotFound("存储位置不存在");
        if (row.Status != StorageStatuses.Failed) throw ApiException.Conflict("只有失败的迁移可以重试");
        row.Status = StorageStatuses.Migrating;
        row.LastError = null;
        row.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, current.Id, "STORAGE_MIGRATION_RETRY", "storage_location", null,
            new { kind, label = StorageKinds.Label(kind), phase = row.Phase }, ip, ct);
        await tx.CommitAsync(ct);
        signal.Wake();
        return ToResponse(row);
    }

    /// <summary>Applies a committed row to the running process: storage roots, and the log sink for the log directory.</summary>
    internal void ApplyRuntime(StorageLocation row)
    {
        StorageLocationState.Apply(options, row);
        if (row.Kind == StorageKinds.Log && logSink is not null
            && !StorageLocationBootstrap.SamePath(logSink.Directory, row.Path))
            logSink.SwitchTo(row.Path);
    }

    private static async Task<CurrentUser> LockAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, CancellationToken ct)
    {
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, Permission, ct);
        return current;
    }

    private static async Task<List<StorageLocation>> LockRowsAsync(YfDbContext context, CancellationToken ct) =>
        await context.StorageLocations.FromSqlRaw("SELECT * FROM storage_locations FOR UPDATE").ToListAsync(ct);

    internal static StorageLocationResponse ToResponse(StorageLocation row) => new(
        row.Kind, StorageKinds.Label(row.Kind), row.Path, row.PreviousPath, row.PendingPath, row.Status, row.Phase,
        row.TotalFiles, row.TotalBytes, row.CopiedFiles, row.CopiedBytes, row.DeletedFiles, row.SkippedFiles,
        row.LastError, row.StartedAt, row.CompletedAt, FreeBytes(row.Path));

    private static long? FreeBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetPathRoot(path) is { Length: > 0 } drive ? new DriveInfo(drive).AvailableFreeSpace : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
