using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Modules.Oem.Transfers;

/// <summary>
/// Read model for transfers (query side). List scoping mirrors <see cref="TransferAccess"/>
/// in SQL; the detail view evaluates <see cref="TransferAccess"/> exactly and hides
/// approval internals from OEM accounts.
/// </summary>
public sealed class OemTransferReader(IDbContextFactory<YfDbContext> dbFactory)
{
    public async Task<object> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        var approvalStatusInput = request.Query["approvalStatus"].ToString();
        var approvalStatus = approvalStatusInput.Length == 0 ? null : NormalizeApprovalStatus(approvalStatusInput);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var db = uow.Db;
        IQueryable<OemTransfer> query;
        if (current is OemAccountActor account)
        {
            query = db.OemTransfers.Where(transfer => transfer.OemCompanyId == account.CompanyId
                && ((transfer.Direction == TransferDirections.OemToInternal
                        && (transfer.LifecycleStatus != TransferLifecycle.Draft || transfer.OemSenderAccountId == account.AccountId))
                    || (transfer.Direction == TransferDirections.InternalToOem && transfer.LifecycleStatus == TransferLifecycle.Released)));
        }
        else
        {
            var userId = current.Id;
            var canView = await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferView, ct);
            var canRecover = await OemAuthorizer.HasAsync(uow, current, OemPermissions.ApprovalRecover, ct);
            var taskTransfers = db.OemFlowTasks.Where(task => task.ApproverUserId == userId)
                .Join(db.OemFlowInstances, task => task.InstanceId, instance => instance.Id, (task, instance) => instance.TransferId);
            var blockedTransfers = db.OemFlowInstances.Where(instance => instance.Status == FlowInstanceStatuses.ApprovalBlocked)
                .Select(instance => instance.TransferId);
            query = db.OemTransfers.Where(transfer => transfer.InternalSenderUserId == userId
                || (canView && transfer.LifecycleStatus != TransferLifecycle.Draft)
                || (transfer.LifecycleStatus != TransferLifecycle.Draft && taskTransfers.Contains(transfer.Id))
                || (canRecover && approvalStatus == FlowInstanceStatuses.ApprovalBlocked
                    && transfer.Direction == TransferDirections.InternalToOem
                    && transfer.LifecycleStatus == TransferLifecycle.Sealed
                    && blockedTransfers.Contains(transfer.Id)));
        }
        var direction = request.Query["direction"].ToString();
        if (direction.Length > 0) query = query.Where(transfer => transfer.Direction == direction);
        var status = request.Query["status"].ToString();
        if (status.Length > 0) query = query.Where(transfer => transfer.LifecycleStatus == status);
        if (approvalStatus is not null)
            query = query.Where(transfer => db.OemFlowInstances.Any(instance => instance.TransferId == transfer.Id
                && instance.Status == approvalStatus));
        if (QueryValues.OptionalUInt64(request, "companyId") is ulong companyId) query = query.Where(transfer => transfer.OemCompanyId == companyId);
        if (request.Query["mine"] == "true")
            query = current is OemAccountActor ? query.Where(transfer => transfer.OemSenderAccountId == current.Id)
                : query.Where(transfer => transfer.InternalSenderUserId == current.Id);
        var keyword = request.Query["keyword"].ToString().Trim();
        if (keyword.Length > 0)
        {
            var pattern = "%" + keyword + "%";
            query = query.Where(transfer => EF.Functions.Like(transfer.Title, pattern));
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(transfer => transfer.Id).Page(offset, size).AsNoTracking().ToArrayAsync(ct);
        var summaries = await SummariesAsync(uow, rows, ct);
        return new { list = rows.Select(transfer => summaries[transfer.Id]), total, page, pageSize = size };
    }

    public Task<object> DetailAsync(OemActor actor, ulong id, CancellationToken ct) =>
        DetailAsync(actor, id, allowRecoveryResult: false, ct);

    /// <summary>
    /// Returns the just-completed recovery command result without granting the caller a
    /// durable read scope after the transfer leaves APPROVAL_BLOCKED.
    /// </summary>
    internal Task<object> RecoveryResultAsync(OemActor actor, ulong id, CancellationToken ct) =>
        DetailAsync(actor, id, allowRecoveryResult: true, ct);

    private async Task<object> DetailAsync(OemActor actor, ulong id, bool allowRecoveryResult, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transfer = await uow.Db.OemTransfers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct) ?? throw ApiException.NotFound();
        var capabilities = await CapabilitiesAsync(uow, current, transfer, ct);
        if (!capabilities.View && allowRecoveryResult && current is InternalOemActor
            && transfer.Direction == TransferDirections.InternalToOem
            && transfer.LifecycleStatus is not (TransferLifecycle.Draft or TransferLifecycle.Released)
            && await OemAuthorizer.HasAsync(uow, current, OemPermissions.ApprovalRecover, ct))
            capabilities = new TransferCapabilities(true, false, ContentPurpose.None, RecoveryOnly: true);
        if (!capabilities.View) throw ApiException.NotFound();
        var summary = (await SummariesAsync(uow, [transfer], ct))[transfer.Id];
        var files = capabilities.RecoveryOnly
            ? Array.Empty<OemTransferFile>()
            : await OemTransferFiles.Members(uow.Db, id).AsNoTracking().OrderBy(file => file.Id).ToArrayAsync(ct);
        var fileIds = files.Select(file => file.Id).ToArray();
        var jobs = await uow.Db.OemFileScanJobs.AsNoTracking().Where(job => Enumerable.Contains(fileIds, job.FileId))
            .Select(job => new { job.FileId, job.AttemptCount, job.LastError, job.Id }).ToArrayAsync(ct);
        var latestJobs = jobs.GroupBy(job => job.FileId).ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.Id).First());
        var retentionTemplate = await uow.Db.OemRetentionTemplates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == transfer.RetentionTemplateId, ct);
        object? approval = null;
        if (current is not OemAccountActor && transfer.Direction == TransferDirections.InternalToOem) approval = await ApprovalAsync(uow, id, ct);
        var now = uow.Now;
        return new
        {
            summary,
            description = capabilities.RecoveryOnly ? null : transfer.Description,
            retention = new
            {
                templateId = transfer.RetentionTemplateId,
                templateName = retentionTemplate?.Name,
                mode = transfer.RetentionMode ?? retentionTemplate?.Mode,
                releaseTtlMinutes = transfer.RetentionMode is null ? retentionTemplate?.ReleaseTtlMinutes : transfer.ReleaseTtlMinutes,
                receiptGraceMinutes = transfer.RetentionMode is null ? retentionTemplate?.ReceiptGraceMinutes : transfer.ReceiptGraceMinutes,
                summary = transfer.RetentionMode is not null
                    ? OemRetentionTemplateService.Describe(transfer.RetentionMode, transfer.ReleaseTtlMinutes, transfer.ReceiptGraceMinutes)
                    : retentionTemplate is null ? null : OemRetentionTemplateService.Describe(retentionTemplate.Mode, retentionTemplate.ReleaseTtlMinutes, retentionTemplate.ReceiptGraceMinutes),
            },
            manifestSha256 = capabilities.RecoveryOnly ? null : transfer.ManifestSha256,
            transfer.ExpiresAt,
            transfer.ClosedReason,
            transfer.ClosedAt,
            capabilities = new
            {
                canEdit = capabilities.EditDraft,
                canSend = capabilities.EditDraft,
                canDelete = capabilities.EditDraft,
                canReadContent = capabilities.CanReadContent,
                contentPurpose = capabilities.ContentAccess.ToString().ToUpperInvariant(),
            },
            files = files.Select(file =>
            {
                latestJobs.TryGetValue(file.Id, out var job);
                var ready = file.ScanStatus == ValidationStatuses.Valid && file.PayloadStatus == PayloadStatuses.Available
                    && (file.PurgeDueAt is null || file.PurgeDueAt > now);
                return new
                {
                    file.Id, file.OriginalName, file.Ext, file.SizeBytes, file.Sha256,
                    validationStatus = file.ScanStatus, file.PayloadStatus,
                    validationAttempts = job?.AttemptCount,
                    validationMessage = ValidationMessage(file.ScanStatus, job?.LastError),
                    file.CreatedAt, file.FirstRecipientDownloadAt, file.PurgeDueAt, file.PurgedAt,
                    downloadable = capabilities.CanReadContent && ready,
                };
            }),
            approval,
        };
    }

    public async Task<object> FileValidationStatusAsync(OemActor actor, ulong fileId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var file = await uow.Db.OemTransferFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fileId, ct) ?? throw ApiException.NotFound();
        var transfer = await uow.Db.OemTransfers.AsNoTracking().SingleAsync(item => item.Id == file.TransferId, ct);
        var capabilities = await CapabilitiesAsync(uow, current, transfer, ct);
        if (!capabilities.View || capabilities.RecoveryOnly) throw ApiException.NotFound();
        return new { file.Id, validationStatus = file.ScanStatus, file.PayloadStatus };
    }

    /// <summary>Capability evaluation shared by detail, preview and download.</summary>
    internal static async Task<TransferCapabilities> CapabilitiesAsync(OemUnitOfWork uow, OemActor actor, OemTransfer transfer, CancellationToken ct)
    {
        IReadOnlySet<string> permissions = new HashSet<string>();
        bool activeTask = false, anyTask = false, approvalBlocked = false;
        if (actor is InternalOemActor)
        {
            permissions = await OemAuthorizer.InternalPermissionsAsync(uow, actor.Id, ct);
            if (transfer.Direction == TransferDirections.InternalToOem)
            {
                approvalBlocked = await uow.Db.OemFlowInstances.AsNoTracking()
                    .AnyAsync(instance => instance.TransferId == transfer.Id
                        && instance.Status == FlowInstanceStatuses.ApprovalBlocked, ct);
                var tasks = await uow.Db.OemFlowTasks.AsNoTracking()
                    .Join(uow.Db.OemFlowInstances.Where(instance => instance.TransferId == transfer.Id), task => task.InstanceId, instance => instance.Id,
                        (task, instance) => task)
                    .Where(task => task.ApproverUserId == actor.Id).Select(task => task.Status).ToArrayAsync(ct);
                anyTask = tasks.Length > 0;
                activeTask = tasks.Contains(FlowTaskStatuses.Pending) && permissions.Contains(OemPermissions.FlowApprove);
            }
        }
        var facts = new TransferFacts(transfer.Direction, transfer.OemCompanyId, transfer.LifecycleStatus,
            transfer.InternalSenderUserId, transfer.OemSenderAccountId, approvalBlocked);
        return TransferAccess.Evaluate(facts, ActorFacts.For(actor, permissions, activeTask, anyTask));
    }

    private static string NormalizeApprovalStatus(string value) => value.Trim().ToUpperInvariant() switch
    {
        "PENDING" => FlowInstanceStatuses.InProgress,
        "APPROVED" => FlowInstanceStatuses.Completed,
        FlowInstanceStatuses.WaitingFiles => FlowInstanceStatuses.WaitingFiles,
        FlowInstanceStatuses.InProgress => FlowInstanceStatuses.InProgress,
        FlowInstanceStatuses.ApprovalBlocked => FlowInstanceStatuses.ApprovalBlocked,
        FlowInstanceStatuses.Completed => FlowInstanceStatuses.Completed,
        FlowInstanceStatuses.Skipped => FlowInstanceStatuses.Skipped,
        FlowInstanceStatuses.Rejected => FlowInstanceStatuses.Rejected,
        FlowInstanceStatuses.Cancelled => FlowInstanceStatuses.Cancelled,
        _ => throw ApiException.BadRequest("审批状态筛选值无效"),
    };

    private static string? ValidationMessage(string validationStatus, string? lastError) => validationStatus switch
    {
        ValidationStatuses.Pending => "等待文件校验",
        ValidationStatuses.Validating => "正在校验文件",
        ValidationStatuses.Valid => "文件校验通过",
        ValidationStatuses.Invalid => lastError ?? "文件未通过校验",
        ValidationStatuses.Error => lastError ?? "文件校验多次失败",
        _ => null,
    };

    private static async Task<Dictionary<ulong, object>> SummariesAsync(OemUnitOfWork uow, IReadOnlyCollection<OemTransfer> transfers, CancellationToken ct)
    {
        var db = uow.Db;
        var ids = transfers.Select(transfer => transfer.Id).ToArray();
        var companyIds = transfers.Select(transfer => transfer.OemCompanyId).Distinct().ToArray();
        var companies = await db.OemCompanies.AsNoTracking().Where(company => Enumerable.Contains(companyIds, company.Id))
            .ToDictionaryAsync(company => company.Id, company => company.Name, ct);
        var userIds = transfers.Where(transfer => transfer.InternalSenderUserId.HasValue).Select(transfer => transfer.InternalSenderUserId!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(user => Enumerable.Contains(userIds, user.Id))
            .ToDictionaryAsync(user => user.Id, user => new { user.EmployeeNo, user.RealName }, ct);
        var accountIds = transfers.Where(transfer => transfer.OemSenderAccountId.HasValue).Select(transfer => transfer.OemSenderAccountId!.Value).Distinct().ToArray();
        var accounts = await db.OemAccounts.AsNoTracking().Where(account => Enumerable.Contains(accountIds, account.Id))
            .ToDictionaryAsync(account => account.Id, account => new { account.EmployeeNo, account.RealName }, ct);
        var files = await db.OemTransferFiles.AsNoTracking()
            .Where(file => Enumerable.Contains(ids, file.TransferId) && (file.PurgeReason == null || file.PurgeReason != PurgeReasons.FileRemoved))
            .Select(file => new { file.TransferId, file.SizeBytes, file.ScanStatus, file.PayloadStatus }).ToArrayAsync(ct);
        var instances = await db.OemFlowInstances.AsNoTracking().Where(instance => Enumerable.Contains(ids, instance.TransferId))
            .ToDictionaryAsync(instance => instance.TransferId, instance => new { instance.Status, instance.BlockedReason }, ct);
        var result = new Dictionary<ulong, object>();
        foreach (var transfer in transfers)
        {
            var own = files.Where(file => file.TransferId == transfer.Id).ToArray();
            instances.TryGetValue(transfer.Id, out var instance);
            var approvalStatus = transfer.Direction == TransferDirections.OemToInternal ? "NOT_REQUIRED" : instance?.Status switch
            {
                null => null,
                FlowInstanceStatuses.InProgress => "PENDING",
                FlowInstanceStatuses.Completed => "APPROVED",
                var value => value,
            };
            object sender = transfer.InternalSenderUserId is ulong userId && users.TryGetValue(userId, out var user)
                ? new { realm = OemRealms.Internal, id = userId, user.EmployeeNo, user.RealName }
                : transfer.OemSenderAccountId is ulong accountId && accounts.TryGetValue(accountId, out var account)
                    ? new { realm = OemRealms.Oem, id = accountId, account.EmployeeNo, account.RealName }
                    : new { realm = "unknown", id = 0UL, EmployeeNo = "", RealName = "" };
            result[transfer.Id] = new
            {
                transfer.Id, transfer.Direction, companyId = transfer.OemCompanyId, companyName = companies.GetValueOrDefault(transfer.OemCompanyId),
                transfer.Title, sender, lifecycleStatus = transfer.LifecycleStatus, approvalStatus,
                approvalBlockedReason = instance?.Status == FlowInstanceStatuses.ApprovalBlocked ? instance.BlockedReason : null,
                validationSummary = ValidationSummary(own.Select(file => file.ScanStatus).ToArray()),
                fileCount = own.Length, totalBytes = own.Aggregate(0UL, (sum, file) => sum + file.SizeBytes),
                availableCount = own.Count(file => file.PayloadStatus == PayloadStatuses.Available),
                purgePendingCount = own.Count(file => file.PayloadStatus == PayloadStatuses.PurgePending),
                purgedCount = own.Count(file => file.PayloadStatus == PayloadStatuses.Purged),
                missingCount = own.Count(file => file.PayloadStatus is PayloadStatuses.StorageLost or PayloadStatuses.MissingUnverified),
                transfer.CreatedAt, transfer.SentAt, transfer.ReleasedAt, version = transfer.ConcurrencyVersion,
            };
        }
        return result;
    }

    /// <summary>Read-only aggregation: failures dominate, then in-progress states; VALID only when every file is valid.</summary>
    internal static string? ValidationSummary(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Count == 0) return null;
        if (statuses.Contains(ValidationStatuses.Invalid)) return ValidationStatuses.Invalid;
        if (statuses.Contains(ValidationStatuses.Error)) return ValidationStatuses.Error;
        if (statuses.Contains(ValidationStatuses.Validating)) return ValidationStatuses.Validating;
        if (statuses.Contains(ValidationStatuses.Pending)) return ValidationStatuses.Pending;
        return statuses.All(status => status == ValidationStatuses.Valid)
            ? ValidationStatuses.Valid
            : ValidationStatuses.Error;
    }

    private static async Task<object?> ApprovalAsync(OemUnitOfWork uow, ulong transferId, CancellationToken ct)
    {
        var db = uow.Db;
        var instance = await db.OemFlowInstances.AsNoTracking().SingleOrDefaultAsync(item => item.TransferId == transferId, ct);
        if (instance is null) return null;
        var nodes = await db.OemFlowInstanceNodes.AsNoTracking().Where(node => node.InstanceId == instance.Id).OrderBy(node => node.SortNo).ToArrayAsync(ct);
        var tasks = await db.OemFlowTasks.AsNoTracking().Where(task => task.InstanceId == instance.Id).OrderBy(task => task.Id).ToArrayAsync(ct);
        var people = tasks.Select(task => task.ApproverUserId).Append(instance.InitiatorUserId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(user => Enumerable.Contains(people, user.Id))
            .ToDictionaryAsync(user => user.Id, user => new { user.EmployeeNo, user.RealName }, ct);
        var template = await db.OemFlowTemplates.AsNoTracking().Where(item => item.Id == instance.TemplateId).Select(item => item.Name).SingleOrDefaultAsync(ct);
        return new
        {
            instanceId = instance.Id, instance.Status, instance.BlockedReason, instance.CurrentSortNo, version = instance.ConcurrencyVersion,
            templateName = template,
            nodes = nodes.Select(node => new
            {
                node.SortNo, node.Name, node.ApproverSource, node.ApprovalMode, node.Status, node.SkipReason, node.UsedFallback, node.CompletedAt,
                tasks = tasks.Where(task => task.InstanceNodeId == node.Id).Select(task => new
                {
                    task.Id, task.Status, approverUserId = task.ApproverUserId,
                    approverName = names.GetValueOrDefault(task.ApproverUserId)?.RealName,
                    approverEmployeeNo = names.GetValueOrDefault(task.ApproverUserId)?.EmployeeNo,
                    task.Reason, task.DecidedAt, task.ReplacesTaskId, task.ReassignReason, version = task.ConcurrencyVersion,
                }),
            }),
        };
    }
}
