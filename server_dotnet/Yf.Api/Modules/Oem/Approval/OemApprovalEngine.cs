using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Approval;

public enum ActivationOutcome { Activated, Blocked, Completed }

public sealed record ActivationResult(ActivationOutcome Outcome, IReadOnlyList<ulong> ActivatedTaskIds, string? BlockedReason)
{
    /// <summary>Tasks approved automatically because their approver already approved an earlier node of the instance.</summary>
    public IReadOnlyList<ulong> AutoApprovedTaskIds { get; init; } = [];
}

/// <summary>
/// State machine of one approval instance (instance → nodes → tasks). It never touches
/// the transfer lifecycle; the transfer progression (process manager) calls it and
/// decides what the outcome means for the transfer. All methods run inside the
/// caller's unit of work and expect the instance row to be locked by the caller.
/// </summary>
public sealed class OemApprovalEngine
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>Persists the frozen plan at send time. The instance waits for validated files (or is SKIPPED when nobody has to approve).</summary>
    internal async Task<OemFlowInstance> CreateAsync(OemUnitOfWork uow, OemTransfer transfer, ulong initiatorUserId, PlanningResult planning, CancellationToken ct)
    {
        var plan = planning.Plan;
        var instance = new OemFlowInstance
        {
            TransferId = transfer.Id,
            TemplateId = planning.Template.Id,
            InitiatorUserId = initiatorUserId,
            InitiatorSectionId = planning.Path.Own.Id,
            Status = plan.RequiresNoApproval ? FlowInstanceStatuses.Skipped : FlowInstanceStatuses.WaitingFiles,
            TemplateSnapshot = JsonSerializer.Serialize(new
            {
                template = new { planning.Template.Id, planning.Template.Name, planning.Template.Version },
                matchedScope = planning.MatchedScope,
                orgPath = planning.Path,
                nodes = planning.Template.Nodes,
                plan = plan.Nodes,
            }, SnapshotJson),
            ConcurrencyVersion = 0,
            CreatedAt = uow.Now,
            UpdatedAt = uow.Now,
        };
        uow.Db.OemFlowInstances.Add(instance);
        await uow.Db.SaveChangesAsync(ct);
        foreach (var planned in plan.Nodes.OrderBy(node => node.SortNo))
        {
            var node = new OemFlowInstanceNode
            {
                InstanceId = instance.Id, SortNo = planned.SortNo, Name = planned.Name, ApproverSource = planned.Source,
                ApprovalMode = planned.Mode, Status = planned.Skipped ? FlowNodeStatuses.Skipped : FlowNodeStatuses.Waiting,
                SkipReason = planned.SkipReason, UsedFallback = planned.UsedFallback,
                CompletedAt = planned.Skipped ? uow.Now : null,
            };
            uow.Db.OemFlowInstanceNodes.Add(node);
            await uow.Db.SaveChangesAsync(ct);
            uow.Db.OemFlowTasks.AddRange(planned.Approvers.Select(approver => new OemFlowTask
            {
                InstanceId = instance.Id, InstanceNodeId = node.Id, ApproverUserId = approver, Status = FlowTaskStatuses.Waiting,
                ConcurrencyVersion = 0, CreatedAt = uow.Now,
            }));
        }
        await uow.Db.SaveChangesAsync(ct);
        return instance;
    }

    public static async Task<OemFlowInstance?> LockInstanceByTransferAsync(OemUnitOfWork uow, ulong transferId, CancellationToken ct) =>
        await uow.Db.OemFlowInstances.FromSqlInterpolated($"SELECT * FROM oem_flow_instances WHERE transfer_id = {transferId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    public static async Task<OemFlowInstance> LockInstanceAsync(OemUnitOfWork uow, ulong instanceId, CancellationToken ct) =>
        await OemLocks.ForUpdate<OemFlowInstance>(uow.Db, instanceId)
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    internal const string AutoApprovedReason = "已在前序节点审批通过，自动通过";

    /// <summary>
    /// Activates the next waiting node after re-validating its approvers. A task whose approver already
    /// approved an earlier node of this instance is approved automatically; a node completed that way
    /// (any-sign, or all-sign with nobody left) is passed and the following node is tried. Returns
    /// Completed when no node is left, Blocked (instance APPROVAL_BLOCKED) when the node
    /// cannot be decided by anyone eligible.
    /// </summary>
    internal async Task<ActivationResult> ActivateNextAsync(OemUnitOfWork uow, OemFlowInstance instance, CancellationToken ct)
    {
        var autoApproved = new List<ulong>();
        while (true)
        {
            var node = await uow.Db.OemFlowInstanceNodes
                .Where(item => item.InstanceId == instance.Id && item.Status == FlowNodeStatuses.Waiting)
                .OrderBy(item => item.SortNo).FirstOrDefaultAsync(ct);
            if (node is null)
            {
                instance.Status = FlowInstanceStatuses.Completed;
                instance.CurrentSortNo = null;
                instance.BlockedReason = null;
                Touch(uow, instance);
                await uow.Db.SaveChangesAsync(ct);
                return new ActivationResult(ActivationOutcome.Completed, [], null) { AutoApprovedTaskIds = autoApproved };
            }
            var tasks = await uow.Db.OemFlowTasks
                .Where(task => task.InstanceNodeId == node.Id && (task.Status == FlowTaskStatuses.Waiting || task.Status == FlowTaskStatuses.Pending))
                .ToArrayAsync(ct);
            var approvedBefore = (await uow.Db.OemFlowTasks.AsNoTracking()
                .Where(task => task.InstanceId == instance.Id && task.InstanceNodeId != node.Id && task.Status == FlowTaskStatuses.Approved)
                .Select(task => task.ApproverUserId).ToArrayAsync(ct)).ToHashSet();
            var repeated = tasks.Where(task => approvedBefore.Contains(task.ApproverUserId)).ToArray();
            foreach (var task in repeated)
            {
                task.Status = FlowTaskStatuses.Approved;
                task.Reason = AutoApprovedReason;
                task.DecidedAt = uow.Now;
                task.ConcurrencyVersion++;
                autoApproved.Add(task.Id);
            }
            tasks = tasks.Except(repeated).ToArray();
            if (repeated.Length > 0 && (node.ApprovalMode != ApprovalModes.All || tasks.Length == 0))
            {
                foreach (var task in tasks)
                {
                    task.Status = FlowTaskStatuses.NotNeeded;
                    task.DecidedAt = uow.Now;
                    task.ConcurrencyVersion++;
                }
                node.Status = FlowNodeStatuses.Approved;
                node.CompletedAt = uow.Now;
                await uow.Db.SaveChangesAsync(ct);
                continue;
            }
            var result = await ActivateNodeAsync(uow, instance, node, tasks, ct);
            return result with { AutoApprovedTaskIds = autoApproved };
        }
    }

    private async Task<ActivationResult> ActivateNodeAsync(OemUnitOfWork uow, OemFlowInstance instance, OemFlowInstanceNode node,
        OemFlowTask[] tasks, CancellationToken ct)
    {
        var eligible = (await ApprovalPlanningService.EligibleIdsAsync(uow.Db, tasks.Select(task => task.ApproverUserId), ct)).ToHashSet();
        var usable = tasks.Where(task => eligible.Contains(task.ApproverUserId)).ToArray();
        var blocked = node.ApprovalMode == ApprovalModes.Any ? usable.Length == 0 : usable.Length != tasks.Length || tasks.Length == 0;
        instance.CurrentSortNo = node.SortNo;
        if (blocked)
        {
            instance.Status = FlowInstanceStatuses.ApprovalBlocked;
            instance.BlockedReason = $"节点「{node.Name}」的审批人已停用或失去 ODM/OEMA 审批权限";
            Touch(uow, instance);
            await uow.Db.SaveChangesAsync(ct);
            return new ActivationResult(ActivationOutcome.Blocked, [], instance.BlockedReason);
        }
        foreach (var task in tasks)
        {
            if (usable.Contains(task))
            {
                if (task.Status != FlowTaskStatuses.Pending) task.ActivatedAt = uow.Now;
                task.Status = FlowTaskStatuses.Pending;
            }
            else
            {
                // ANY node: an approver that lost eligibility simply drops out.
                task.Status = FlowTaskStatuses.Cancelled;
                task.DecidedAt = uow.Now;
            }
            task.ConcurrencyVersion++;
        }
        node.Status = FlowNodeStatuses.Pending;
        instance.Status = FlowInstanceStatuses.InProgress;
        instance.BlockedReason = null;
        Touch(uow, instance);
        await uow.Db.SaveChangesAsync(ct);
        return new ActivationResult(ActivationOutcome.Activated, usable.Select(task => task.Id).ToArray(), null);
    }

    /// <summary>Records an approval. Returns true when the task's node is now complete.</summary>
    internal async Task<bool> ApproveAsync(OemUnitOfWork uow, OemFlowInstance instance, OemFlowTask task, CancellationToken ct)
    {
        var node = await uow.Db.OemFlowInstanceNodes.SingleAsync(item => item.Id == task.InstanceNodeId, ct);
        task.Status = FlowTaskStatuses.Approved;
        task.DecidedAt = uow.Now;
        task.ConcurrencyVersion++;
        var siblings = await uow.Db.OemFlowTasks
            .Where(item => item.InstanceNodeId == node.Id && item.Id != task.Id && (item.Status == FlowTaskStatuses.Pending || item.Status == FlowTaskStatuses.Waiting))
            .ToArrayAsync(ct);
        var nodeComplete = node.ApprovalMode != ApprovalModes.All || siblings.Length == 0;
        if (nodeComplete)
        {
            foreach (var sibling in siblings)
            {
                sibling.Status = FlowTaskStatuses.NotNeeded;
                sibling.DecidedAt = uow.Now;
                sibling.ConcurrencyVersion++;
            }
            node.Status = FlowNodeStatuses.Approved;
            node.CompletedAt = uow.Now;
        }
        Touch(uow, instance);
        await uow.Db.SaveChangesAsync(ct);
        return nodeComplete;
    }

    internal async Task RejectAsync(OemUnitOfWork uow, OemFlowInstance instance, OemFlowTask task, string reason, CancellationToken ct)
    {
        task.Status = FlowTaskStatuses.Rejected;
        task.Reason = reason;
        task.DecidedAt = uow.Now;
        task.ConcurrencyVersion++;
        var node = await uow.Db.OemFlowInstanceNodes.SingleAsync(item => item.Id == task.InstanceNodeId, ct);
        node.Status = FlowNodeStatuses.Rejected;
        node.CompletedAt = uow.Now;
        await CloseOpenAsync(uow, instance, FlowInstanceStatuses.Rejected, exceptTaskId: task.Id, ct);
    }

    /// <summary>Ends an open instance (cancel, reject, security block): every open node and task is closed.</summary>
    internal async Task CloseOpenAsync(OemUnitOfWork uow, OemFlowInstance instance, string finalStatus, ulong? exceptTaskId, CancellationToken ct)
    {
        var openTasks = await uow.Db.OemFlowTasks
            .Where(task => task.InstanceId == instance.Id && (task.Status == FlowTaskStatuses.Waiting || task.Status == FlowTaskStatuses.Pending))
            .ToArrayAsync(ct);
        foreach (var task in openTasks.Where(task => task.Id != exceptTaskId))
        {
            task.Status = FlowTaskStatuses.Cancelled;
            task.DecidedAt = uow.Now;
            task.ConcurrencyVersion++;
        }
        var openNodes = await uow.Db.OemFlowInstanceNodes
            .Where(node => node.InstanceId == instance.Id && (node.Status == FlowNodeStatuses.Waiting || node.Status == FlowNodeStatuses.Pending))
            .ToArrayAsync(ct);
        foreach (var node in openNodes)
        {
            node.Status = FlowNodeStatuses.Cancelled;
            node.CompletedAt = uow.Now;
        }
        instance.Status = finalStatus;
        instance.CurrentSortNo = null;
        Touch(uow, instance);
        await uow.Db.SaveChangesAsync(ct);
    }

    /// <summary>Re-validates the active node's pending approvers; blocks the instance when the node can no longer be decided.</summary>
    internal async Task<string?> RevalidateActiveNodeAsync(OemUnitOfWork uow, OemFlowInstance instance, CancellationToken ct)
    {
        if (instance.Status != FlowInstanceStatuses.InProgress) return null;
        var node = await uow.Db.OemFlowInstanceNodes.SingleOrDefaultAsync(item => item.InstanceId == instance.Id && item.Status == FlowNodeStatuses.Pending, ct);
        if (node is null) return null;
        var pending = await uow.Db.OemFlowTasks.Where(task => task.InstanceNodeId == node.Id && task.Status == FlowTaskStatuses.Pending).ToArrayAsync(ct);
        var eligible = (await ApprovalPlanningService.EligibleIdsAsync(uow.Db, pending.Select(task => task.ApproverUserId), ct)).ToHashSet();
        var stillUsable = pending.Count(task => eligible.Contains(task.ApproverUserId));
        var blocked = node.ApprovalMode == ApprovalModes.Any ? stillUsable == 0 : stillUsable != pending.Length || pending.Length == 0;
        if (!blocked) return null;
        // The node goes back to WAITING; the transfer can then only be terminated (approvers are not reassigned).
        foreach (var task in pending)
        {
            task.Status = FlowTaskStatuses.Waiting;
            task.ConcurrencyVersion++;
        }
        node.Status = FlowNodeStatuses.Waiting;
        instance.Status = FlowInstanceStatuses.ApprovalBlocked;
        instance.BlockedReason = $"节点「{node.Name}」的审批人已停用或失去 ODM/OEMA 审批权限";
        Touch(uow, instance);
        await uow.Db.SaveChangesAsync(ct);
        return instance.BlockedReason;
    }

    private static void Touch(OemUnitOfWork uow, OemFlowInstance instance)
    {
        instance.ConcurrencyVersion++;
        instance.UpdatedAt = uow.Now;
    }
}
