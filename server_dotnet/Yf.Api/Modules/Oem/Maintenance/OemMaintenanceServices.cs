using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Maintenance;

/// <summary>
/// Physical deletion as a retryable state machine (the file system cannot join a
/// database transaction): claim → PURGE_PENDING (new downloads refused) → wait for
/// active downloads up to the drain period, then cancel them → delete → PURGED.
/// A failed delete keeps PURGE_PENDING, backs off and alerts after repeated failures.
/// </summary>
public sealed class OemPurgeService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    ILogger<OemPurgeService> logger)
{
    private const int AlertAfterAttempts = 5;
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        ulong[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            candidates = await uow.Db.OemTransferFiles.AsNoTracking()
                .Where(file => ((file.PayloadStatus == PayloadStatuses.Quarantined || file.PayloadStatus == PayloadStatuses.Available)
                        && file.PurgeDueAt != null && file.PurgeDueAt <= uow.Now)
                    || (file.PayloadStatus == PayloadStatuses.PurgePending
                        && (file.PurgeLeaseUntil == null || file.PurgeLeaseUntil <= uow.Now)
                        && (file.PurgeNextAttemptAt == null || file.PurgeNextAttemptAt <= uow.Now)))
                .OrderBy(file => file.PurgeDueAt).Select(file => file.Id).Take(20).ToArrayAsync(ct);
        }
        var purged = 0;
        foreach (var id in candidates)
            if (await PurgeOneAsync(id, ct)) purged++;
        return purged;
    }

    private async Task<bool> PurgeOneAsync(ulong fileId, CancellationToken ct)
    {
        ulong claimVersion;
        string relative;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == fileId).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {fileId} FOR UPDATE").SingleAsync(ct);
            var due = file.PayloadStatus is PayloadStatuses.Quarantined or PayloadStatuses.Available && file.PurgeDueAt <= uow.Now;
            var retry = file.PayloadStatus == PayloadStatuses.PurgePending && (file.PurgeLeaseUntil is null || file.PurgeLeaseUntil <= uow.Now)
                && (file.PurgeNextAttemptAt is null || file.PurgeNextAttemptAt <= uow.Now);
            if (!due && !retry) return false;
            var settings = await OemSettings.LoadAsync(uow.Db, ct);
            file.PayloadStatus = PayloadStatuses.PurgePending;
            file.UpdatedAt = uow.Now;
            file.ConcurrencyVersion++;
            var leases = await uow.Db.OemDownloadLeases.Where(lease => lease.FileId == fileId && lease.Status == DownloadLeaseStatuses.Active
                && lease.LeaseUntil > uow.Now && lease.HardDeadline > uow.Now).ToArrayAsync(ct);
            if (leases.Length > 0 && (file.PurgeDueAt ?? uow.Now).Add(settings.PurgeDrain) > uow.Now)
            {
                // Downloads in flight may finish within the drain period; new ones are already refused.
                file.PurgeNextAttemptAt = uow.Now.AddSeconds(30);
                await uow.Db.SaveChangesAsync(ct);
                await uow.CommitAsync(ct);
                return false;
            }
            foreach (var lease in leases) lease.Status = DownloadLeaseStatuses.Cancelled;
            await uow.Db.OemFileScanJobs.Where(job => job.FileId == fileId && (job.Status == ScanJobStatuses.Pending || job.Status == ScanJobStatuses.Running))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, ScanJobStatuses.Error)
                    .SetProperty(job => job.LastError, "文件已清理").SetProperty(job => job.CompletedAt, uow.Now)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1), ct);
            file.PurgeLeaseOwner = Owner;
            file.PurgeLeaseUntil = uow.Now.AddMinutes(10);
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            claimVersion = file.ConcurrencyVersion;
            relative = file.StoragePath;
        }

        string? failure = null;
        try
        {
            var path = storage.Absolute(relative);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = error is UnauthorizedAccessException ? "没有删除权限" : "文件被占用或删除失败";
            logger.LogWarning("OEM purge of file {FileId} failed ({ErrorType}).", fileId, error.GetType().Name);
        }

        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == fileId).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {fileId} FOR UPDATE").SingleAsync(ct);
            if (file.ConcurrencyVersion != claimVersion || file.PayloadStatus != PayloadStatuses.PurgePending) return false;
            file.PurgeLeaseOwner = null;
            file.PurgeLeaseUntil = null;
            file.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            if (failure is null)
            {
                file.PayloadStatus = PayloadStatuses.Purged;
                file.PurgedAt = uow.Now;
                file.PurgeLastError = null;
                await uow.Db.SaveChangesAsync(ct);
                await audit.WriteAsync(uow, null, "OEM_PURGE", "oem_file", fileId,
                    new { targetName = file.OriginalName, transferId, reason = file.PurgeReason, file.Sha256, file.SizeBytes }, ct);
                if (file.PurgeReason == PurgeReasons.Retention) uow.Raise(new FilePurgedEvent(transferId, fileId));
            }
            else
            {
                file.PurgeAttemptCount++;
                file.PurgeLastError = failure;
                file.PurgeNextAttemptAt = uow.Now.AddSeconds(Math.Min(3600, 30 * (1 << Math.Min(file.PurgeAttemptCount, 7))));
                await uow.Db.SaveChangesAsync(ct);
                if (file.PurgeAttemptCount == AlertAfterAttempts)
                {
                    await audit.WriteAsync(uow, null, "OEM_PURGE_FAILING", "oem_file", fileId,
                        new { targetName = file.OriginalName, transferId, attempts = file.PurgeAttemptCount, reason = failure }, ct);
                    uow.Raise(new FilePurgeFailingEvent(transferId, fileId, file.PurgeAttemptCount));
                }
            }
            await dispatcher.CommitAsync(uow, ct);
            return failure is null;
        }
    }

    /// <summary>Abandons drafts untouched for longer than the draft retention period; their files are purged.</summary>
    public async Task<int> ExpireDraftsAsync(OemTransferService transfers, CancellationToken ct)
    {
        ulong[] ids;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            var cutoff = read.Now.Subtract((await OemSettings.LoadAsync(read.Db, ct)).DraftTtl);
            ids = await read.Db.OemTransfers.AsNoTracking()
                .Where(transfer => transfer.LifecycleStatus == TransferLifecycle.Draft && transfer.UpdatedAt <= cutoff)
                .OrderBy(transfer => transfer.Id).Select(transfer => transfer.Id).Take(50).ToArrayAsync(ct);
        }
        foreach (var id in ids)
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var transfer = await OemTransferProgression.LockTransferAsync(uow, id, ct);
            if (transfer.LifecycleStatus != TransferLifecycle.Draft) continue;
            await transfers.AbandonAsync(uow, transfer, null, "草稿超过保留期限", PurgeReasons.DraftExpired, ct);
            await dispatcher.CommitAsync(uow, ct);
        }
        return ids.Length;
    }
}

/// <summary>
/// Storage consistency. After a database restore (marker set by the maintenance
/// script or <c>--oem-mark-restored</c>) content endpoints stay closed until this runs:
/// unfinished moves are resumed, missing files become MISSING_UNVERIFIED (the restored
/// database cannot tell a normal purge from a loss), claimed purges complete, and files
/// uploaded after the backup — unknown to the database — are deleted. In normal
/// operation it flags unexpected loss as STORAGE_LOST and removes stale orphans.
/// </summary>
public sealed class OemReconcileService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemPromotionService promotions,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    ILogger<OemReconcileService> logger)
{
    public const string RestoredMarker = "RESTORED";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        bool restoring;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            restoring = (await OemSettings.LoadAsync(read.Db, ct)).ReconcileRequired;
        return restoring ? await ReconcileAfterRestoreAsync(ct) : await RoutineCheckAsync(ct);
    }

    /// <summary>Writes the restore marker (used by <c>--oem-mark-restored</c> after a manual database restore).</summary>
    public static async Task MarkRestoredAsync(AppDb database, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var db = EfDb.Use(connection);
        var updated = await db.SystemConfigs.Where(config => config.CfgKey == OemSettingCatalog.ReconcileRequired)
            .ExecuteUpdateAsync(setters => setters.SetProperty(config => config.CfgValue, RestoredMarker), ct);
        if (updated != 1) throw new InvalidOperationException("OEM reconcile marker is missing; run --migrate-database first.");
    }

    public async Task<int> ReconcileAfterRestoreAsync(CancellationToken ct)
    {
        string[] prepared;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            prepared = await read.Db.OemFilePromotions.AsNoTracking().Where(item => item.Status == PromotionStatuses.Prepared)
                .Select(item => item.Id).ToArrayAsync(ct);
        foreach (var id in prepared) await promotions.PromoteAsync(id, ct);

        var missing = await MarkMissingAsync(PayloadStatuses.MissingUnverified, ct);
        int purgedClaims;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var pending = await uow.Db.OemTransferFiles.Where(file => file.PayloadStatus == PayloadStatuses.PurgePending).ToArrayAsync(ct);
            var gone = pending.Where(file => !storage.Exists(file.StoragePath)).ToArray();
            foreach (var file in gone)
            {
                file.PayloadStatus = PayloadStatuses.Purged;
                file.PurgedAt = uow.Now;
                file.PurgeLeaseOwner = null;
                file.PurgeLeaseUntil = null;
                file.ConcurrencyVersion++;
            }
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            purgedClaims = gone.Length;
        }
        var orphans = await DeleteOrphansAsync(TimeSpan.Zero, ct);

        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            await uow.Db.SystemConfigs.Where(config => config.CfgKey == OemSettingCatalog.ReconcileRequired)
                .ExecuteUpdateAsync(setters => setters.SetProperty(config => config.CfgValue, string.Empty), ct);
            await audit.WriteAsync(uow, null, "OEM_RECONCILE_COMPLETED", "oem_storage", null,
                new { missing, completedPurges = purgedClaims, orphansDeleted = orphans, resumedPromotions = prepared.Length }, ct);
            await uow.CommitAsync(ct);
        }
        return missing + orphans;
    }

    public async Task<int> RoutineCheckAsync(CancellationToken ct) =>
        await MarkMissingAsync(PayloadStatuses.StorageLost, ct) + await DeleteOrphansAsync(TimeSpan.FromHours(48), ct);

    /// <summary>Flags files whose content should be on disk but is absent or has the wrong size.</summary>
    private async Task<int> MarkMissingAsync(string status, CancellationToken ct)
    {
        (ulong Id, string Path, ulong Size)[] files;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            files = (await read.Db.OemTransferFiles.AsNoTracking()
                .Where(file => file.PayloadStatus == PayloadStatuses.Quarantined || file.PayloadStatus == PayloadStatuses.Available
                    || (status == PayloadStatuses.MissingUnverified && file.PayloadStatus == PayloadStatuses.Promoting))
                .Select(file => new { file.Id, file.StoragePath, file.SizeBytes }).ToArrayAsync(ct))
                .Select(file => (file.Id, file.StoragePath, file.SizeBytes)).ToArray();
        var marked = 0;
        foreach (var candidate in files.Where(file => !Intact(file.Path, file.Size)))
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == candidate.Id).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {candidate.Id} FOR UPDATE").SingleAsync(ct);
            // Re-check under the lock: a concurrent promotion or purge may have moved it legitimately.
            if (file.PayloadStatus is not (PayloadStatuses.Quarantined or PayloadStatuses.Available or PayloadStatuses.Promoting)
                || Intact(file.StoragePath, file.SizeBytes)) continue;
            file.PayloadStatus = status;
            file.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            await audit.WriteAsync(uow, null, status == PayloadStatuses.StorageLost ? "OEM_FILE_STORAGE_LOST" : "OEM_FILE_MISSING_UNVERIFIED",
                "oem_file", file.Id, new { targetName = file.OriginalName, transferId, file.Sha256, file.SizeBytes }, ct);
            uow.Raise(new FileMissingEvent(transferId, file.Id, status));
            await dispatcher.CommitAsync(uow, ct);
            marked++;
        }
        return marked;
    }

    /// <summary>Deletes content the database does not know about (never files referenced by any row or an active upload).</summary>
    private async Task<int> DeleteOrphansAsync(TimeSpan minimumAge, CancellationToken ct)
    {
        var root = storage.Root();
        HashSet<string> known;
        HashSet<string> activeSessions;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            known = (await read.Db.OemTransferFiles.AsNoTracking().Select(file => file.StoredName).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            activeSessions = (await read.Db.OemUploadSessions.AsNoTracking()
                .Where(session => session.Status == Transfers.UploadStatuses.Uploading || session.Status == Transfers.UploadStatuses.Merging)
                .Select(session => session.Id).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var cutoff = DateTime.UtcNow - minimumAge;
        var deleted = new List<(string Name, long Size)>();
        foreach (var area in new[] { OemStorage.QuarantineArea, OemStorage.AvailableArea })
        {
            var directory = Path.Combine(root, area);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || known.Contains(info.Name) || info.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    var size = info.Length;
                    info.Delete();
                    deleted.Add((info.Name, size));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("OEM orphan cleanup failed ({ErrorType}).", error.GetType().Name);
                }
            }
        }
        var uploads = Path.Combine(root, OemStorage.UploadsArea);
        if (Directory.Exists(uploads))
            foreach (var directory in Directory.EnumerateDirectories(uploads))
            {
                var info = new DirectoryInfo(directory);
                if (activeSessions.Contains(info.Name) || info.LastWriteTimeUtc > cutoff) continue;
                try { await storage.DeleteDirectoryAsync(directory, ct); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning("OEM upload directory cleanup failed ({ErrorType}).", error.GetType().Name);
                }
            }
        if (deleted.Count > 0)
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            foreach (var (name, size) in deleted)
                await audit.WriteAsync(uow, null, "OEM_RECONCILE_ORPHAN_PURGED", "oem_storage", null, new { storedName = name, size }, ct);
            await uow.CommitAsync(ct);
        }
        return deleted.Count;
    }

    private bool Intact(string relative, ulong size)
    {
        try
        {
            var path = storage.Absolute(relative);
            return File.Exists(path) && (ulong)new FileInfo(path).Length == size;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>OEM audit view: only OEM rows (and organisation-leader changes that drive OEM routing).</summary>
public sealed class OemAuditQueryService(IDbContextFactory<YfDbContext> dbFactory)
{
    public async Task<object> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AuditView, ct);
        var query = uow.Db.AuditLogs.AsNoTracking().Where(log => log.ActorRealm == AuditScopes.OemRealm
            || log.Action.StartsWith(AuditScopes.OemActionPrefix) || log.Action == "DEPT_LEADER_CHANGE");
        var action = request.Query["action"].ToString().Trim();
        if (action.Length > 0) query = query.Where(log => log.Action == action);
        var targetId = request.Query["targetId"].ToString().Trim();
        if (targetId.Length > 0) query = query.Where(log => log.TargetId == targetId);
        var keyword = request.Query["keyword"].ToString().Trim();
        if (keyword.Length > 0)
        {
            var pattern = "%" + keyword + "%";
            query = query.Where(log => (log.EmployeeNo != null && EF.Functions.Like(log.EmployeeNo, pattern))
                || EF.Functions.Like(log.Action, pattern)
                || (log.Detail != null && EF.Functions.Like(EF.Functions.JsonUnquote(log.Detail), pattern)));
        }
        foreach (var name in new[] { "start", "end" })
        {
            if (string.IsNullOrWhiteSpace(request.Query[name])) continue;
            if (!DateTimeOffset.TryParse(request.Query[name], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var at)) throw ApiException.BadRequest("日期参数无效");
            var utc = at.UtcDateTime;
            query = name == "start" ? query.Where(log => log.CreatedAt >= utc) : query.Where(log => log.CreatedAt <= utc);
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(log => log.Id).Page(offset, size)
            .Select(log => new
            {
                log.Id, log.Action, actorRealm = log.ActorRealm ?? (log.UserId == null ? OemRealms.System : OemRealms.Internal),
                actorId = log.ActorAccountId ?? log.UserId, log.EmployeeNo, log.TargetType, log.TargetId, log.Detail, log.Ip, log.CreatedAt,
            }).ToArrayAsync(ct);
        return new
        {
            list = rows.Select(row => new
            {
                row.Id, row.Action, row.actorRealm, row.actorId, row.EmployeeNo, row.TargetType, row.TargetId, row.Ip, row.CreatedAt,
                detail = row.Detail is null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonDocument.Parse(row.Detail).RootElement.Clone(),
            }),
            total, page, pageSize = size,
        };
    }
}
