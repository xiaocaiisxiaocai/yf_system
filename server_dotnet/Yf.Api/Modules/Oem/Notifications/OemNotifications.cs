using System.Globalization;
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

public sealed record MailRecipient(string Realm, ulong Id, string Email, string Name);

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
            .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName)).ToArrayAsync(ct);
    }

    public static async Task<MailRecipient[]> VendorAccountsAsync(YfDbContext db, ulong companyId, CancellationToken ct) =>
        await db.OemAccounts.AsNoTracking()
            .Where(account => account.OemCompanyId == companyId && account.Status == OemStatus.Active
                && db.OemCompanies.Any(company => company.Id == companyId && company.Status == OemStatus.Active))
            .OrderBy(account => account.Id)
            .Select(account => new MailRecipient(OemRealms.Oem, account.Id, account.Email, account.RealName)).ToArrayAsync(ct);

    public static async Task<MailRecipient?> SenderAsync(YfDbContext db, OemTransfer transfer, CancellationToken ct)
    {
        if (transfer.InternalSenderUserId is ulong userId)
            return await db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == OemStatus.Active)
                .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName)).SingleOrDefaultAsync(ct);
        if (transfer.OemSenderAccountId is ulong accountId)
            return (await VendorAccountsAsync(db, transfer.OemCompanyId, ct)).SingleOrDefault(account => account.Id == accountId);
        return null;
    }

    public static async Task<MailRecipient?> InternalUserAsync(YfDbContext db, ulong userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == OemStatus.Active && user.UserType == "INTERNAL")
            .Select(user => new MailRecipient(OemRealms.Internal, user.Id, user.Email, user.RealName)).SingleOrDefaultAsync(ct);
}

/// <summary>
/// Turns OEM domain events into queued mail inside the business transaction (outbox
/// pattern): one message per recipient, deduplicated by event + recipient, never
/// carrying attachments, file names or download links — only a pointer to the page.
/// </summary>
public sealed class OemNotificationHandler(AppOptions options) : IOemEventHandler
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
                    var approverId = await uow.Db.OemFlowTasks.Where(task => task.Id == taskId).Select(task => task.ApproverUserId).SingleAsync(ct);
                    if (await OemRecipients.InternalUserAsync(uow.Db, approverId, ct) is { } approver)
                        await EnqueueAsync(uow, settings, OemMailEvents.ApprovalPending, $"oem:approval:{taskId}", approver, facts,
                            $"待审批：{transfer.Title}", "有一张公司发往 OEM 厂商的文件传递单等待您审批。", null, ct);
                }
                break;
            case TransferReleasedEvent:
                var recipients = transfer.Direction == TransferDirections.InternalToOem
                    ? await OemRecipients.VendorAccountsAsync(uow.Db, transfer.OemCompanyId, ct)
                    : await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.TransferView, OemPermissions.FileDownload);
                foreach (var recipient in recipients)
                    await EnqueueAsync(uow, settings, OemMailEvents.TransferReleased, $"oem:released:{transfer.Id}:{recipient.Realm}:{recipient.Id}",
                        recipient, facts, $"新文件已送达：{transfer.Title}", "有新的文件传递给您，请登录平台查看和下载。", null, ct);
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } releasedSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.SenderReleased, $"oem:sender-released:{transfer.Id}", releasedSender, facts,
                        $"已发布：{transfer.Title}", "您发送的文件传递单已通过检查并发布给接收方。", null, ct);
                break;
            case TransferClosedEvent closed:
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } closedSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.TransferClosed, $"oem:closed:{transfer.Id}", closedSender, facts,
                        $"{TransferStateMachine.Label(closed.Lifecycle)}：{transfer.Title}", closed.Lifecycle switch
                        {
                            TransferLifecycle.Rejected => "您发送的文件传递单已被审批驳回，如需重新发送请创建新的传递单。",
                            TransferLifecycle.Blocked => "您发送的文件传递单中有附件未通过文件校验，传递单已被阻断，附件将被自动清理。",
                            _ => "您发送的文件传递单已被管理员终止。",
                        }, closed.Reason, ct);
                break;
            case ApprovalBlockedEvent blocked:
                var stamp = uow.Now.Ticks.ToString(CultureInfo.InvariantCulture);
                var audience = new List<MailRecipient>();
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } blockedSender) audience.Add(blockedSender);
                audience.AddRange(await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.ApprovalRecover));
                foreach (var person in audience.DistinctBy(item => (item.Realm, item.Id)))
                    await EnqueueAsync(uow, settings, OemMailEvents.ApprovalBlocked, $"oem:blocked:{transfer.Id}:{stamp}:{person.Realm}:{person.Id}", person, facts,
                        $"审批阻断：{transfer.Title}", "文件传递单的审批因审批人不可用而暂停，需要具有异常处置权限的管理员改派或终止。", blocked.Reason, ct);
                break;
            case DraftFileValidationFailedEvent failed:
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } draftSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.ValidationFailed, $"oem:validationfail:{failed.FileId}", draftSender, facts,
                        $"附件未通过文件校验：{transfer.Title}", "草稿中有附件未通过文件校验，请移除该附件后再发送。", null, ct);
                break;
            case FileMissingEvent missing:
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } missingSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.FileMissing, $"oem:missing:{missing.FileId}", missingSender, facts,
                        $"附件不可用：{transfer.Title}", missing.PayloadStatus == PayloadStatuses.MissingUnverified
                            ? "系统恢复后发现传递单中有附件不可用（原因待核实），如需继续传递请重新创建传递单。"
                            : "传递单中有附件在存储中丢失，已禁止下载，如需继续传递请重新创建传递单。", null, ct);
                break;
            case FirstReceiptEvent receipt:
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } receiptSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.FirstReceipt, $"oem:receipt:{receipt.FileId}", receiptSender, facts,
                        $"文件已被接收：{transfer.Title}", "接收方已完整下载传递单中的附件。", null, ct);
                break;
            case FilePurgedEvent purged:
                if (await OemRecipients.SenderAsync(uow.Db, transfer, ct) is { } purgedSender)
                    await EnqueueAsync(uow, settings, OemMailEvents.FilePurged, $"oem:purged:{purged.FileId}", purgedSender, facts,
                        $"附件已按策略删除：{transfer.Title}", "传递单中的附件已按删除策略从服务器删除，相关记录仍可在平台查看。", null, ct);
                break;
            case FilePurgeFailingEvent failing:
                foreach (var admin in await OemRecipients.InternalWithAllAsync(uow.Db, ct, OemPermissions.FilePolicyManage))
                    await EnqueueAsync(uow, settings, OemMailEvents.PurgeFailing, $"oem:purgefail:{failing.FileId}:{failing.Attempts}:{admin.Id}", admin, facts,
                        "OEM 附件清理持续失败", "有到期附件多次清理失败，请检查 OEM 存储目录权限和占用情况。", $"已重试 {failing.Attempts} 次", ct);
                break;
        }
    }

    private sealed record TransferFactsForMail(OemTransfer Transfer, string CompanyName, string SenderName, int FileCount, ulong TotalBytes);

    private static async Task<TransferFactsForMail> FactsAsync(YfDbContext db, OemTransfer transfer, CancellationToken ct)
    {
        var company = await db.OemCompanies.AsNoTracking().Where(item => item.Id == transfer.OemCompanyId).Select(item => item.Name).SingleAsync(ct);
        var sender = transfer.InternalSenderUserId is ulong userId
            ? await db.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.RealName).SingleOrDefaultAsync(ct)
            : await db.OemAccounts.AsNoTracking().Where(account => account.Id == transfer.OemSenderAccountId).Select(account => account.RealName).SingleOrDefaultAsync(ct);
        var sizes = await OemTransferFiles.Members(db, transfer.Id).Select(file => file.SizeBytes).ToArrayAsync(ct);
        return new TransferFactsForMail(transfer, company, sender ?? "-", sizes.Length, sizes.Aggregate(0UL, (sum, size) => sum + size));
    }

    private async Task EnqueueAsync(OemUnitOfWork uow, OemSettings settings, string eventType, string dedupeKey, MailRecipient recipient,
        TransferFactsForMail facts, string subject, string lead, string? reason, CancellationToken ct)
    {
        if (!OemMailEvents.Allowed(settings, eventType) || string.IsNullOrWhiteSpace(recipient.Email)) return;
        var transfer = facts.Transfer;
        var direction = transfer.Direction == TransferDirections.InternalToOem
            ? $"公司 → OEM（{facts.CompanyName}）"
            : $"OEM（{facts.CompanyName}）→ 公司";
        var lines = new List<string>
        {
            $"{recipient.Name}，您好：", string.Empty, lead, string.Empty,
            $"传递单：{transfer.Title}", $"方向：{direction}", $"发送方：{facts.SenderName}",
            $"附件：{facts.FileCount} 个，共 {HumanSize(facts.TotalBytes)}",
        };
        if (!string.IsNullOrWhiteSpace(reason)) lines.Add($"说明：{reason}");
        // Vendors use their own portal; internal staff use the OEM section of the company site.
        var area = recipient.Realm == OemRealms.Oem ? "oem-portal" : "oem";
        lines.AddRange([string.Empty, $"请登录 OEM 文件传递平台查看：{options.WebBaseUrl.TrimEnd('/')}/{area}/transfers/{transfer.Id}", string.Empty,
            "此邮件由系统自动发送，请勿直接回复。"]);
        var body = string.Join("\n", lines);
        var fullSubject = Truncate("[OEM 文件传递] " + subject, 255);
        var key = Truncate(dedupeKey, 128);
        // Idempotent enqueue (a documented raw-SQL boundary): a repeated event never mails twice.
        // Only a dedupe-key conflict is ignored; unlike INSERT IGNORE, truncation or other data
        // errors still fail the business transaction.
        await uow.Db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO email_outbox
            (event_type, dedupe_key, project_id, recipient_user_id, recipient_email, subject, body, status, retry_count, next_attempt_at,
             created_at, recipient_realm, recipient_account_id, oem_transfer_id)
            VALUES ({eventType}, {key}, NULL, NULL, {recipient.Email.Trim()}, {fullSubject}, {body}, 'PENDING', 0, NULL,
             UTC_TIMESTAMP(3), {recipient.Realm}, {recipient.Id}, {transfer.Id})
            ON DUPLICATE KEY UPDATE id = id", ct);
    }

    private static string HumanSize(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024UL * 1024 => $"{bytes / 1024d / 1024:0.##} MB",
        >= 1024UL => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Send-time rules for OEM mail inside the shared mail worker: the OEM switches (not
/// the collaboration ones) apply, and each message is re-validated against the live
/// recipient, account/vendor status and the transfer or task it is about.
/// </summary>
public sealed class OemOutboxPolicy : IOutboxRecipientPolicy
{
    public string EventTypePrefix => OemMailEvents.Prefix;
    public string AuditActionPrefix => "OEM_";

    public async Task<OutboxDecision> EvaluateAsync(MySqlConnection conn, MySqlTransaction tx, OutboxMailInfo mail, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var settings = await OemSettings.LoadAsync(db, ct);
        if (!OemMailEvents.Allowed(settings, mail.EventType))
            return OutboxDecision.Cancel("OEM 邮件提醒规则已关闭，通知已取消", "OEM_NOTIFICATION_POLICY_DISABLED");
        if (mail.RecipientAccountId is not ulong recipientId || mail.OemTransferId is not ulong transferId)
            return OutboxDecision.Cancel("OEM 邮件缺少收件人或传递单信息", "OEM_MAIL_INVALID");
        var transfer = await db.OemTransfers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == transferId, ct);
        if (transfer is null) return OutboxDecision.Cancel("传递单不存在", "OEM_TRANSFER_MISSING");

        if (mail.RecipientRealm == OemRealms.Oem)
        {
            var recipient = await db.OemAccounts.AsNoTracking()
                .Where(account => account.Id == recipientId && account.Status == OemStatus.Active
                    && db.OemCompanies.Any(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active)
                    && account.OemCompanyId == transfer.OemCompanyId)
                .Select(account => account.Email).SingleOrDefaultAsync(ct);
            if (!EmailMatches(mail.RecipientEmail, recipient)) return Stale();
            if (mail.EventType == OemMailEvents.TransferReleased
                && (transfer.Direction != TransferDirections.InternalToOem || transfer.LifecycleStatus != TransferLifecycle.Released))
                return Stale();
            return OutboxDecision.Allow;
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
                return stillPending ? OutboxDecision.Allow : Stale();
            case OemMailEvents.TransferReleased:
                if (transfer.Direction != TransferDirections.OemToInternal || transfer.LifecycleStatus != TransferLifecycle.Released) return Stale();
                var holders = await OemRecipients.InternalWithAllAsync(db, ct, OemPermissions.TransferView, OemPermissions.FileDownload);
                return holders.Any(holder => holder.Id == recipientId) ? OutboxDecision.Allow : Stale();
            case OemMailEvents.ApprovalBlocked:
                var stillBlocked = await db.OemFlowInstances.AnyAsync(instance => instance.TransferId == transferId
                    && instance.Status == Yf.Api.Modules.Oem.Approval.FlowInstanceStatuses.ApprovalBlocked, ct);
                if (!stillBlocked) return Stale();
                if (transfer.InternalSenderUserId == recipientId) return OutboxDecision.Allow;
                var recoverers = await OemRecipients.InternalWithAllAsync(db, ct, OemPermissions.ApprovalRecover);
                return recoverers.Any(recoverer => recoverer.Id == recipientId) ? OutboxDecision.Allow : Stale();
            default:
                return OutboxDecision.Allow;
        }
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

    private static ulong? TaskIdFrom(string? dedupeKey) =>
        dedupeKey?.StartsWith("oem:approval:", StringComparison.Ordinal) == true
        && ulong.TryParse(dedupeKey["oem:approval:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
