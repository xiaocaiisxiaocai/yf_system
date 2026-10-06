using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Approval;

public sealed record ApprovalDecisionRequest(ulong? Version, string? Reason);
public sealed record ReassignTaskRequest(ulong NewApproverUserId, string Reason, ulong? Version);
public sealed record CancelTransferRequest(string Reason, ulong? Version);

/// <summary>
/// Approver and recovery operations. Lock order is always transfer → instance → task,
/// matching the transfer progression, so concurrent approvals, reassignments and validation
/// completions serialise without deadlocks; the task version rejects stale decisions.
/// </summary>
public sealed class OemApprovalService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemApprovalEngine engine,
    OemTransferProgression progression,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    OemTransferReader reader)
{
    public async Task<IReadOnlyList<OemPendingApprovalResponse>> PendingAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowApprove, ct);
        var db = uow.Db;
        var rows = await (
            from task in db.OemFlowTasks
            where task.ApproverUserId == current.Id && task.Status == FlowTaskStatuses.Pending
            join instance in db.OemFlowInstances on task.InstanceId equals instance.Id
            where instance.Status == FlowInstanceStatuses.InProgress
            join node in db.OemFlowInstanceNodes on task.InstanceNodeId equals node.Id
            join transfer in db.OemTransfers on instance.TransferId equals transfer.Id
            join company in db.OemCompanies on transfer.OemCompanyId equals company.Id
            join sender in db.Users on instance.InitiatorUserId equals sender.Id
            orderby task.CreatedAt
            select new OemPendingApprovalResponse(
                task.Id, task.ConcurrencyVersion, transfer.Id, transfer.Title, company.Name,
                sender.RealName, sender.EmployeeNo, node.Name, node.ApprovalMode,
                transfer.SentAt, instance.UpdatedAt)).AsNoTracking().ToArrayAsync(ct);
        return rows;
    }

    public async Task<OemTransferDetailResponse> ApproveAsync(OemActor actor, ulong taskId, ApprovalDecisionRequest request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowApprove, ct);
        var (transfer, instance, task) = await LockAsync(uow, taskId, ct);
        EnsureDecidable(current, transfer, instance, task, version);
        var nodeComplete = await engine.ApproveAsync(uow, instance, task, ct);
        await audit.WriteAsync(uow, current, "OEM_APPROVAL_APPROVE", "oem_transfer", transfer.Id,
            new { targetName = transfer.Title, taskId, reason = OemValidation.OptionalText(request.Reason, "审批意见", 1024) }, ct);
        if (nodeComplete) await progression.ContinueApprovalAsync(uow, transfer, instance, current, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.DetailAsync(current, transfer.Id, ct);
    }

    public async Task<OemTransferDetailResponse> RejectAsync(OemActor actor, ulong taskId, ApprovalDecisionRequest request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        var reason = OemValidation.RequiredText(request.Reason, "驳回原因", 512);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowApprove, ct);
        var (transfer, instance, task) = await LockAsync(uow, taskId, ct);
        EnsureDecidable(current, transfer, instance, task, version);
        await engine.RejectAsync(uow, instance, task, reason, ct);
        await progression.CloseAsync(uow, transfer, TransferLifecycle.Rejected, reason, current, PurgeReasons.Rejected, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.DetailAsync(current, transfer.Id, ct);
    }

    /// <summary>Recovery: replace the approver of an unfinished task (never approve on someone's behalf).</summary>
    public async Task<OemTransferDetailResponse> ReassignAsync(OemActor actor, ulong taskId, ReassignTaskRequest request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        var reason = OemValidation.RequiredText(request.Reason, "改派原因", 512);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.ApprovalRecover, ct);
        var (transfer, instance, task) = await LockAsync(uow, taskId, ct);
        OemValidation.MatchVersion(task.ConcurrencyVersion, version);
        var canAdministerTransfers = await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferView, ct);
        if (canAdministerTransfers)
        {
            if (transfer.LifecycleStatus != TransferLifecycle.Sealed || !FlowInstanceStatuses.IsOpen(instance.Status))
                throw ApiException.Conflict("审批流程已结束，不能改派");
        }
        else
        {
            EnsureRestrictedRecoveryScope(transfer, instance);
        }
        if (!FlowTaskStatuses.IsOpen(task.Status)) throw ApiException.Conflict("任务已处理或已改派，请刷新后重试");
        var previous = task.ApproverUserId;
        var replacement = await engine.ReassignAsync(uow, instance, task, request.NewApproverUserId, reason, current.Id, ct);
        await audit.WriteAsync(uow, current, "OEM_APPROVAL_REASSIGN", "oem_transfer", transfer.Id, new
        {
            targetName = transfer.Title, taskId, newTaskId = replacement.Id, reason,
            changes = AuditChange.OnlyChanged(new AuditChange("approver", "审批人", previous, request.NewApproverUserId)),
        }, ct);
        if (replacement.Status == FlowTaskStatuses.Pending) uow.Raise(new ApprovalTasksActivatedEvent(transfer.Id, [replacement.Id]));
        if (instance.Status == FlowInstanceStatuses.ApprovalBlocked
            && (await TransferFilesState.LoadAsync(uow.Db, transfer.Id, ct)).AllReady)
            await progression.ContinueApprovalAsync(uow, transfer, instance, current, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.RecoveryResultAsync(current, transfer.Id, ct);
    }

    /// <summary>Recovery: terminate a transfer that has not been released yet.</summary>
    public async Task<OemTransferDetailResponse> CancelAsync(OemActor actor, ulong transferId, CancelTransferRequest request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        var reason = OemValidation.RequiredText(request.Reason, "终止原因", 512);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.ApprovalRecover, ct);
        var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
        OemValidation.MatchVersion(transfer.ConcurrencyVersion, version);
        var canAdministerTransfers = await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferView, ct);
        if (canAdministerTransfers)
        {
            if (transfer.LifecycleStatus != TransferLifecycle.Sealed)
                throw ApiException.Conflict("只能终止已发送但尚未发布的传递单");
        }
        else
        {
            var instance = await OemApprovalEngine.LockInstanceByTransferAsync(uow, transferId, ct)
                ?? throw ApiException.Conflict("该传递单不属于审批阻断异常，不能执行恢复操作");
            EnsureRestrictedRecoveryScope(transfer, instance);
        }
        await progression.CloseAsync(uow, transfer, TransferLifecycle.Cancelled, reason, current, PurgeReasons.Cancelled, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.RecoveryResultAsync(current, transferId, ct);
    }

    /// <summary>
    /// Maintenance: re-validates the active node of every in-progress instance so that a
    /// disabled or de-authorised approver blocks the flow (and notifies) instead of
    /// silently stalling it.
    /// </summary>
    public async Task<int> RevalidateActiveInstancesAsync(CancellationToken ct)
    {
        var blocked = 0;
        var afterTransferId = 0UL;
        while (true)
        {
            ulong[] transferIds;
            await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
                transferIds = await read.Db.OemFlowInstances.AsNoTracking()
                    .Where(instance => instance.Status == FlowInstanceStatuses.InProgress && instance.TransferId > afterTransferId)
                    .OrderBy(instance => instance.TransferId).Select(instance => instance.TransferId).Take(500).ToArrayAsync(ct);
            if (transferIds.Length == 0) break;
            foreach (var transferId in transferIds)
            {
                await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
                var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
                var instance = await OemApprovalEngine.LockInstanceByTransferAsync(uow, transferId, ct);
                if (transfer.LifecycleStatus != TransferLifecycle.Sealed || instance is null) continue;
                var reason = await engine.RevalidateActiveNodeAsync(uow, instance, ct);
                if (reason is not null)
                {
                    await audit.WriteAsync(uow, null, "OEM_APPROVAL_BLOCKED", "oem_transfer", transferId, new { targetName = transfer.Title, reason }, ct);
                    uow.Raise(new ApprovalBlockedEvent(transferId, reason));
                    blocked++;
                }
                await dispatcher.CommitAsync(uow, ct);
            }
            afterTransferId = transferIds[^1];
        }
        return blocked;
    }

    private static void EnsureDecidable(OemActor actor, OemTransfer transfer, OemFlowInstance instance, OemFlowTask task, ulong version)
    {
        if (task.ApproverUserId != actor.Id) throw ApiException.Forbidden("该审批任务不属于当前账号");
        OemValidation.MatchVersion(task.ConcurrencyVersion, version);
        if (task.Status != FlowTaskStatuses.Pending) throw ApiException.Conflict("任务已处理或已改派，请刷新后重试");
        if (transfer.LifecycleStatus != TransferLifecycle.Sealed || instance.Status != FlowInstanceStatuses.InProgress)
            throw ApiException.Conflict("审批流程当前不可处理，请刷新后重试");
    }

    private static void EnsureRestrictedRecoveryScope(OemTransfer transfer, OemFlowInstance instance)
    {
        if (transfer.Direction != TransferDirections.InternalToOem
            || transfer.LifecycleStatus != TransferLifecycle.Sealed
            || instance.Status != FlowInstanceStatuses.ApprovalBlocked)
            throw ApiException.Conflict("该传递单不属于审批阻断异常，不能执行恢复操作");
    }

    private static async Task<(OemTransfer Transfer, OemFlowInstance Instance, OemFlowTask Task)> LockAsync(OemUnitOfWork uow, ulong taskId, CancellationToken ct)
    {
        var located = await uow.Db.OemFlowTasks.AsNoTracking().Where(task => task.Id == taskId)
            .Join(uow.Db.OemFlowInstances, task => task.InstanceId, instance => instance.Id, (task, instance) => new { instance.TransferId, InstanceId = instance.Id })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        var transfer = await OemTransferProgression.LockTransferAsync(uow, located.TransferId, ct);
        var instance = await OemApprovalEngine.LockInstanceAsync(uow, located.InstanceId, ct);
        var task = await uow.Db.OemFlowTasks.FromSqlInterpolated($"SELECT * FROM oem_flow_tasks WHERE id = {taskId} FOR UPDATE").SingleAsync(ct);
        return (transfer, instance, task);
    }
}
