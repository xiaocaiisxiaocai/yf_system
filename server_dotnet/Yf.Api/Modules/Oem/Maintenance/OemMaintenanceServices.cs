using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;

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
    private readonly OemItemBackoff<ulong> purgeBackoff = new();
    private readonly OemItemBackoff<ulong> draftBackoff = new();

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        ulong[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            var suppressed = purgeBackoff.Suppressed();
            candidates = await uow.Db.OemTransferFiles.AsNoTracking()
                .Where(file => !suppressed.Contains(file.Id))
                .Where(file => ((file.PayloadStatus == PayloadStatuses.Quarantined || file.PayloadStatus == PayloadStatuses.Available)
                        && file.PurgeDueAt != null && file.PurgeDueAt <= uow.Now)
                    || (file.PayloadStatus == PayloadStatuses.Promoting && file.PurgeDueAt != null && file.PurgeDueAt <= uow.Now
                        && uow.Db.OemFilePromotions.Any(promotion => promotion.FileId == file.Id && promotion.Status == PromotionStatuses.Failed))
                    || (file.PayloadStatus == PayloadStatuses.PurgePending
                        && (file.PurgeLeaseUntil == null || file.PurgeLeaseUntil <= uow.Now)
                        && (file.PurgeNextAttemptAt == null || file.PurgeNextAttemptAt <= uow.Now)))
                .OrderBy(file => file.PurgeDueAt).Select(file => file.Id).Take(20).ToArrayAsync(ct);
        }
        var purged = 0;
        foreach (var id in candidates)
        {
            try
            {
                if (await PurgeOneAsync(id, ct)) purged++;
                purgeBackoff.Succeeded(id);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                // Isolate a row that keeps throwing so the rest of the batch still progresses.
                purgeBackoff.Failed(id);
                logger.LogError(error, "OEM purge of file {FileId} failed; it is retried later.", id);
            }
        }
        return purged;
    }

    private async Task<bool> PurgeOneAsync(ulong fileId, CancellationToken ct)
    {
        ulong claimVersion;
        string[] relatives;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == fileId).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {fileId} FOR UPDATE").SingleAsync(ct);
            var failedPromotions = await uow.Db.OemFilePromotions.AsNoTracking()
                .Where(promotion => promotion.FileId == fileId && promotion.Status == PromotionStatuses.Failed).ToArrayAsync(ct);
            var due = (file.PayloadStatus is PayloadStatuses.Quarantined or PayloadStatuses.Available
                    || file.PayloadStatus == PayloadStatuses.Promoting && failedPromotions.Length > 0)
                && file.PurgeDueAt <= uow.Now;
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
            await uow.Db.OemFileScanJobs.Where(job => job.FileId == fileId && (job.Status == ValidationJobStatuses.Pending || job.Status == ValidationJobStatuses.Running))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, ValidationJobStatuses.Error)
                    .SetProperty(job => job.LastError, "文件已清理").SetProperty(job => job.CompletedAt, uow.Now)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1), ct);
            file.PurgeLeaseOwner = Owner;
            file.PurgeLeaseUntil = uow.Now.AddMinutes(10);
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            claimVersion = file.ConcurrencyVersion;
            relatives = failedPromotions.SelectMany(promotion => new[] { promotion.SourcePath, promotion.TargetPath })
                .Append(file.StoragePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        string? failure = null;
        foreach (var relative in relatives)
        {
            try
            {
                var path = storage.Absolute(relative);
                // Delete is already idempotent for an absent file. File.Exists would
                // also return false on access errors and incorrectly acknowledge a purge.
                File.Delete(path);
            }
            catch (DirectoryNotFoundException) { /* The containing directory is already absent. */ }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failure ??= error is UnauthorizedAccessException ? "没有删除权限" : "文件被占用或删除失败";
                logger.LogWarning("OEM purge of file {FileId} failed ({ErrorType}).", fileId, error.GetType().Name);
            }
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
            var suppressed = draftBackoff.Suppressed();
            ids = await read.Db.OemTransfers.AsNoTracking()
                .Where(transfer => transfer.LifecycleStatus == TransferLifecycle.Draft && transfer.UpdatedAt <= cutoff)
                .Where(transfer => !suppressed.Contains(transfer.Id))
                .OrderBy(transfer => transfer.Id).Select(transfer => transfer.Id).Take(50).ToArrayAsync(ct);
        }
        var expired = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await ExpireDraftAsync(transfers, id, ct)) expired++;
                draftBackoff.Succeeded(id);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                // A draft that cannot be abandoned must not block the drafts after it.
                draftBackoff.Failed(id);
                logger.LogError(error, "OEM draft {TransferId} could not be expired; it is retried later.", id);
            }
        }
        return expired;
    }

    private async Task<bool> ExpireDraftAsync(OemTransferService transfers, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var transfer = await OemTransferProgression.LockTransferAsync(uow, id, ct);
        var cutoff = uow.Now.Subtract((await OemSettings.LoadAsync(uow.Db, ct)).DraftTtl);
        if (transfer.LifecycleStatus != TransferLifecycle.Draft || transfer.UpdatedAt > cutoff) return false;
        await transfers.AbandonAsync(uow, transfer, null, "草稿超过保留期限", PurgeReasons.DraftExpired, ct);
        await dispatcher.CommitAsync(uow, ct);
        return true;
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

    /// <summary>
    /// The routine check stats every on-disk row and walks both content areas, so it runs at
    /// most hourly; the job itself ticks more often so a restore marker is handled promptly.
    /// </summary>
    internal static readonly TimeSpan RoutineInterval = TimeSpan.FromHours(1);

    private DateTime nextRoutineCheck = DateTime.MinValue;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        bool restoring;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            restoring = (await OemSettings.LoadAsync(read.Db, ct)).ReconcileRequired;
        if (restoring) return await ReconcileAfterRestoreAsync(ct);
        if (DateTime.UtcNow < nextRoutineCheck) return 0;
        var result = await RoutineCheckAsync(ct);
        nextRoutineCheck = DateTime.UtcNow + RoutineInterval;
        return result;
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
        var unresolved = 0;
        foreach (var id in prepared)
        {
            try { await promotions.PromoteAsync(id, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                // Keep resuming the other moves, but never clear the restore marker while one
                // is unresolved: the whole reconcile is retried on the next run.
                unresolved++;
                logger.LogError(error, "OEM reconcile could not resume promotion {PromotionId}.", id);
            }
        }
        if (unresolved > 0)
            throw new InvalidOperationException($"OEM reconcile left {unresolved} promotion(s) unresolved; it will be retried.");

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
        var restoring = status == PayloadStatuses.MissingUnverified;
        foreach (var candidate in files.Where(file => !Intact(file.Path, file.Size)))
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == candidate.Id).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {candidate.Id} FOR UPDATE").SingleAsync(ct);
            // Re-check under the lock: a concurrent promotion or purge may have moved it legitimately.
            if (file.PayloadStatus is not (PayloadStatuses.Quarantined or PayloadStatuses.Available or PayloadStatuses.Promoting)
                || Intact(file.StoragePath, file.SizeBytes)) continue;
            if (restoring && await TryRelocateAsync(file, ct))
            {
                await audit.WriteAsync(uow, null, "OEM_RECONCILE_FILE_RELOCATED", "oem_file", file.Id,
                    new { targetName = file.OriginalName, transferId, file.Sha256, file.SizeBytes }, ct);
                await uow.CommitAsync(ct);
                continue;
            }
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
        ValidateStorageRoot(root, ct);
        HashSet<string> known;
        HashSet<string> activeSessions;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            // Every row whose content is or may be on disk protects its stored name. Only PURGED
            // rows are left out: their content was deleted before the row reached that terminal
            // state, so a file still carrying such a name is a genuine orphan. Lost/missing rows
            // stay protected because their content may legitimately reappear.
            known = (await read.Db.OemTransferFiles.AsNoTracking()
                .Where(file => file.PayloadStatus != PayloadStatuses.Purged)
                .Select(file => file.StoredName).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            IReadOnlyList<FileInfo> candidates;
            try { candidates = EnumerateFilesWithoutReparsePoints(root, directory, ct); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning("OEM orphan area validation failed ({ErrorType}); the area was left untouched.", error.GetType().Name);
                continue;
            }
            foreach (var info in candidates)
            {
                if (known.Contains(info.Name) || info.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    // Re-resolve every ancestor immediately before deletion. The initial traversal
                    // rejects reparse points without following them; this second check also catches
                    // an ancestor that was replaced after traversal.
                    var resolved = FileStorage.ResolveExistingFile(root, info.FullName, ct);
                    if (!string.Equals(Path.GetFullPath(info.FullName), resolved,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("OEM orphan path changed during cleanup");
                    var size = new FileInfo(resolved).Length;
                    File.Delete(resolved);
                    deleted.Add((info.Name, size));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning("OEM orphan cleanup failed ({ErrorType}).", error.GetType().Name);
                }
            }
        }
        var uploads = Path.Combine(root, OemStorage.UploadsArea);
        if (Directory.Exists(uploads))
        {
            IReadOnlyList<DirectoryInfo> uploadDirectories;
            IReadOnlyList<FileInfo> strayFiles;
            try { (uploadDirectories, strayFiles) = EnumerateDirectChildrenWithoutReparsePoints(root, uploads, ct); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(error, "OEM upload area validation failed; the area was left untouched.");
                (uploadDirectories, strayFiles) = ([], []);
            }
            // Uploads only ever create session directories; a plain file there is debris. It is
            // removed once stale instead of making the whole uploads cleanup fail forever.
            foreach (var info in strayFiles)
            {
                if (info.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    var resolved = FileStorage.ResolveExistingFile(root, info.FullName, ct);
                    if (!string.Equals(Path.GetFullPath(info.FullName), resolved,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("OEM upload stray file path changed during cleanup");
                    var size = new FileInfo(resolved).Length;
                    File.Delete(resolved);
                    deleted.Add((info.Name, size));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(error, "OEM upload stray file cleanup failed.");
                }
            }
            foreach (var info in uploadDirectories)
            {
                if (activeSessions.Contains(info.Name) || info.LastWriteTimeUtc > cutoff) continue;
                try { await storage.DeleteDirectoryAsync(info.FullName, ct); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning("OEM upload directory cleanup failed ({ErrorType}).", error.GetType().Name);
                }
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

    /// <summary>
    /// Validates an entire OEM content area before returning any deletion candidates.
    /// Enumeration is top-directory-only at each level, so a junction or symbolic link is
    /// rejected as an entry and is never traversed.
    /// </summary>
    private static IReadOnlyList<FileInfo> EnumerateFilesWithoutReparsePoints(
        string root, string directory, CancellationToken ct)
    {
        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(ValidatedDirectory(root, directory, ct));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in current.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("OEM orphan area contains a reparse point");
                if (entry is DirectoryInfo child)
                    pending.Push(ValidatedDirectory(root, child.FullName, ct));
                else if (entry is FileInfo file)
                {
                    FileStorage.ResolveExistingFile(root, file.FullName, ct);
                    files.Add(file);
                }
            }
        }
        return files;
    }

    private static (IReadOnlyList<DirectoryInfo> Directories, IReadOnlyList<FileInfo> Files) EnumerateDirectChildrenWithoutReparsePoints(
        string root, string directory, CancellationToken ct)
    {
        var parent = ValidatedDirectory(root, directory, ct);
        var children = new List<DirectoryInfo>();
        var files = new List<FileInfo>();
        foreach (var entry in parent.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("OEM upload area contains a reparse point");
            if (entry is DirectoryInfo child) children.Add(ValidatedDirectory(root, child.FullName, ct));
            else if (entry is FileInfo file) files.Add(file);
        }
        return (children, files);
    }

    private static DirectoryInfo ValidatedDirectory(string root, string directory, CancellationToken ct)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) throw new DirectoryNotFoundException("OEM storage directory is missing");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("OEM storage directory is a reparse point");
        var resolved = FileStorage.ResolveExisting(root, info.FullName, requireFile: false, ct: ct);
        if (!string.Equals(Path.GetFullPath(info.FullName), resolved,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("OEM storage directory resolves through a reparse point");
        return info;
    }

    private static void ValidateStorageRoot(string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(root);
        if (!info.Exists) throw new DirectoryNotFoundException("OEM storage root is missing");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("OEM storage root is a reparse point");
    }

    /// <summary>
    /// After a database restore onto a reused OEM root, a file may have moved between areas
    /// since the backup (a quarantined row whose content was promoted later). The restored
    /// row is authoritative: identical content found under the same stored name in the other
    /// area is moved back to the recorded path instead of being declared missing.
    /// </summary>
    private async Task<bool> TryRelocateAsync(Data.OemTransferFile file, CancellationToken ct)
    {
        try
        {
            var recorded = OemStorage.QuarantineRelative(file.StoredName) == file.StoragePath
                ? OemStorage.AvailableRelative(file.StoredName)
                : OemStorage.QuarantineRelative(file.StoredName);
            if (recorded == file.StoragePath || !Intact(recorded, file.SizeBytes)) return false;
            var source = storage.Absolute(recorded);
            await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
                if (!sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            var target = storage.Absolute(file.StoragePath);
            storage.EnsureParent(target, ct);
            File.Move(source, target, overwrite: false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("OEM reconcile could not relocate file {FileId} ({ErrorType}).", file.Id, error.GetType().Name);
            return false;
        }
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

/// <summary>
/// Bounded cleanup of OEM session bookkeeping that otherwise grows forever: refresh tokens
/// expired for longer than the retention, and download sessions that finished (or passed their
/// absolute deadline) longer ago. Download ranges and leases go with their session through
/// the ON DELETE CASCADE foreign keys. Deleted rows are unusable already; who downloaded what
/// stays in the audit log, which also keeps account-deletion history checks intact.
/// </summary>
public sealed class OemSessionCleanupService(IDbContextFactory<YfDbContext> dbFactory, ILogger<OemSessionCleanupService> logger)
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    internal const int BatchSize = 500;
    internal const int MaximumBatchesPerRun = 20;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var tokens = 0;
        var sessions = 0;
        for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var cutoff = uow.Now.Subtract(Retention);
            var ids = await uow.Db.OemRefreshTokens.AsNoTracking()
                .Where(token => token.ExpiresAt < cutoff || token.SessionExpiresAt < cutoff)
                .OrderBy(token => token.Id).Select(token => token.Id).Take(BatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            tokens += await uow.Db.OemRefreshTokens.Where(token => ids.Contains(token.Id)).ExecuteDeleteAsync(ct);
            await uow.CommitAsync(ct);
            if (ids.Length < BatchSize) break;
        }
        for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var cutoff = uow.Now.Subtract(Retention);
            var ids = await uow.Db.OemDownloadSessions.AsNoTracking()
                .Where(session => (session.CompletedAt != null && session.CompletedAt < cutoff) || session.AbsoluteDeadline < cutoff)
                .OrderBy(session => session.CreatedAt).Select(session => session.Id).Take(BatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            sessions += await uow.Db.OemDownloadSessions.Where(session => ids.Contains(session.Id)).ExecuteDeleteAsync(ct);
            await uow.CommitAsync(ct);
            if (ids.Length < BatchSize) break;
        }
        if (tokens + sessions > 0)
            logger.LogInformation("OEM session cleanup removed {Tokens} refresh token(s) and {Sessions} download session(s).", tokens, sessions);
        return tokens + sessions;
    }
}

/// <summary>OEM audit view: only OEM rows (and organisation-leader changes that drive OEM routing).</summary>
public sealed class OemAuditQueryService(IDbContextFactory<YfDbContext> dbFactory)
{
    public async Task<OemPageResponse<OemAuditLogResponse>> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
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
        return new OemPageResponse<OemAuditLogResponse>(
            rows.Select(row => new OemAuditLogResponse(
                row.Id, row.Action, row.actorRealm, row.actorId, row.EmployeeNo, row.TargetType, row.TargetId, row.Ip, row.CreatedAt,
                ParseDetail(row.Detail))).ToArray(),
            total, page, size);
    }

    /// <summary>Copies the stored detail out of a disposed document so pooled parser buffers are returned.</summary>
    private static System.Text.Json.JsonElement? ParseDetail(string? detail)
    {
        if (detail is null) return null;
        using var document = System.Text.Json.JsonDocument.Parse(detail);
        return document.RootElement.Clone();
    }
}
