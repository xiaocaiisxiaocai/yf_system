using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Validation;

/// <summary>
/// File-validation worker logic. Jobs are claimed with a lease and a version (fencing): a worker
/// that lost its lease — for example during an IIS overlapped recycle — can never
/// write a result. Temporary failures back off and retry; only exhausted retries,
/// or invalid content are final, and every final failure stays quarantined.
/// <para>
/// Row-lock order shared by every OEM writer: transfer → file → validation job / promotion.
/// Purge, reconcile and transfer operations lock the transfer and then the file before
/// touching jobs, so a job or promotion row is always resolved without a lock first and
/// locked last (then re-validated). Taking them in any other order can deadlock.
/// </para>
/// </summary>
public sealed class OemFileValidationService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemFileValidationPipeline pipeline,
    OemTransferProgression progression,
    OemPromotionService promotions,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    ILogger<OemFileValidationService> logger)
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Upper bound for handing a claimed job back during shutdown.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly OemItemBackoff<ulong> backoff = new();

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        ulong[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            var suppressed = backoff.Suppressed();
            candidates = await uow.Db.OemFileScanJobs.AsNoTracking()
                .Where(job => (job.Status == ValidationJobStatuses.Pending && (job.NextAttemptAt == null || job.NextAttemptAt <= uow.Now))
                    || (job.Status == ValidationJobStatuses.Running && job.LeaseUntil <= uow.Now))
                .Where(job => !suppressed.Contains(job.Id))
                .OrderBy(job => job.Id).Select(job => job.Id).Take(4).ToArrayAsync(ct);
        }
        var processed = 0;
        foreach (var jobId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            Claim? claim = null;
            try
            {
                claim = await ClaimAsync(jobId, ct);
                if (claim is null) continue;
                var result = await ExecuteAsync(claim, ct);
                ct.ThrowIfCancellationRequested();
                await CompleteAsync(claim, result, ct);
                backoff.Succeeded(jobId);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: hand the job back now instead of leaving it RUNNING until its
                // (size-scaled, possibly very long) lease expires after the restart.
                if (claim is not null) await ReleaseAsync(claim);
                throw;
            }
            catch (Exception error)
            {
                // One job that keeps failing must not block the others behind it.
                backoff.Failed(jobId);
                logger.LogError(error, "OEM validation job {JobId} failed; it is retried later.", jobId);
            }
        }
        return processed;
    }

    internal sealed record Claim(ulong JobId, ulong Version, ulong FileId, string StoragePath, string Extension, ulong Size, string Sha256);

    private sealed record LockedJob(OemTransfer Transfer, OemTransferFile File, OemFileScanJob Job);

    /// <summary>Locks a job's transfer, file and job rows in the global order (transfer → file → job).</summary>
    private static async Task<LockedJob?> LockJobAsync(OemUnitOfWork uow, ulong jobId, CancellationToken ct)
    {
        var target = await uow.Db.OemFileScanJobs.AsNoTracking().Where(job => job.Id == jobId)
            .Join(uow.Db.OemTransferFiles, job => job.FileId, file => file.Id, (job, file) => new { job.FileId, file.TransferId })
            .SingleOrDefaultAsync(ct);
        if (target is null) return null;
        var transfer = await OemTransferProgression.LockTransferAsync(uow, target.TransferId, ct);
        var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {target.FileId} FOR UPDATE").SingleAsync(ct);
        var job = await uow.Db.OemFileScanJobs.FromSqlInterpolated($"SELECT * FROM oem_file_scan_jobs WHERE id = {jobId} FOR UPDATE").SingleOrDefaultAsync(ct);
        // A job never changes its file; re-validate after locking rather than trusting the unlocked read.
        return job is null || job.FileId != file.Id || file.TransferId != transfer.Id ? null : new LockedJob(transfer, file, job);
    }

    internal async Task<Claim?> ClaimAsync(ulong jobId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        if (await LockJobAsync(uow, jobId, ct) is not { } locked) return null;
        var (file, job) = (locked.File, locked.Job);
        var claimable = (job.Status == ValidationJobStatuses.Pending && (job.NextAttemptAt is null || job.NextAttemptAt <= uow.Now))
            || (job.Status == ValidationJobStatuses.Running && job.LeaseUntil <= uow.Now);
        if (!claimable) return null;
        if (file.PayloadStatus != PayloadStatuses.Quarantined || file.Sha256 != job.FileSha256 || file.SizeBytes != job.FileSizeBytes)
        {
            // The file left quarantine or changed: the job is obsolete and must never produce a verdict.
            job.Status = ValidationJobStatuses.Error;
            job.LastError = "文件已不在隔离区或内容已变化";
            job.CompletedAt = uow.Now;
            job.ConcurrencyVersion++;
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            return null;
        }
        job.Status = ValidationJobStatuses.Running;
        job.LeaseOwner = Owner;
        job.LeaseUntil = uow.Now.Add(Timeout(file.SizeBytes)).AddMinutes(1);
        job.StartedAt = uow.Now;
        job.ConcurrencyVersion++;
        file.ScanStatus = ValidationStatuses.Validating;
        file.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await uow.CommitAsync(ct);
        return new Claim(job.Id, job.ConcurrencyVersion, file.Id, file.StoragePath, file.Ext, file.SizeBytes, file.Sha256);
    }

    /// <summary>
    /// Returns a claimed job to PENDING when the worker stops, so it is picked up right after
    /// a restart. Fencing is preserved: nothing changes unless this worker still holds the
    /// claim, and the version bump invalidates the abandoned attempt. Runs on its own short
    /// timeout because the worker's token is already cancelled; on failure the lease simply expires.
    /// </summary>
    internal async Task ReleaseAsync(Claim claim)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, timeout.Token);
            if (await LockJobAsync(uow, claim.JobId, timeout.Token) is not { } locked) return;
            var (file, job) = (locked.File, locked.Job);
            if (job.ConcurrencyVersion != claim.Version || job.Status != ValidationJobStatuses.Running) return;
            job.Status = ValidationJobStatuses.Pending;
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            job.NextAttemptAt = null;
            job.ConcurrencyVersion++;
            if (file.PayloadStatus == PayloadStatuses.Quarantined && file.ScanStatus == ValidationStatuses.Validating)
            {
                file.ScanStatus = ValidationStatuses.Pending;
                file.UpdatedAt = uow.Now;
            }
            await uow.Db.SaveChangesAsync(timeout.Token);
            await uow.CommitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "OEM validation job {JobId} could not be released on shutdown; it is reclaimed after its lease expires.", claim.JobId);
        }
    }

    private async Task<ValidationResult> ExecuteAsync(Claim claim, CancellationToken ct)
    {
        string? workDirectory = null;
        try
        {
            var path = await storage.ResolveExistingFileAsync(claim.StoragePath, ct);
            workDirectory = storage.SessionDirectory(Guid.NewGuid().ToString("D"), create: true, ct);
            ArchiveLimits limits;
            await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            {
                var settings = await OemSettings.LoadAsync(uow.Db, ct);
                limits = settings.ArchiveLimits;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout(claim.Size));
            try
            {
                return await pipeline.RunAsync(new ValidationTarget(path, claim.Sha256, claim.Size), claim.Extension, limits, workDirectory, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ValidationResult.Error("文件校验超时");
            }
        }
        catch (FileNotFoundException)
        {
            return ValidationResult.Error("隔离文件不存在");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("OEM validation of file {FileId} failed ({ErrorType}).", claim.FileId, error.GetType().Name);
            return ValidationResult.Error("文件校验过程出错");
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // Any other failure still completes the job as a retryable error, so it counts
            // toward ValidationMaxRetries instead of looping forever on lease expiry.
            logger.LogError(error, "OEM validation of file {FileId} failed unexpectedly.", claim.FileId);
            return ValidationResult.Error("文件校验过程出错");
        }
        finally
        {
            if (workDirectory is not null)
            {
                try { await storage.DeleteDirectoryAsync(workDirectory, CancellationToken.None); }
                catch (Exception error) { logger.LogWarning("OEM validation work directory cleanup failed ({ErrorType}).", error.GetType().Name); }
            }
        }
    }

    private async Task CompleteAsync(Claim claim, ValidationResult result, CancellationToken ct)
    {
        string? promotionId = null;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            if (await LockJobAsync(uow, claim.JobId, ct) is not { } locked) return;
            var (transfer, file, job) = (locked.Transfer, locked.File, locked.Job);
            var transferId = transfer.Id;
            // Fencing: the lease was lost to another worker, or the job was changed meanwhile.
            if (job.ConcurrencyVersion != claim.Version || job.Status != ValidationJobStatuses.Running) return;
            var settings = await OemSettings.LoadAsync(uow.Db, ct);
            if (file.PayloadStatus != PayloadStatuses.Quarantined)
            {
                // The file left quarantine while it was being validated: the result is moot.
                job.Status = ValidationJobStatuses.Error;
                job.LastError = "文件已不在隔离区";
                job.CompletedAt = uow.Now;
                job.LeaseOwner = null;
                job.LeaseUntil = null;
                job.ConcurrencyVersion++;
                await uow.Db.SaveChangesAsync(ct);
                await uow.CommitAsync(ct);
                return;
            }
            // Legacy engine metadata columns remain empty. They exist only so older
            // database layouts can still be read and migrated.
            job.EngineName = null;
            job.EngineVersion = null;
            job.SignatureVersion = null;
            job.ThreatName = null;
            job.LastError = Truncate(result.Message, 1024);
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            job.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            var finalFailure = false;
            switch (result.Verdict)
            {
                case ValidationVerdict.Valid:
                    job.Status = ValidationJobStatuses.Valid;
                    job.CompletedAt = uow.Now;
                    file.ScanStatus = ValidationStatuses.Valid;
                    file.PayloadStatus = PayloadStatuses.Promoting;
                    promotionId = Guid.NewGuid().ToString("D");
                    uow.Db.OemFilePromotions.Add(new OemFilePromotion
                    {
                        Id = promotionId, FileId = file.Id, FileSha256 = file.Sha256, SizeBytes = file.SizeBytes, SourcePath = file.StoragePath,
                        TargetPath = OemStorage.AvailableRelative(file.StoredName), Status = PromotionStatuses.Prepared, AttemptCount = 0,
                        ConcurrencyVersion = 0, CreatedAt = uow.Now,
                    });
                    break;
                case ValidationVerdict.Invalid:
                    job.Status = ValidationJobStatuses.Invalid;
                    job.CompletedAt = uow.Now;
                    file.ScanStatus = ValidationStatuses.Invalid;
                    finalFailure = true;
                    break;
                default:
                    job.AttemptCount++;
                    if (job.AttemptCount > settings.ValidationMaxRetries)
                    {
                        job.Status = ValidationJobStatuses.Error;
                        job.CompletedAt = uow.Now;
                        file.ScanStatus = ValidationStatuses.Error;
                        finalFailure = true;
                    }
                    else
                    {
                        job.Status = ValidationJobStatuses.Pending;
                        job.NextAttemptAt = uow.Now.AddSeconds(Math.Min(3600, 30 * (1 << Math.Min(job.AttemptCount, 7))));
                        file.ScanStatus = ValidationStatuses.Pending;
                    }
                    break;
            }
            if (finalFailure)
            {
                var due = uow.Now.Add(settings.BlockedRetention);
                if (file.PurgeDueAt is null || file.PurgeDueAt > due) file.PurgeDueAt = due;
                if (file.PurgeReason != PurgeReasons.FileRemoved) file.PurgeReason = PurgeReasons.Blocked;
            }
            file.ConcurrencyVersion++;
            await uow.Db.SaveChangesAsync(ct);
            if (result.Verdict != ValidationVerdict.Error || finalFailure)
                await audit.WriteAsync(uow, null, "OEM_VALIDATION_RESULT", "oem_file", file.Id, new
                {
                    targetName = file.OriginalName, transferId, verdict = file.ScanStatus,
                    file.Sha256, reason = job.LastError, attempts = job.AttemptCount,
                }, ct);
            if (finalFailure && transfer.LifecycleStatus == TransferLifecycle.Draft && file.PurgeReason != PurgeReasons.FileRemoved)
                uow.Raise(new DraftFileValidationFailedEvent(transferId, file.Id, file.ScanStatus));
            if (finalFailure) await progression.AdvanceAsync(uow, transferId, null, ct);
            await dispatcher.CommitAsync(uow, ct);
        }
        if (promotionId is not null) await promotions.PromoteAsync(promotionId, ct);
    }

    /// <summary>Time budget for reading and hashing a file of the given size (validation timeout, promotion lease).</summary>
    internal static TimeSpan Timeout(ulong sizeBytes)
    {
        var gigabytes = Math.Ceiling(sizeBytes / (1024d * 1024 * 1024));
        return TimeSpan.FromMinutes(15 + 2 * gigabytes);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

public static class PromotionStatuses
{
    public const string Prepared = "PREPARED";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

/// <summary>
/// Moves a validated file from quarantine to the available area. The intent (source and
/// target paths) is committed before the rename, so a crash at any point can be
/// resumed: only-source → move; only-target with matching size → commit; both →
/// verify and drop the source; neither → the content is lost and never released.
/// </summary>
public sealed class OemPromotionService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemTransferProgression progression,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    ILogger<OemPromotionService> logger)
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int MaximumAttempts = 10;
    private readonly OemItemBackoff<string> backoff = new();

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        string[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            var suppressed = backoff.Suppressed();
            candidates = await uow.Db.OemFilePromotions.AsNoTracking()
                .Where(item => item.Status == PromotionStatuses.Prepared && (item.LeaseUntil == null || item.LeaseUntil <= uow.Now)
                    && (item.NextAttemptAt == null || item.NextAttemptAt <= uow.Now))
                .Where(item => !suppressed.Contains(item.Id))
                .OrderBy(item => item.CreatedAt).Select(item => item.Id).Take(10).ToArrayAsync(ct);
        }
        var done = 0;
        foreach (var id in candidates)
        {
            try
            {
                if (await PromoteAsync(id, ct)) done++;
                backoff.Succeeded(id);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                // A promotion that keeps throwing is backed off so the later ones still run.
                backoff.Failed(id);
                logger.LogError(error, "OEM promotion {PromotionId} failed; it is retried later.", id);
            }
        }
        return done;
    }

    public async Task<bool> PromoteAsync(string promotionId, CancellationToken ct)
    {
        OemFilePromotion claim;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var promotion = await LockAsync(uow, promotionId, ct);
            if (promotion is null || promotion.Status != PromotionStatuses.Prepared || promotion.LeaseUntil > uow.Now) return false;
            promotion.LeaseOwner = Owner;
            // Scales with the file like the validation timeout; the worst case hashes it twice
            // (an existing target is verified, then the final check runs).
            promotion.LeaseUntil = uow.Now.Add(OemFileValidationService.Timeout(promotion.SizeBytes) * 2);
            promotion.ConcurrencyVersion++;
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            claim = promotion;
        }

        string? failure = null;
        var lost = false;
        try
        {
            var source = storage.Absolute(claim.SourcePath);
            var target = storage.Absolute(claim.TargetPath);
            var sourceExists = File.Exists(source);
            var targetExists = File.Exists(target);
            if (sourceExists && !targetExists)
            {
                storage.EnsureParent(target, ct);
                File.Move(source, target, overwrite: false);
            }
            else if (sourceExists && targetExists)
            {
                failure = await ValidateTargetAsync(target, claim.SizeBytes, claim.FileSha256, ct);
                if (failure is not null) failure = "正式区已存在内容不同的同名文件：" + failure;
                else File.Delete(source);
            }
            else if (!targetExists)
            {
                lost = true;
            }
            if (failure is null && !lost) failure = await ValidateTargetAsync(target, claim.SizeBytes, claim.FileSha256, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReleaseAsync(claim);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = "移动文件失败";
            logger.LogWarning(error, "OEM promotion {PromotionId} failed.", promotionId);
        }

        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            // Lock order: transfer → file → promotion (see OemFileValidationService).
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == claim.FileId).Select(item => item.TransferId).SingleAsync(ct);
            var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {claim.FileId} FOR UPDATE").SingleAsync(ct);
            var promotion = await LockAsync(uow, promotionId, ct);
            if (promotion is null || promotion.ConcurrencyVersion != claim.ConcurrencyVersion || promotion.Status != PromotionStatuses.Prepared) return false;
            promotion.LeaseOwner = null;
            promotion.LeaseUntil = null;
            promotion.ConcurrencyVersion++;
            if (failure is null && !lost)
            {
                promotion.Status = PromotionStatuses.Completed;
                promotion.CompletedAt = uow.Now;
                file.StoragePath = promotion.TargetPath;
                file.PayloadStatus = PayloadStatuses.Available;
            }
            else if (lost)
            {
                promotion.Status = PromotionStatuses.Failed;
                promotion.LastError = "源文件与目标文件均不存在";
                file.PayloadStatus = PayloadStatuses.StorageLost;
                file.ScanStatus = ValidationStatuses.Error;
                uow.Raise(new FileMissingEvent(transferId, file.Id, PayloadStatuses.StorageLost));
            }
            else
            {
                promotion.AttemptCount++;
                promotion.LastError = failure;
                if (promotion.AttemptCount >= MaximumAttempts)
                {
                    promotion.Status = PromotionStatuses.Failed;
                    file.ScanStatus = ValidationStatuses.Error;
                    if (transfer.LifecycleStatus == TransferLifecycle.Draft)
                        uow.Raise(new DraftFileValidationFailedEvent(transferId, file.Id, ValidationStatuses.Error));
                }
                else promotion.NextAttemptAt = uow.Now.AddSeconds(Math.Min(3600, 30 * (1 << Math.Min(promotion.AttemptCount, 7))));
            }
            file.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            if (promotion.Status == PromotionStatuses.Failed && transfer.LifecycleStatus == TransferLifecycle.Draft)
            {
                var due = uow.Now.Add((await OemSettings.LoadAsync(uow.Db, ct)).BlockedRetention);
                if (file.PurgeDueAt is null || file.PurgeDueAt > due) file.PurgeDueAt = due;
                if (file.PurgeReason != PurgeReasons.FileRemoved) file.PurgeReason = PurgeReasons.Blocked;
            }
            await uow.Db.SaveChangesAsync(ct);
            if (lost || promotion.Status == PromotionStatuses.Failed)
                await audit.WriteAsync(uow, null, "OEM_PROMOTION_FAILED", "oem_file", file.Id,
                    new { targetName = file.OriginalName, transferId, reason = promotion.LastError }, ct);
            if (file.PayloadStatus == PayloadStatuses.Available || promotion.Status == PromotionStatuses.Failed)
                await progression.AdvanceAsync(uow, transferId, null, ct);
            await dispatcher.CommitAsync(uow, ct);
            return file.PayloadStatus == PayloadStatuses.Available;
        }
    }

    /// <summary>Clears this worker's promotion lease on shutdown (fenced by version) so a restart resumes at once.</summary>
    private async Task ReleaseAsync(OemFilePromotion claim)
    {
        using var timeout = new CancellationTokenSource(OemFileValidationService.ReleaseTimeout);
        try
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, timeout.Token);
            var promotion = await LockAsync(uow, claim.Id, timeout.Token);
            if (promotion is null || promotion.ConcurrencyVersion != claim.ConcurrencyVersion || promotion.Status != PromotionStatuses.Prepared) return;
            promotion.LeaseOwner = null;
            promotion.LeaseUntil = null;
            promotion.ConcurrencyVersion++;
            await uow.Db.SaveChangesAsync(timeout.Token);
            await uow.CommitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "OEM promotion {PromotionId} could not be released on shutdown; it resumes after its lease expires.", claim.Id);
        }
    }

    internal static async Task<string?> ValidateTargetAsync(string path, ulong sizeBytes, string expectedSha256, CancellationToken ct)
    {
        if ((ulong)new FileInfo(path).Length != sizeBytes) return "文件大小与记录不符";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) ? null : "SHA-256 与校验快照不符";
    }

    private static Task<OemFilePromotion?> LockAsync(OemUnitOfWork uow, string id, CancellationToken ct) =>
        uow.Db.OemFilePromotions.FromSqlInterpolated($"SELECT * FROM oem_file_promotions WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
