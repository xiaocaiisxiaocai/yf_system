using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Oem.Notifications;

/// <summary>OEM mail event types (all share the <c>OEM_</c> prefix that routes them to <see cref="OemOutboxPolicy"/>).</summary>
public static class OemMailEvents
{
    public const string Prefix = "OEM_";
    public const string ApprovalPending = "OEM_APPROVAL_PENDING";
    public const string TransferReleased = "OEM_TRANSFER_RELEASED";
    public const string SenderReleased = "OEM_SENDER_RELEASED";
    public const string TransferClosed = "OEM_TRANSFER_CLOSED";
    public const string ApprovalBlocked = "OEM_APPROVAL_BLOCKED";
    public const string ValidationFailed = "OEM_VALIDATION_FAILED";
    public const string FileMissing = "OEM_FILE_MISSING";
    public const string FirstReceipt = "OEM_FIRST_RECEIPT";
    public const string FilePurged = "OEM_FILE_PURGED";
    public const string PurgeFailing = "OEM_PURGE_FAILING";

    /// <summary>Per-event switch key; null means only the OEM master switch applies (operational alerts).</summary>
    public static string? SwitchKey(string eventType) => eventType switch
    {
        ApprovalPending => OemSettingCatalog.NotifyApprovalPending,
        TransferReleased => OemSettingCatalog.NotifyTransferReleased,
        SenderReleased or TransferClosed or ApprovalBlocked or ValidationFailed or FileMissing => OemSettingCatalog.NotifySenderResult,
        FirstReceipt or FilePurged => OemSettingCatalog.NotifyReceipt,
        _ => null,
    };

    public static bool Allowed(OemSettings settings, string eventType) =>
        settings.Bool(OemSettingCatalog.NotifyEnabled) && (SwitchKey(eventType) is not { } key || settings.Bool(key));
}

public sealed record MailRecipient(string Realm, ulong Id, string Email, string Name, ulong? ContactId = null);

/// <summary>Queries for people addressed by OEM notifications.</summary>
internal static class OemRecipients
{
    /// <summary>Active internal users holding every one of <paramref name="codes"/> through an active role.</summary>
    public static async Task<MailRecipient[]> InternalWithAllAsync(YfDbContext db, CancellationToken ct, params string[] codes)
    {
        var query = db.Users.AsNoTracking().Where(user => user.Status == OemStatus.Active && user.UserType == "INTERNAL");
        foreach (var code in codes)
        {
            var required = code;
            query = query.Where(user => db.UserRoles.Any(userRole => userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Status == OemStatus.Active)
                && db.RolePermissions.Any(rolePermission => rolePermission.RoleId == userRole.RoleId
                    && db.Permissions.Any(permission => permission.Id == rolePermission.PermissionId && permission.Code == required))));
        }
        return await query.OrderBy(user => user.Id)
            .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName, null)).ToArrayAsync(ct);
    }

    public static async Task<MailRecipient[]> VendorContactsAsync(
        YfDbContext db, ulong companyId, CancellationToken ct, ulong? preferredAnchorAccountId = null)
    {
        var company = await db.OemCompanies.AsNoTracking()
            .Where(item => item.Id == companyId && item.Status == OemStatus.Active)
            .Select(item => new { item.Name })
            .SingleOrDefaultAsync(ct);
        if (company is null) return [];

        var anchorAccountId = preferredAnchorAccountId is ulong preferred
            ? await db.OemAccounts.AsNoTracking().Where(account => account.Id == preferred
                && account.OemCompanyId == companyId && account.Status == OemStatus.Active)
                .Select(account => (ulong?)account.Id).SingleOrDefaultAsync(ct)
            : null;
        anchorAccountId ??= await db.OemAccounts.AsNoTracking().Where(account => account.OemCompanyId == companyId
                && account.Status == OemStatus.Active).OrderBy(account => account.Id)
            .Select(account => (ulong?)account.Id).FirstOrDefaultAsync(ct);
        if (anchorAccountId is not ulong anchor) return [];

        var result = new List<MailRecipient>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contacts = await db.OemCompanyContacts.AsNoTracking()
            .Where(contact => contact.OemCompanyId == companyId && contact.Status == OemStatus.Active && contact.Email != null)
            .OrderBy(contact => contact.SortNo).ThenBy(contact => contact.Id)
            .Select(contact => new { contact.Id, contact.Name, contact.Email }).ToArrayAsync(ct);
        foreach (var contact in contacts)
        {
            var email = contact.Email?.Trim();
            if (!string.IsNullOrWhiteSpace(email) && seen.Add(email))
                result.Add(new(OemRealms.Oem, anchor, email, contact.Name, contact.Id));
        }
        return result.ToArray();
    }

    public static async Task<MailRecipient[]> SenderRecipientsAsync(YfDbContext db, OemTransfer transfer, CancellationToken ct)
    {
        if (transfer.InternalSenderUserId is ulong userId)
        {
            var recipient = await db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == OemStatus.Active)
                .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName, null)).SingleOrDefaultAsync(ct);
            return recipient is null ? [] : [recipient];
        }
        if (transfer.OemSenderAccountId is ulong accountId)
            return await VendorContactsAsync(db, transfer.OemCompanyId, ct, accountId);
        return [];
    }

    public static string DedupeRecipient(MailRecipient recipient) => recipient.ContactId is ulong contactId
        ? $"{recipient.Realm}:{recipient.Id}:contact:{contactId}"
        : $"{recipient.Realm}:{recipient.Id}";

    public static async Task<MailRecipient?> InternalUserAsync(YfDbContext db, ulong userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == OemStatus.Active && user.UserType == "INTERNAL")
            .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName, null)).SingleOrDefaultAsync(ct);
}

internal sealed record OemMailContent(string Subject, string Body);

/// <summary>
/// Rebuilds every OEM message from fixed event copy and stable database identifiers.
/// It deliberately accepts no transfer title, file name, person name, company name or
/// free-text reason, so both new and pre-upgrade queued mail share the same privacy boundary.
/// </summary>
internal static class OemMailContentBuilder
{
    private static readonly Regex SafeAttachmentLine = new(
        @"\A附件：[0-9]+ 个，共 [0-9]+(?:\.[0-9]{1,2})? (?:B|KB|MB|GB)\z",
        RegexOptions.CultureInvariant);

    public static OemMailContent Build(
        AppOptions options,
        string eventType,
        OemTransfer transfer,
        string? recipientRealm,
        int fileCount,
        ulong totalBytes)
    {
        var (action, lead) = Copy(eventType, transfer.LifecycleStatus);
        var direction = transfer.Direction == TransferDirections.InternalToOem
            ? "公司 → ODM/OEMA"
            : "ODM/OEMA → 公司";
        var sender = transfer.InternalSenderUserId is ulong internalUserId
            ? $"内部账号 #{internalUserId}"
            : $"ODM/OEMA 账号 #{transfer.OemSenderAccountId}";
        var area = recipientRealm == OemRealms.Oem ? "oem-portal" : "oem";
        var lines = new List<string>
        {
            "您好：", string.Empty, lead, string.Empty,
            $"传递单编号：#{transfer.Id}",
            $"方向：{direction}",
            $"发送方：{sender}",
            $"附件：{fileCount} 个，共 {HumanSize(totalBytes)}",
        };
        if (SafeFixedReason(transfer) is { } fixedReason)
            lines.Add($"状态说明：{fixedReason}");
        lines.AddRange([
            string.Empty,
            $"请登录 ODM/OEMA 文件传递平台查看：{options.WebBaseUrl.TrimEnd('/')}/{area}/transfers/{transfer.Id}",
            string.Empty,
            "此邮件由系统自动发送，请勿直接回复。",
        ]);
        return new(
            Truncate($"[ODM/OEMA 文件传递] {action} · 传递单 #{transfer.Id}", 255),
            string.Join("\n", lines));
    }

    public static bool MatchesCurrentTemplate(
        AppOptions options,
        string eventType,
        OemTransfer transfer,
        string? recipientRealm,
        string? subject,
        string? body)
    {
        if (subject is null || body is null) return false;
        var expected = Build(options, eventType, transfer, recipientRealm, 0, 0);
        if (subject != expected.Subject) return false;
        var actualLines = body.Split('\n');
        var expectedLines = expected.Body.Split('\n');
        if (actualLines.Length != expectedLines.Length) return false;
        for (var index = 0; index < expectedLines.Length; index++)
        {
            if (expectedLines[index].StartsWith("附件：", StringComparison.Ordinal))
            {
                if (!SafeAttachmentLine.IsMatch(actualLines[index])) return false;
            }
            else if (actualLines[index] != expectedLines[index])
            {
                return false;
            }
        }
        return true;
    }

    private static (string Action, string Lead) Copy(string eventType, string lifecycle) => eventType switch
    {
        OemMailEvents.ApprovalPending => ("待审批", "有一张公司发往 ODM/OEMA 厂商的文件传递单等待您审批。"),
        OemMailEvents.TransferReleased => ("新文件已送达", "有新的文件传递给您，请登录平台查看和下载。"),
        OemMailEvents.SenderReleased => ("已发布", "您发送的文件传递单已通过检查并发布给接收方。"),
        OemMailEvents.TransferClosed => lifecycle switch
        {
            TransferLifecycle.Rejected => ("已驳回", "您发送的文件传递单已被审批驳回，如需重新发送请创建新的传递单。"),
            TransferLifecycle.Blocked => ("已阻断", "您发送的文件传递单未能发布，已被阻断，附件将被自动清理。"),
            _ => ("已终止", "您发送的文件传递单已被管理员终止。"),
        },
        OemMailEvents.ApprovalBlocked => ("审批异常", "文件传递单的审批因审批人不可用而暂停，请登录平台处置或查看进度。"),
        OemMailEvents.ValidationFailed => ("附件未通过文件校验", "草稿中有附件未通过文件校验，请登录平台移除后再发送。"),
        OemMailEvents.FileMissing => ("附件不可用", "传递单中有附件不可用，请登录平台查看并按需重新创建传递单。"),
        OemMailEvents.FirstReceipt => ("文件已被接收", "接收方已完整下载传递单中的附件。"),
        OemMailEvents.FilePurged => ("附件已按策略删除", "传递单中的附件已按删除策略从服务器删除，相关记录仍可在平台查看。"),
        OemMailEvents.PurgeFailing => ("附件清理持续失败", "有到期附件多次清理失败，请检查 ODM/OEMA 存储目录权限和占用情况。"),
        _ => ("状态更新", "文件传递单状态已更新，请登录平台查看。"),
    };

    private static string? SafeFixedReason(OemTransfer transfer) =>
        transfer.LifecycleStatus == TransferLifecycle.Blocked ? transfer.ClosedReason switch
        {
            OemTransferProgression.RecipientUnavailableReason => OemTransferProgression.RecipientUnavailableReason,
            OemTransferProgression.MissingFilesReason => OemTransferProgression.MissingFilesReason,
            "附件未通过文件校验" => "附件未通过文件校验",
            _ => null,
        } : null;

    private static string HumanSize(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024 * 1024 => $"{(bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture)} GB",
        >= 1024UL * 1024 => $"{(bytes / 1024d / 1024).ToString("0.##", CultureInfo.InvariantCulture)} MB",
        >= 1024UL => $"{(bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture)} KB",
        _ => $"{bytes} B",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Turns OEM domain events into queued mail inside the business transaction (outbox
/// pattern): one message per recipient, deduplicated by event + recipient, never
/// carrying attachments, file names, sender-entered transfer titles or download
/// credentials — only a stable transfer number and a pointer to the page.
/// </summary>
public sealed class OemNotificationHandler(AppOptions options, OemAuditWriter audit) : IOemEventHandler
{
    public async Task HandleAsync(OemUnitOfWork uow, IOemEvent domainEvent, CancellationToken ct)
    {
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        if (!settings.Bool(OemSettingCatalog.NotifyEnabled)) return;
        var transfer = await uow.Db.OemTransfers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == domainEvent.TransferId, ct);
        if (transfer is null) return;
        var facts = await FactsAsync(uow.Db, transfer, ct);
        switch (domainEvent)
        {
            case ApprovalTasksActivatedEvent activated:
                foreach (var taskId in activated.TaskIds)
                {
                    var task = await uow.Db.OemFlowTasks.Where(item => item.Id == taskId)
                        .Select(item => new { item.ApproverUserId, item.ActivatedAt }).SingleAsync(ct);
                    // A task blocked and later re-activated keeps its id; the activation time keeps its new
                    // notice from being swallowed by the earlier one's dedupe key.
                    var activation = task.ActivatedAt?.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) ?? "0";
                    if (await OemRecipients.InternalUserAsync(uow.Db, task.ApproverUserId, ct) is { } approver)
                        await EnqueueAsync(uow, settings, OemMailEvents.ApprovalPending, $"oem:approval:{taskId}:{activation}", approver, facts,
                            ct);
                }
                break;
            case TransferReleasedEvent:
                var recipients = transfer.Direction == TransferDirections.InternalToOem
                    ? await OemRecipients.VendorContactsAsync(uow.Db, transfer.OemCompanyId, ct)
                    : await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.TransferView, OemPermissions.FileDownload);
                if (transfer.Direction == TransferDirections.InternalToOem && recipients.Length == 0 && OemMailEvents.Allowed(settings, OemMailEvents.TransferReleased))
                    await audit.WriteAsync(uow, null, "OEM_EMAIL_SKIPPED_MISSING_EMAIL", "oem_transfer", transfer.Id,
                        new { eventType = OemMailEvents.TransferReleased, reason = "厂商没有启用的联络人邮箱，通知未入队" }, ct);
                foreach (var recipient in recipients)
                    await EnqueueAsync(uow, settings, OemMailEvents.TransferReleased, $"oem:released:{transfer.Id}:{OemRecipients.DedupeRecipient(recipient)}",
                        recipient, facts, ct);
                foreach (var releasedSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.SenderReleased, $"oem:sender-released:{transfer.Id}:{OemRecipients.DedupeRecipient(releasedSender)}", releasedSender, facts,
                        ct);
                break;
            case TransferClosedEvent closed:
                foreach (var closedSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.TransferClosed, $"oem:closed:{transfer.Id}:{OemRecipients.DedupeRecipient(closedSender)}", closedSender, facts,
                        ct);
                break;
            case ApprovalBlockedEvent blocked:
                var stamp = uow.Now.Ticks.ToString(CultureInfo.InvariantCulture);
                var audience = new List<MailRecipient>();
                audience.AddRange(await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct));
                audience.AddRange(await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.ApprovalRecover));
                foreach (var person in audience.DistinctBy(item => (item.Realm, item.Id, item.ContactId)))
                    await EnqueueAsync(uow, settings, OemMailEvents.ApprovalBlocked, $"oem:blocked:{transfer.Id}:{stamp}:{OemRecipients.DedupeRecipient(person)}", person, facts,
                        ct);
                break;
            case DraftFileValidationFailedEvent failed:
                foreach (var draftSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.ValidationFailed, $"oem:validationfail:{failed.FileId}:{OemRecipients.DedupeRecipient(draftSender)}", draftSender, facts,
                        ct);
                break;
            case FileMissingEvent missing:
                foreach (var missingSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.FileMissing, $"oem:missing:{missing.FileId}:{OemRecipients.DedupeRecipient(missingSender)}", missingSender, facts,
                        ct);
                break;
            case FirstReceiptEvent receipt:
                foreach (var receiptSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.FirstReceipt, $"oem:receipt:{receipt.FileId}:{OemRecipients.DedupeRecipient(receiptSender)}", receiptSender, facts,
                        ct);
                break;
            case FilePurgedEvent purged:
                foreach (var purgedSender in await OemRecipients.SenderRecipientsAsync(uow.Db, transfer, ct))
                    await EnqueueAsync(uow, settings, OemMailEvents.FilePurged, $"oem:purged:{purged.FileId}:{OemRecipients.DedupeRecipient(purgedSender)}", purgedSender, facts,
                        ct);
                break;
            case FilePurgeFailingEvent failing:
                foreach (var admin in await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.FilePolicyManage))
                    await EnqueueAsync(uow, settings, OemMailEvents.PurgeFailing, $"oem:purgefail:{failing.FileId}:{failing.Attempts}:{admin.Id}", admin, facts,
                        ct);
                break;
        }
    }

    private sealed record TransferFactsForMail(OemTransfer Transfer, int FileCount, ulong TotalBytes);

    private static async Task<TransferFactsForMail> FactsAsync(YfDbContext db, OemTransfer transfer, CancellationToken ct)
    {
        var sizes = await OemTransferFiles.Members(db, transfer.Id).Select(file => file.SizeBytes).ToArrayAsync(ct);
        return new TransferFactsForMail(transfer, sizes.Length, sizes.Aggregate(0UL, (sum, size) => sum + size));
    }

    private async Task EnqueueAsync(OemUnitOfWork uow, OemSettings settings, string eventType, string dedupeKey, MailRecipient recipient,
        TransferFactsForMail facts, CancellationToken ct)
    {
        if (!OemMailEvents.Allowed(settings, eventType)) return;
        if (string.IsNullOrWhiteSpace(recipient.Email))
        {
            await audit.WriteAsync(uow, null, "OEM_EMAIL_SKIPPED_MISSING_EMAIL", "oem_transfer", facts.Transfer.Id,
                new { eventType, reason = "收件人未填写邮箱，通知未入队" }, ct);
            return;
        }
        var transfer = facts.Transfer;
        var content = OemMailContentBuilder.Build(
            options, eventType, transfer, recipient.Realm, facts.FileCount, facts.TotalBytes);
        var key = Truncate(dedupeKey, 128);
        // Idempotent enqueue (a documented raw-SQL boundary): a repeated event never mails twice.
        // Only a dedupe-key conflict is ignored; unlike INSERT IGNORE, truncation or other data
        // errors still fail the business transaction.
        await uow.Db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO email_outbox
            (event_type, dedupe_key, project_id, recipient_user_id, recipient_email, subject, body, status, retry_count, next_attempt_at,
             created_at, recipient_realm, recipient_account_id, oem_transfer_id)
            VALUES ({eventType}, {key}, NULL, NULL, {recipient.Email.Trim()}, {content.Subject}, {content.Body}, 'PENDING', 0, NULL,
             UTC_TIMESTAMP(3), {recipient.Realm}, {recipient.Id}, {transfer.Id})
            ON DUPLICATE KEY UPDATE id = id", ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Send-time rules for OEM mail inside the shared mail worker: the OEM switches (not
/// the collaboration ones) apply, and each message is re-validated against the live
/// recipient, account/vendor status and the transfer or task it is about.
/// </summary>
public sealed class OemOutboxPolicy(AppOptions options) : IOutboxRecipientPolicy
{
    public string EventTypePrefix => OemMailEvents.Prefix;
    public string AuditActionPrefix => "OEM_";

    public async Task<OutboxDecision> EvaluateAsync(MySqlConnection conn, MySqlTransaction tx, OutboxMailInfo mail, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var settings = await OemSettings.LoadAsync(db, ct);
        if (!OemMailEvents.Allowed(settings, mail.EventType))
            return OutboxDecision.Cancel("ODM/OEMA 邮件提醒规则已关闭，通知已取消", "OEM_NOTIFICATION_POLICY_DISABLED");
        if (mail.RecipientAccountId is not ulong recipientId || mail.OemTransferId is not ulong transferId)
            return OutboxDecision.Cancel("ODM/OEMA 邮件缺少收件人或传递单信息", "OEM_MAIL_INVALID");
        var transfer = await db.OemTransfers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == transferId, ct);
        if (transfer is null) return OutboxDecision.Cancel("传递单不存在", "OEM_TRANSFER_MISSING");

        if (mail.RecipientRealm == OemRealms.Oem)
        {
            var companyId = await db.OemAccounts.AsNoTracking()
                .Where(account => account.Id == recipientId && account.Status == OemStatus.Active
                    && db.OemCompanies.Any(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active)
                    && account.OemCompanyId == transfer.OemCompanyId)
                .Select(account => (ulong?)account.OemCompanyId).SingleOrDefaultAsync(ct);
            if (companyId is null || !System.Net.Mail.MailAddress.TryCreate(mail.RecipientEmail.Trim(), out _)) return Stale();
            var emailIsCurrentContact = await db.OemCompanyContacts.AnyAsync(contact => contact.OemCompanyId == companyId
                    && contact.Status == OemStatus.Active && contact.Email == mail.RecipientEmail.Trim(), ct);
            if (!emailIsCurrentContact) return Stale();
            if (mail.EventType == OemMailEvents.TransferReleased
                && (transfer.Direction != TransferDirections.InternalToOem || transfer.LifecycleStatus != TransferLifecycle.Released))
                return Stale();
            return await RewriteAsync(db, transfer, mail, ct);
        }
        if (mail.RecipientRealm != OemRealms.Internal) return OutboxDecision.Cancel("未知收件人类型", "OEM_MAIL_INVALID");
        var internalRecipient = await OemRecipients.InternalUserAsync(db, recipientId, ct);
        if (internalRecipient is null || !EmailMatches(mail.RecipientEmail, internalRecipient.Email)) return Stale();
        switch (mail.EventType)
        {
            case OemMailEvents.ApprovalPending:
                var taskId = TaskIdFrom(mail.DedupeKey);
                var stillPending = taskId is ulong id && await db.OemFlowTasks.AnyAsync(task => task.Id == id && task.ApproverUserId == recipientId
                    && task.Status == Approval.FlowTaskStatuses.Pending, ct);
                return stillPending ? await RewriteAsync(db, transfer, mail, ct) : Stale();
            case OemMailEvents.TransferReleased:
                if (transfer.Direction != TransferDirections.OemToInternal || transfer.LifecycleStatus != TransferLifecycle.Released) return Stale();
                var holders = await OemRecipients.InternalWithAllAsync(db, ct, OemPermissions.TransferView, OemPermissions.FileDownload);
                return holders.Any(holder => holder.Id == recipientId) ? await RewriteAsync(db, transfer, mail, ct) : Stale();
            case OemMailEvents.ApprovalBlocked:
                var stillBlocked = await db.OemFlowInstances.AnyAsync(instance => instance.TransferId == transferId
                    && instance.Status == Yf.Api.Modules.Oem.Approval.FlowInstanceStatuses.ApprovalBlocked, ct);
                if (!stillBlocked) return Stale();
                if (transfer.InternalSenderUserId == recipientId) return await RewriteAsync(db, transfer, mail, ct);
                var recoverers = await OemRecipients.InternalWithAllAsync(db, ct, OemPermissions.ApprovalRecover);
                return recoverers.Any(recoverer => recoverer.Id == recipientId) ? await RewriteAsync(db, transfer, mail, ct) : Stale();
            default:
                return await RewriteAsync(db, transfer, mail, ct);
        }
    }

    private async Task<OutboxDecision> RewriteAsync(
        YfDbContext db,
        OemTransfer transfer,
        OutboxMailInfo mail,
        CancellationToken ct)
    {
        if (OemMailContentBuilder.MatchesCurrentTemplate(
            options,
            mail.EventType,
            transfer,
            mail.RecipientRealm,
            mail.Subject,
            mail.Body))
            return OutboxDecision.Allow;
        var sizes = await OemTransferFiles.Members(db, transfer.Id)
            .Select(file => file.SizeBytes)
            .ToArrayAsync(ct);
        var content = OemMailContentBuilder.Build(
            options,
            mail.EventType,
            transfer,
            mail.RecipientRealm,
            sizes.Length,
            sizes.Aggregate(0UL, (sum, size) => sum + size));
        return OutboxDecision.Rewrite(content.Subject, content.Body);
    }

    private static OutboxDecision Stale() => OutboxDecision.Cancel("收件人已无权接收该通知", "OEM_RECIPIENT_UNAUTHORIZED");

    private static bool EmailMatches(string queued, string? current)
    {
        var queuedAddress = queued.Trim();
        var currentAddress = current?.Trim() ?? string.Empty;
        return queuedAddress.Length > 0 && currentAddress.Length > 0
            && System.Net.Mail.MailAddress.TryCreate(queuedAddress, out _)
            && System.Net.Mail.MailAddress.TryCreate(currentAddress, out _)
            && string.Equals(queuedAddress, currentAddress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Task id of an approval notice key (<c>oem:approval:{taskId}</c>, optionally followed by <c>:{activation}</c>).</summary>
    private static ulong? TaskIdFrom(string? dedupeKey) =>
        dedupeKey?.StartsWith("oem:approval:", StringComparison.Ordinal) == true
        && ulong.TryParse(dedupeKey["oem:approval:".Length..].Split(':')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
