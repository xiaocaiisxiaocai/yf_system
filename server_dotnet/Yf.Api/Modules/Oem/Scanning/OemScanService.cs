using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Scanning;

/// <summary>
/// Scan worker logic. Jobs are claimed with a lease and a version (fencing): a worker
/// that lost its lease — for example during an IIS overlapped recycle — can never
/// write a result. Temporary failures back off and retry; only exhausted retries,
/// threats and unscannable content are final, and every final failure fails closed.
/// </summary>
public sealed class OemScanService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemScanPipeline pipeline,
    OemTransferProgression progression,
    OemPromotionService promotions,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    AppOptions options,
    ILogger<OemScanService> logger)
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly TimeSpan UnavailableRetry = TimeSpan.FromMinutes(5);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        ulong[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            candidates = await uow.Db.OemFileScanJobs.AsNoTracking()
                .Where(job => (job.Status == ScanJobStatuses.Pending && (job.NextAttemptAt == null || job.NextAttemptAt <= uow.Now))
                    || (job.Status == ScanJobStatuses.Running && job.LeaseUntil <= uow.Now))
                .OrderBy(job => job.Id).Select(job => job.Id).Take(4).ToArrayAsync(ct);
        }
        var processed = 0;
        foreach (var jobId in candidates)
        {
            var claim = await ClaimAsync(jobId, ct);
            if (claim is null) continue;
            var result = await ExecuteAsync(claim, ct);
            await CompleteAsync(claim, result, ct);
            processed++;
        }
        return processed;
    }

    private sealed record Claim(ulong JobId, ulong Version, ulong FileId, string StoragePath, string Extension, ulong Size, string Sha256);

    private async Task<Claim?> ClaimAsync(ulong jobId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var job = await uow.Db.OemFileScanJobs.FromSqlInterpolated($"SELECT * FROM oem_file_scan_jobs WHERE id = {jobId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (job is null) return null;
        var claimable = (job.Status == ScanJobStatuses.Pending && (job.NextAttemptAt is null || job.NextAttemptAt <= uow.Now))
            || (job.Status == ScanJobStatuses.Running && job.LeaseUntil <= uow.Now);
        if (!claimable) return null;
        var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {job.FileId} FOR UPDATE").SingleAsync(ct);
        if (file.PayloadStatus != PayloadStatuses.Quarantined || file.Sha256 != job.FileSha256 || file.SizeBytes != job.FileSizeBytes)
        {
            // The file left quarantine or changed: the job is obsolete and must never produce a verdict.
            job.Status = ScanJobStatuses.Error;
            job.LastError = "文件已不在隔离区或内容已变化";
            job.CompletedAt = uow.Now;
            job.ConcurrencyVersion++;
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            return null;
        }
        job.Status = ScanJobStatuses.Running;
        job.LeaseOwner = Owner;
        job.LeaseUntil = uow.Now.Add(Timeout(file.SizeBytes)).AddMinutes(1);
        job.StartedAt = uow.Now;
        job.ConcurrencyVersion++;
        file.ScanStatus = ScanStatuses.Scanning;
        file.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await uow.CommitAsync(ct);
        return new Claim(job.Id, job.ConcurrencyVersion, file.Id, file.StoragePath, file.Ext, file.SizeBytes, file.Sha256);
    }

    private async Task<ScanResult> ExecuteAsync(Claim claim, CancellationToken ct)
    {
        string? workDirectory = null;
        try
        {
            var path = await storage.ResolveExistingFileAsync(claim.StoragePath, ct);
            if ((ulong)new FileInfo(path).Length != claim.Size)
                return pipeline.ExplainAccessFailure(new QuarantineContentChangedException())
                    ?? new ScanResult(ScanVerdict.Error, pipeline.EngineName, null, null, null, "隔离文件大小与记录不符");
            workDirectory = storage.SessionDirectory(Guid.NewGuid().ToString("D"), create: true, ct);
            ArchiveLimits limits;
            await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
                limits = (await OemSettings.LoadAsync(uow.Db, ct)).ArchiveLimits;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout(claim.Size));
            try
            {
                return await pipeline.RunAsync(new ScanTarget(path, claim.Sha256, claim.Size), claim.Extension, limits, workDirectory, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ScanResult(ScanVerdict.Error, pipeline.EngineName, null, null, null, "扫描超时");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && pipeline.ExplainAccessFailure(error) is ScanResult explained)
        {
            // The engine attributes the failure (e.g. the antivirus removed or locked the file).
            logger.LogWarning("OEM scan of file {FileId}: quarantined file intercepted ({ErrorType}).", claim.FileId, error.GetType().Name);
            return pipeline.ExplainAccessFailure(error)!;
        }
        catch (FileNotFoundException)
        {
            return new ScanResult(ScanVerdict.Error, pipeline.EngineName, null, null, null, "隔离文件不存在");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("OEM scan of file {FileId} failed ({ErrorType}).", claim.FileId, error.GetType().Name);
            return new ScanResult(ScanVerdict.Error, pipeline.EngineName, null, null, null, "扫描过程出错");
        }
        finally
        {
            if (workDirectory is not null)
            {
                try { await storage.DeleteDirectoryAsync(workDirectory, CancellationToken.None); }
                catch (Exception error) { logger.LogWarning("OEM scan work directory cleanup failed ({ErrorType}).", error.GetType().Name); }
            }
        }
    }

    private async Task CompleteAsync(Claim claim, ScanResult result, CancellationToken ct)
    {
        string? promotionId = null;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var job = await uow.Db.OemFileScanJobs.FromSqlInterpolated($"SELECT * FROM oem_file_scan_jobs WHERE id = {claim.JobId} FOR UPDATE").SingleAsync(ct);
            // Fencing: the lease was lost to another worker, or the job was changed meanwhile.
            if (job.ConcurrencyVersion != claim.Version || job.Status != ScanJobStatuses.Running) return;
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == claim.FileId).Select(item => item.TransferId).SingleAsync(ct);
            var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {claim.FileId} FOR UPDATE").SingleAsync(ct);
            var settings = await OemSettings.LoadAsync(uow.Db, ct);
            if (file.PayloadStatus != PayloadStatuses.Quarantined)
            {
                // The file was purged or removed while it was being scanned: the verdict is moot.
                job.Status = ScanJobStatuses.Error;
                job.LastError = "文件已不在隔离区";
                job.CompletedAt = uow.Now;
                job.LeaseOwner = null;
                job.LeaseUntil = null;
                job.ConcurrencyVersion++;
                await uow.Db.SaveChangesAsync(ct);
                await uow.CommitAsync(ct);
                return;
            }
            job.EngineName = result.EngineName;
            job.EngineVersion = result.EngineVersion;
            job.SignatureVersion = result.SignatureVersion;
            job.ThreatName = result.ThreatName;
            job.LastError = Truncate(result.Error, 1024);
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            job.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            var finalFailure = false;
            switch (result.Verdict)
            {
                case ScanVerdict.Clean:
                    job.Status = ScanJobStatuses.Clean;
                    job.CompletedAt = uow.Now;
                    file.ScanStatus = ScanStatuses.Clean;
                    file.PayloadStatus = PayloadStatuses.Promoting;
                    promotionId = Guid.NewGuid().ToString("D");
                    uow.Db.OemFilePromotions.Add(new OemFilePromotion
                    {
                        Id = promotionId, FileId = file.Id, FileSha256 = file.Sha256, SizeBytes = file.SizeBytes, SourcePath = file.StoragePath,
                        TargetPath = OemStorage.AvailableRelative(file.StoredName), Status = PromotionStatuses.Prepared, AttemptCount = 0,
                        ConcurrencyVersion = 0, CreatedAt = uow.Now,
                    });
                    break;
                case ScanVerdict.Infected:
                case ScanVerdict.Unscannable:
                    job.Status = result.Verdict == ScanVerdict.Infected ? ScanJobStatuses.Infected : ScanJobStatuses.Unscannable;
                    job.CompletedAt = uow.Now;
                    file.ScanStatus = result.Verdict == ScanVerdict.Infected ? ScanStatuses.Infected : ScanStatuses.Unscannable;
                    finalFailure = true;
                    break;
                case ScanVerdict.EngineUnavailable:
                    // No engine: stay quarantined without consuming attempts (fail closed, never auto-block).
                    job.Status = ScanJobStatuses.Pending;
                    job.NextAttemptAt = uow.Now.Add(UnavailableRetry);
                    file.ScanStatus = ScanStatuses.Pending;
                    break;
                default:
                    job.AttemptCount++;
                    if (job.AttemptCount > settings.ScanMaxRetries)
                    {
                        job.Status = ScanJobStatuses.Error;
                        job.CompletedAt = uow.Now;
                        file.ScanStatus = ScanStatuses.Error;
                        finalFailure = true;
                    }
                    else
                    {
                        job.Status = ScanJobStatuses.Pending;
                        job.NextAttemptAt = uow.Now.AddSeconds(Math.Min(3600, 30 * (1 << Math.Min(job.AttemptCount, 7))));
                        file.ScanStatus = ScanStatuses.Pending;
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
            if (result.Verdict is not (ScanVerdict.EngineUnavailable or ScanVerdict.Error) || finalFailure)
                await audit.WriteAsync(uow, null, "OEM_SCAN_RESULT", "oem_file", file.Id, new
                {
                    targetName = file.OriginalName, transferId, verdict = file.ScanStatus, job.EngineName, job.EngineVersion,
                    job.SignatureVersion, job.ThreatName, reason = job.LastError, attempts = job.AttemptCount,
                }, ct);
            if (finalFailure && transfer.LifecycleStatus == TransferLifecycle.Draft && file.PurgeReason != PurgeReasons.FileRemoved)
                uow.Raise(new DraftFileScanFailedEvent(transferId, file.Id, file.ScanStatus));
            if (finalFailure) await progression.AdvanceAsync(uow, transferId, null, ct);
            await dispatcher.CommitAsync(uow, ct);
        }
        if (promotionId is not null) await promotions.PromoteAsync(promotionId, ct);
    }

    private TimeSpan Timeout(ulong sizeBytes)
    {
        var gigabytes = Math.Ceiling(sizeBytes / (1024d * 1024 * 1024));
        return TimeSpan.FromSeconds(options.OemScanner.BaseTimeoutSeconds + gigabytes * options.OemScanner.TimeoutSecondsPerGb);
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
/// Moves a clean file from quarantine to the available area. The intent (source and
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

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        string[] candidates;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired) return 0;
            candidates = await uow.Db.OemFilePromotions.AsNoTracking()
                .Where(item => item.Status == PromotionStatuses.Prepared && (item.LeaseUntil == null || item.LeaseUntil <= uow.Now)
                    && (item.NextAttemptAt == null || item.NextAttemptAt <= uow.Now))
                .OrderBy(item => item.CreatedAt).Select(item => item.Id).Take(10).ToArrayAsync(ct);
        }
        var done = 0;
        foreach (var id in candidates)
            if (await PromoteAsync(id, ct)) done++;
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
            promotion.LeaseUntil = uow.Now.AddMinutes(10);
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
                if ((ulong)new FileInfo(target).Length != claim.SizeBytes) failure = "正式区已存在内容不同的同名文件";
                else File.Delete(source);
            }
            else if (!targetExists)
            {
                lost = true;
            }
            if (failure is null && !lost && (ulong)new FileInfo(target).Length != claim.SizeBytes) failure = "正式区文件大小与记录不符";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = "移动文件失败";
            logger.LogWarning("OEM promotion {PromotionId} failed ({ErrorType}).", promotionId, error.GetType().Name);
        }

        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var promotion = await LockAsync(uow, promotionId, ct);
            if (promotion is null || promotion.ConcurrencyVersion != claim.ConcurrencyVersion || promotion.Status != PromotionStatuses.Prepared) return false;
            var transferId = await uow.Db.OemTransferFiles.Where(item => item.Id == promotion.FileId).Select(item => item.TransferId).SingleAsync(ct);
            await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
            var file = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {promotion.FileId} FOR UPDATE").SingleAsync(ct);
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
                uow.Raise(new FileMissingEvent(transferId, file.Id, PayloadStatuses.StorageLost));
            }
            else
            {
                promotion.AttemptCount++;
                promotion.LastError = failure;
                if (promotion.AttemptCount >= MaximumAttempts) promotion.Status = PromotionStatuses.Failed;
                else promotion.NextAttemptAt = uow.Now.AddSeconds(Math.Min(3600, 30 * (1 << Math.Min(promotion.AttemptCount, 7))));
            }
            file.ConcurrencyVersion++;
            file.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            if (lost || promotion.Status == PromotionStatuses.Failed)
                await audit.WriteAsync(uow, null, "OEM_PROMOTION_FAILED", "oem_file", file.Id,
                    new { targetName = file.OriginalName, transferId, reason = promotion.LastError }, ct);
            if (file.PayloadStatus == PayloadStatuses.Available) await progression.AdvanceAsync(uow, transferId, null, ct);
            await dispatcher.CommitAsync(uow, ct);
            return file.PayloadStatus == PayloadStatuses.Available;
        }
    }

    private static Task<OemFilePromotion?> LockAsync(OemUnitOfWork uow, string id, CancellationToken ct) =>
        uow.Db.OemFilePromotions.FromSqlInterpolated($"SELECT * FROM oem_file_promotions WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}

/// <summary>The quarantined file no longer has the size recorded at upload.</summary>
public sealed class QuarantineContentChangedException() : IOException("隔离文件大小与记录不符");
