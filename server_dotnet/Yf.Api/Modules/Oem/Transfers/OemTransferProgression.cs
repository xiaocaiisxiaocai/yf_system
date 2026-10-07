using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Modules.Oem.Transfers;

/// <summary>Aggregate state of the files that belong to a transfer (removed draft files excluded).</summary>
public sealed record TransferFilesState(int Count, bool AnyValidationFailure, bool AllReady)
{
    public static async Task<TransferFilesState> LoadAsync(YfDbContext db, ulong transferId, CancellationToken ct)
    {
        var files = await OemTransferFiles.Members(db, transferId)
            .Select(file => new { file.ScanStatus, file.PayloadStatus }).ToArrayAsync(ct);
        return new TransferFilesState(
            files.Length,
            files.Any(file => !ValidationStatuses.IsKnown(file.ScanStatus)
                || file.ScanStatus is ValidationStatuses.Invalid or ValidationStatuses.Error),
            files.Length > 0 && files.All(file => file.ScanStatus == ValidationStatuses.Valid && file.PayloadStatus == PayloadStatuses.Available));
    }
}

public static class OemTransferFiles
{
    /// <summary>Files that are part of the transfer's manifest (files removed from a draft are excluded).</summary>
    public static IQueryable<OemTransferFile> Members(YfDbContext db, ulong transferId) =>
        db.OemTransferFiles.Where(file => file.TransferId == transferId && (file.PurgeReason == null || file.PurgeReason != PurgeReasons.FileRemoved));
}

/// <summary>
/// Process manager for a sent transfer. It is the only place that decides when a
/// SEALED transfer is released, blocked, rejected or cancelled, and it is invoked
/// after every event that can change that decision: send, validation result, promotion,
/// approval decision, reassignment. Release and closure schedule file purges and raise
/// domain events in the same transaction.
/// </summary>
public sealed class OemTransferProgression(OemApprovalEngine engine, OemAuditWriter audit)
{
    /// <summary>Re-evaluates a transfer. No-op unless it is SEALED.</summary>
    internal async Task AdvanceAsync(OemUnitOfWork uow, ulong transferId, OemActor? actor, CancellationToken ct)
    {
        var transfer = await LockTransferAsync(uow, transferId, ct);
        if (transfer.LifecycleStatus != TransferLifecycle.Sealed) return;
        var files = await TransferFilesState.LoadAsync(uow.Db, transferId, ct);
        if (files.AnyValidationFailure)
        {
            await CloseAsync(uow, transfer, TransferLifecycle.Blocked, "附件未通过文件校验", null, PurgeReasons.Blocked, ct);
            return;
        }
        if (!files.AllReady) return;
        if (transfer.Direction == TransferDirections.OemToInternal)
        {
            await ReleaseAsync(uow, transfer, actor, ct);
            return;
        }
        var instance = await OemApprovalEngine.LockInstanceByTransferAsync(uow, transferId, ct)
            ?? throw new InvalidOperationException("Outbound transfer has no approval instance.");
        switch (instance.Status)
        {
            case FlowInstanceStatuses.Skipped:
            case FlowInstanceStatuses.Completed:
                await ReleaseAsync(uow, transfer, actor, ct);
                break;
            case FlowInstanceStatuses.WaitingFiles:
            case FlowInstanceStatuses.ApprovalBlocked:
                await ContinueApprovalAsync(uow, transfer, instance, actor, ct);
                break;
        }
    }

    /// <summary>Activates the next approval node (or releases when approval is complete).</summary>
    internal async Task ContinueApprovalAsync(OemUnitOfWork uow, OemTransfer transfer, OemFlowInstance instance, OemActor? actor, CancellationToken ct)
    {
        var wasBlocked = instance.Status == FlowInstanceStatuses.ApprovalBlocked;
        var result = await engine.ActivateNextAsync(uow, instance, ct);
        switch (result.Outcome)
        {
            case ActivationOutcome.Completed:
                uow.Raise(new ApprovalCompletedEvent(transfer.Id));
                await ReleaseAsync(uow, transfer, actor, ct);
                break;
            case ActivationOutcome.Activated:
                uow.Raise(new ApprovalTasksActivatedEvent(transfer.Id, result.ActivatedTaskIds));
                break;
            case ActivationOutcome.Blocked when !wasBlocked:
                await audit.WriteAsync(uow, actor, "OEM_APPROVAL_BLOCKED", "oem_transfer", transfer.Id,
                    new { targetName = transfer.Title, reason = result.BlockedReason }, ct);
                uow.Raise(new ApprovalBlockedEvent(transfer.Id, result.BlockedReason!));
                break;
        }
    }

    internal const string RecipientUnavailableReason = "收件厂商已停用或没有可用账号，未发布";

    private static Task<bool> RecipientCanReceiveAsync(YfDbContext db, ulong companyId, CancellationToken ct) =>
        db.OemCompanies.AnyAsync(company => company.Id == companyId && company.Status == OemStatus.Active
            && db.OemAccounts.Any(account => account.OemCompanyId == company.Id && account.Status == OemStatus.Active), ct);

    internal async Task ReleaseAsync(OemUnitOfWork uow, OemTransfer transfer, OemActor? actor, CancellationToken ct)
    {
        TransferStateMachine.Ensure(transfer.LifecycleStatus, TransferLifecycle.Released);
        // Files can become undeliverable while approval runs (reconcile may mark one STORAGE_LOST),
        // so the release decision re-reads them under the transfer lock and fails closed.
        var files = await TransferFilesState.LoadAsync(uow.Db, transfer.Id, ct);
        if (!files.AllReady)
        {
            await CloseAsync(uow, transfer, TransferLifecycle.Blocked,
                files.AnyValidationFailure ? "附件未通过文件校验" : "附件已丢失或不可用，未能发布", null, PurgeReasons.Blocked, ct);
            return;
        }
        // The vendor may be disabled (or lose every enabled account) while approval runs; releasing then
        // would deliver to nobody, so the transfer is blocked instead (same predicate as at create/send time).
        if (transfer.Direction == TransferDirections.InternalToOem && !await RecipientCanReceiveAsync(uow.Db, transfer.OemCompanyId, ct))
        {
            await CloseAsync(uow, transfer, TransferLifecycle.Blocked, RecipientUnavailableReason, null, PurgeReasons.Blocked, ct);
            return;
        }
        var snapshot = new RetentionSnapshot(transfer.RetentionMode!, transfer.ReleaseTtlMinutes, transfer.ReceiptGraceMinutes);
        transfer.LifecycleStatus = TransferLifecycle.Released;
        transfer.ReleasedAt = uow.Now;
        transfer.ExpiresAt = snapshot.Strategy.ExpiresAt(snapshot, uow.Now);
        Touch(uow, transfer);
        var due = snapshot.Strategy.InitialPurgeDue(snapshot, uow.Now);
        if (due is not null)
            await OemTransferFiles.Members(uow.Db, transfer.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(file => file.PurgeDueAt, due)
                    .SetProperty(file => file.PurgeReason, PurgeReasons.Retention)
                    .SetProperty(file => file.UpdatedAt, uow.Now)
                    .SetProperty(file => file.ConcurrencyVersion, file => file.ConcurrencyVersion + 1), ct);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, actor, "OEM_TRANSFER_RELEASE", "oem_transfer", transfer.Id,
            new { targetName = transfer.Title, transfer.ExpiresAt, retentionMode = snapshot.Mode }, ct);
        uow.Raise(new TransferReleasedEvent(transfer.Id));
    }

    /// <summary>Ends a SEALED transfer without release and schedules its files for cleanup.</summary>
    internal async Task CloseAsync(OemUnitOfWork uow, OemTransfer transfer, string lifecycle, string reason, OemActor? actor,
        string purgeReason, CancellationToken ct)
    {
        TransferStateMachine.Ensure(transfer.LifecycleStatus, lifecycle);
        transfer.LifecycleStatus = lifecycle;
        transfer.ClosedReason = reason;
        transfer.ClosedByRealm = actor?.Realm ?? OemRealms.System;
        transfer.ClosedById = actor?.Id;
        transfer.ClosedAt = uow.Now;
        Touch(uow, transfer);
        await uow.Db.SaveChangesAsync(ct);
        if (transfer.Direction == TransferDirections.InternalToOem)
        {
            var instance = await OemApprovalEngine.LockInstanceByTransferAsync(uow, transfer.Id, ct);
            if (instance is not null && FlowInstanceStatuses.IsOpen(instance.Status))
                await engine.CloseOpenAsync(uow, instance, FlowInstanceStatuses.Cancelled, null, ct);
        }
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        await SchedulePurgeAsync(uow, transfer.Id, uow.Now.Add(settings.BlockedRetention), purgeReason, ct);
        await audit.WriteAsync(uow, actor, lifecycle switch
        {
            TransferLifecycle.Blocked => "OEM_TRANSFER_BLOCK",
            TransferLifecycle.Rejected => "OEM_TRANSFER_REJECT",
            _ => "OEM_TRANSFER_CANCEL",
        }, "oem_transfer", transfer.Id, new { targetName = transfer.Title, reason }, ct);
        uow.Raise(new TransferClosedEvent(transfer.Id, lifecycle, reason));
    }

    /// <summary>Brings every stored file of the transfer forward to <paramref name="due"/> (never later than an existing deadline).</summary>
    internal static async Task SchedulePurgeAsync(OemUnitOfWork uow, ulong transferId, DateTime due, string reason, CancellationToken ct)
    {
        var onDisk = PayloadStatuses.OnDisk;
        await uow.Db.OemTransferFiles
            .Where(file => file.TransferId == transferId && Enumerable.Contains(onDisk, file.PayloadStatus))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(file => file.PurgeDueAt, file => file.PurgeDueAt == null || file.PurgeDueAt > due ? due : file.PurgeDueAt)
                .SetProperty(file => file.PurgeReason, file => file.PurgeReason == PurgeReasons.FileRemoved ? file.PurgeReason : reason)
                .SetProperty(file => file.UpdatedAt, uow.Now)
                .SetProperty(file => file.ConcurrencyVersion, file => file.ConcurrencyVersion + 1), ct);
    }

    public static async Task<OemTransfer> LockTransferAsync(OemUnitOfWork uow, ulong transferId, CancellationToken ct) =>
        await uow.Db.OemTransfers.FromSqlInterpolated($"SELECT * FROM oem_transfers WHERE id = {transferId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound();

    internal static void Touch(OemUnitOfWork uow, OemTransfer transfer)
    {
        transfer.ConcurrencyVersion++;
        transfer.UpdatedAt = uow.Now;
    }
}
