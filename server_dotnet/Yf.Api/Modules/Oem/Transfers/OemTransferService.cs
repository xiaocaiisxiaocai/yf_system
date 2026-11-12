using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Modules.Oem.Transfers;

/// <remarks>
/// Retention is always the unified active policy; senders cannot choose it. A legacy
/// <c>retentionTemplateId</c> member in the body is ignored by System.Text.Json.
/// </remarks>
public sealed record TransferCreate(
    string? Title = null,
    string? Description = null,
    ulong? OemCompanyId = null);
public sealed record TransferUpdate(
    string? Title = null,
    string? Description = null,
    ulong? Version = null);
public sealed record TransferVersionRequest(ulong? Version);
/// <summary>
/// Send a draft. A vendor sending files also names the company staff to notify (from its notification list) and signs with
/// the name of one of its registered contacts.
/// </summary>
public sealed record TransferSendRequest(ulong? Version, ulong[]? RecipientUserIds, string? SignerName = null);

/// <summary>
/// Transfer drafts, sending, listing and detail. The direction is always derived from
/// the caller's realm. Visibility and content rights come from <see cref="TransferAccess"/>;
/// what happens after sending is decided by <see cref="OemTransferProgression"/>.
/// </summary>
public sealed class OemTransferService(
    IDbContextFactory<YfDbContext> dbFactory,
    ApprovalPlanningService planning,
    OemApprovalEngine engine,
    OemTransferProgression progression,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    OemTransferReader reader)
{
    internal const string DraftTitlePlaceholder = "待上传文件";

    public async Task<OemTransferDetailResponse> CreateAsync(OemActor actor, TransferCreate request, CancellationToken ct)
    {
        var title = DraftTitle(request.Title);
        var description = OemValidation.OptionalText(request.Description, "说明", 1024);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        string direction;
        ulong companyId;
        if (current is OemAccountActor account)
        {
            if (request.OemCompanyId is ulong requested && requested != account.CompanyId) throw ApiException.BadRequest("只能向本厂商创建传递单");
            direction = TransferDirections.OemToInternal;
            companyId = account.CompanyId;
            // The draft references its vendor sender (see OemAccountLock).
            await OemAccountLock.ShareAsync(uow, current, ct);
        }
        else
        {
            await OemAuthorizer.RequireAsync(uow, current, OemPermissions.TransferCreate, ct);
            try { await planning.LoadOrgPathAsync(uow, current.Id, ct); }
            catch (ApprovalPlanningException error) { throw ApiException.BadRequest(error.Message); }
            companyId = request.OemCompanyId ?? throw ApiException.BadRequest("请选择目标 ODM/OEMA 厂商");
            if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == companyId && company.Status == OemStatus.Active, ct))
                throw ApiException.BadRequest("目标厂商不存在或已停用");
            await OemRecipientPolicy.RequireEnabledAccountAsync(uow.Db, companyId, ct);
            direction = TransferDirections.InternalToOem;
        }
        var retention = await OemRetentionTemplateService.EffectiveActiveAsync(uow.Db, ct);
        var transfer = new OemTransfer
        {
            Direction = direction, OemCompanyId = companyId, Title = title, Description = description,
            InternalSenderUserId = current is InternalOemActor ? current.Id : null,
            OemSenderAccountId = current is OemAccountActor ? current.Id : null,
            LifecycleStatus = TransferLifecycle.Draft, RetentionTemplateId = retention.Id,
            CreatedAt = uow.Now, UpdatedAt = uow.Now, ConcurrencyVersion = 0,
        };
        uow.Db.OemTransfers.Add(transfer);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_TRANSFER_CREATE", "oem_transfer", transfer.Id,
            new { targetName = title, direction, companyId }, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.DetailAsync(current, transfer.Id, ct);
    }

    public async Task<OemTransferDetailResponse> UpdateAsync(OemActor actor, ulong id, TransferUpdate request, CancellationToken ct)
    {
        var description = OemValidation.OptionalText(request.Description, "说明", 1024);
        var version = OemValidation.ExpectedVersion(request.Version);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transfer = await LockOwnDraftAsync(uow, current, id, version, ct);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? transfer.Title
            : OemValidation.RequiredText(request.Title, "标题", 128);
        var retention = await OemRetentionTemplateService.EffectiveActiveAsync(uow.Db, ct);
        var changes = AuditChange.OnlyChanged(
            new AuditChange("title", "标题", transfer.Title, title),
            new AuditChange("description", "说明", transfer.Description, description),
            new AuditChange("retentionTemplateId", "删除策略", transfer.RetentionTemplateId, retention.Id));
        transfer.Title = title;
        transfer.Description = description;
        transfer.RetentionTemplateId = retention.Id;
        OemTransferProgression.Touch(uow, transfer);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_TRANSFER_UPDATE", "oem_transfer", id, new { targetName = title, changes }, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.DetailAsync(current, id, ct);
    }

    /// <summary>Deletes an unsent draft: the record is kept as ABANDONED for audit, its files are purged.</summary>
    public async Task DeleteAsync(OemActor actor, ulong id, ulong? expectedVersion, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(expectedVersion);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transfer = await LockOwnDraftAsync(uow, current, id, version, ct);
        await AbandonAsync(uow, transfer, current, "发送人删除草稿", PurgeReasons.DraftDeleted, ct);
        await dispatcher.CommitAsync(uow, ct);
    }

    internal async Task AbandonAsync(OemUnitOfWork uow, OemTransfer transfer, OemActor? actor, string reason, string purgeReason, CancellationToken ct)
    {
        TransferStateMachine.Ensure(transfer.LifecycleStatus, TransferLifecycle.Abandoned);
        transfer.LifecycleStatus = TransferLifecycle.Abandoned;
        transfer.ClosedReason = reason;
        transfer.ClosedByRealm = actor?.Realm ?? OemRealms.System;
        transfer.ClosedById = actor?.Id;
        transfer.ClosedAt = uow.Now;
        OemTransferProgression.Touch(uow, transfer);
        await uow.Db.SaveChangesAsync(ct);
        await uow.Db.OemUploadSessions
            .Where(session => session.TransferId == transfer.Id && (session.Status == UploadStatuses.Uploading || session.Status == UploadStatuses.Merging))
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.Status, UploadStatuses.Aborted)
                .SetProperty(session => session.ReservedBytes, 0UL).SetProperty(session => session.UpdatedAt, uow.Now), ct);
        await OemTransferProgression.SchedulePurgeAsync(uow, transfer.Id, uow.Now, purgeReason, ct);
        await audit.WriteAsync(uow, actor, "OEM_TRANSFER_ABANDON", "oem_transfer", transfer.Id, new { targetName = transfer.Title, reason }, ct);
    }

    /// <summary>
    /// Freezes the manifest and starts validation/approval/release. Outbound transfers are
    /// routed now: any routing problem fails the send and the transfer stays a draft.
    /// </summary>
    public async Task<OemTransferDetailResponse> SendAsync(OemActor actor, ulong id, ulong? expectedVersion, CancellationToken ct,
        IReadOnlyCollection<ulong>? recipientUserIds = null, string? signerName = null)
    {
        var version = OemValidation.ExpectedVersion(expectedVersion);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transfer = await LockOwnDraftAsync(uow, current, id, version, ct);
        if (current is InternalOemActor) await OemAuthorizer.RequireAsync(uow, current, OemPermissions.TransferCreate, ct);
        await OemSettings.EnsureStorageSettledAsync(uow.Db, "发送", ct);
        if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == transfer.OemCompanyId && company.Status == OemStatus.Active, ct))
            throw current is OemAccountActor ? ApiException.Forbidden() : ApiException.BadRequest("目标厂商已停用");
        if (current is InternalOemActor)
            await OemRecipientPolicy.RequireEnabledAccountAsync(uow.Db, transfer.OemCompanyId, ct);
        var activeUploads = await uow.Db.OemUploadSessions.AnyAsync(session => session.TransferId == id
            && ((session.Status == UploadStatuses.Uploading && session.ExpiresAt > uow.Now) || session.Status == UploadStatuses.Merging), ct);
        if (activeUploads) throw ApiException.Conflict("仍有附件正在上传，请等待上传完成或取消后再发送");
        var files = await OemTransferFiles.Members(uow.Db, id).OrderBy(file => file.Id).ToArrayAsync(ct);
        if (files.Length == 0) throw ApiException.BadRequest("请至少上传一个附件");
        if (files.Any(file => !ValidationStatuses.IsKnown(file.ScanStatus)
                || file.ScanStatus is ValidationStatuses.Invalid or ValidationStatuses.Error
                || file.PayloadStatus is not (PayloadStatuses.Quarantined or PayloadStatuses.Promoting or PayloadStatuses.Available)))
            throw ApiException.BadRequest("存在未通过文件校验或不可用的附件，请移除后再发送");
        var retention = await OemRetentionTemplateService.EffectiveActiveAsync(uow.Db, ct);

        PlanningResult? plan = null;
        if (transfer.Direction == TransferDirections.InternalToOem)
        {
            try { plan = await planning.PlanAsync(uow, current.Id, ct); }
            catch (ApprovalPlanningException error) { throw error.ToApi(); }
        }
        TransferStateMachine.Ensure(transfer.LifecycleStatus, TransferLifecycle.Sealed);
        transfer.LifecycleStatus = TransferLifecycle.Sealed;
        transfer.SentAt = uow.Now;
        transfer.RetentionTemplateId = retention.Id;
        transfer.RetentionMode = retention.Mode;
        transfer.ReleaseTtlMinutes = retention.ReleaseTtlMinutes;
        transfer.ReceiptGraceMinutes = retention.ReceiptGraceMinutes;
        transfer.ManifestSha256 = Manifest(files);
        if (transfer.Title == DraftTitlePlaceholder) transfer.Title = GeneratedTitle(files);
        var notified = await ChooseInboundRecipientsAsync(uow, current, transfer, recipientUserIds, ct);
        await SignAsync(uow, current, transfer, signerName, ct);
        OemTransferProgression.Touch(uow, transfer);
        await uow.Db.SaveChangesAsync(ct);
        if (plan is not null) await engine.CreateAsync(uow, transfer, current.Id, plan, ct);
        await audit.WriteAsync(uow, current, "OEM_TRANSFER_SEND", "oem_transfer", id, new
        {
            targetName = transfer.Title, files = files.Length, manifest = transfer.ManifestSha256, retentionMode = retention.Mode,
            approval = plan is null ? "NOT_REQUIRED" : plan.Plan.RequiresNoApproval ? "SKIPPED" : "REQUIRED",
            notifyRecipients = notified,
            signerName = transfer.SignerName,
            skippedNodes = plan?.Plan.Nodes.Where(node => node.Skipped).Select(node => new { node.Name, node.SkipReason }),
        }, ct);
        await progression.AdvanceAsync(uow, id, current, ct);
        await dispatcher.CommitAsync(uow, ct);
        return await reader.DetailAsync(current, id, ct);
    }

    /// <summary>
    /// A vendor sending files chooses whom to notify from its notification list: at least one when anyone on the list can receive,
    /// and only people on that list. Stored before release so the release mail goes to exactly these people.
    /// </summary>
    private static async Task<int> ChooseInboundRecipientsAsync(
        OemUnitOfWork uow, OemActor current, OemTransfer transfer, IReadOnlyCollection<ulong>? recipientUserIds, CancellationToken ct)
    {
        var chosen = (recipientUserIds ?? []).Distinct().ToArray();
        if (current is not OemAccountActor || transfer.Direction != TransferDirections.OemToInternal)
        {
            if (chosen.Length > 0) throw ApiException.BadRequest("公司发出的传递单不需要选择通知人员");
            return 0;
        }
        var candidates = (await OemRecipients.InboundRecipientsAsync(uow.Db, ct))
            .Select(candidate => candidate.Id).ToHashSet();
        if (candidates.Count > 0 && chosen.Length == 0) throw ApiException.BadRequest("请选择要通知的公司人员");
        if (chosen.Any(userId => !candidates.Contains(userId))) throw ApiException.BadRequest("所选通知人员已不可选，请刷新后重新选择");
        await uow.Db.OemTransferInternalRecipients.Where(item => item.TransferId == transfer.Id).ExecuteDeleteAsync(ct);
        uow.Db.OemTransferInternalRecipients.AddRange(chosen.Select(userId => new OemTransferInternalRecipient
        {
            TransferId = transfer.Id, UserId = userId,
        }));
        return chosen.Length;
    }

    /// <summary>
    /// A vendor sending files must sign with the name of the person who provides them (the shared login is not a person):
    /// the signature is always required and is free text, not limited to registered contacts.
    /// </summary>
    private static async Task SignAsync(OemUnitOfWork uow, OemActor current, OemTransfer transfer, string? signerName, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(signerName) ? null : signerName.Trim();
        if (current is not OemAccountActor || transfer.Direction != TransferDirections.OemToInternal)
        {
            if (name is not null) throw ApiException.BadRequest("公司发出的传递单不需要签名");
            return;
        }
        var active = uow.Db.OemCompanyContacts.AsNoTracking()
            .Where(contact => contact.OemCompanyId == transfer.OemCompanyId && contact.Status == OemStatus.Active);
        if (name is null) throw ApiException.BadRequest("请填写签名（提供人姓名）");
        if (name.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("签名不能超过 32 个字符");
        // When the name happens to be a registered contact the link (and its spelling) is kept; otherwise only the name is stored.
        var contact = await active.Where(item => item.Name == name).OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Name }).FirstOrDefaultAsync(ct);
        transfer.SignerContactId = contact?.Id;
        transfer.SignerName = contact?.Name ?? name;
    }

    /// <summary>Removes an attachment from an unsent draft (content is purged; metadata kept for audit).</summary>
    public async Task RemoveFileAsync(OemActor actor, ulong fileId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transferId = await uow.Db.OemTransferFiles.Where(file => file.Id == fileId).Select(file => (ulong?)file.TransferId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
        await EnsureOwnDraftAsync(uow, current, transfer, ct);
        var file = await OemLocks.ForUpdate<OemTransferFile>(uow.Db, fileId).SingleAsync(ct);
        if (file.PurgeReason == PurgeReasons.FileRemoved) throw ApiException.NotFound();
        file.PurgeReason = PurgeReasons.FileRemoved;
        if (PayloadStatuses.OnDisk.Contains(file.PayloadStatus)) file.PurgeDueAt = uow.Now;
        file.ConcurrencyVersion++;
        file.UpdatedAt = uow.Now;
        OemTransferProgression.Touch(uow, transfer);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_FILE_REMOVE", "oem_file", fileId,
            new { targetName = file.OriginalName, transferId, file.Sha256, file.SizeBytes }, ct);
        await dispatcher.CommitAsync(uow, ct);
    }

    /// <summary>
    /// Only the actual sender may change a draft. A caller who cannot see the transfer at all
    /// gets 404 (never 403), so draft state and existence do not leak across senders or vendors.
    /// </summary>
    internal static async Task EnsureOwnDraftAsync(OemUnitOfWork uow, OemActor actor, OemTransfer transfer, CancellationToken ct)
    {
        var isSender = actor is OemAccountActor ? transfer.OemSenderAccountId == actor.Id : transfer.InternalSenderUserId == actor.Id;
        if (!isSender)
        {
            if (transfer.LifecycleStatus == TransferLifecycle.Draft
                || !(await OemTransferReader.CapabilitiesAsync(uow, actor, transfer, ct)).View)
                throw ApiException.NotFound();
            throw ApiException.Forbidden();
        }
        if (transfer.LifecycleStatus != TransferLifecycle.Draft) throw ApiException.Conflict("传递单已发送，附件和内容不能再修改");
    }

    private static async Task<OemTransfer> LockOwnDraftAsync(OemUnitOfWork uow, OemActor actor, ulong id, ulong expectedVersion, CancellationToken ct)
    {
        var transfer = await OemTransferProgression.LockTransferAsync(uow, id, ct);
        await EnsureOwnDraftAsync(uow, actor, transfer, ct);
        OemValidation.MatchVersion(transfer.ConcurrencyVersion, expectedVersion);
        return transfer;
    }

    private static string DraftTitle(string? value) => string.IsNullOrWhiteSpace(value)
        ? DraftTitlePlaceholder
        : OemValidation.RequiredText(value, "标题", 128);

    internal static string GeneratedTitle(IReadOnlyCollection<OemTransferFile> files)
    {
        if (files.Count == 0) throw new ArgumentException("At least one file is required.", nameof(files));
        var first = files.OrderBy(file => file.Id).First().OriginalName;
        var suffix = files.Count == 1 ? string.Empty : $" 等{files.Count}个文件";
        var available = 128 - suffix.EnumerateRunes().Count();
        return TruncateRunes(first, available) + suffix;
    }

    private static string TruncateRunes(string value, int maxRunes)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Take(maxRunes)) builder.Append(rune);
        return builder.ToString();
    }

    /// <summary>Digest over the ordered attachment list; any change to the frozen files changes it.</summary>
    internal static string Manifest(IEnumerable<OemTransferFile> files)
    {
        var text = string.Join('\n', files.OrderBy(file => file.Id).Select(file => $"{file.Id}:{file.Sha256}:{file.SizeBytes}:{file.OriginalName}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

public static class UploadStatuses
{
    public const string Uploading = "UPLOADING";
    public const string Merging = "MERGING";
    public const string Completed = "COMPLETED";
    public const string Expired = "EXPIRED";
    public const string Aborted = "ABORTED";
}
