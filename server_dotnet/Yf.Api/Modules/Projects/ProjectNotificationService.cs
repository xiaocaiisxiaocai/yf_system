using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Projects;

internal static class ProjectNotificationService
{
    internal const string SupersededAcceptanceMailReason = "验收申请已失效或收件人已无验收权限，通知已取消";
    internal const string StaleProjectMailReason = "收件人已无项目访问权限，通知已取消";

    /// <summary>Message e-mails for one recipient and subproject are merged within this window.</summary>
    internal const int MessageSummaryDelayMinutes = 2;
    private const int MessageSummaryMaximumBodyBytes = 60_000;

    /// <summary>
    /// Queues a MESSAGE_CREATED summary for every eligible recipient on the other side. Like the
    /// FILE_UPLOADED summary, one PENDING row per (subproject, recipient) collects the messages posted
    /// within <see cref="MessageSummaryDelayMinutes"/> minutes; every entry keeps its own precise link.
    /// </summary>
    internal static async Task EnqueueMessageAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        ulong messageId,
        string content,
        CurrentUser actor,
        string baseUrl,
        AuditService audit,
        CancellationToken ct,
        IReadOnlyList<UserRow>? participants = null)
    {
        var recipients = await SelectRecipientsAsync(
            conn, tx, project, "MESSAGE_CREATED", OppositeSide(actor), ["project:list"], [], false, actor.Id,
            ct, audit, participants);
        if (recipients.Count == 0) return;

        var safeProjectName = SafeMailLine(project.Name, 100);
        var preview = SafeMailLine(content, 80);
        var suffix = content.EnumerateRunes().Count() > 80 ? "…" : string.Empty;
        var entry = $"- 留言人：工号 {SafeMailLine(actor.EmployeeNo, 64)}\n  内容：{preview}{suffix}\n  {ProjectUrl(baseUrl, project.Id, "messages", messageId)}\n";
        var subject = $"[协作平台] 项目「{safeProjectName}」有新留言";
        var body = $"项目：{safeProjectName}\n以下留言请登录平台查看（本邮件由系统自动发送）：\n\n{entry}";
        await using var db = EfDb.Use(conn, tx);
        var createdAt = await DbClock.UtcNowAsync(db, ct, 3);
        var nextAttemptAt = createdAt.AddMinutes(MessageSummaryDelayMinutes);
        foreach (var recipient in recipients)
        {
            var dedupeKey = MessageSummaryDedupeKey(project.Id, recipient.Id);
            // Same window protocol as the file summary: only an untouched, delayed PENDING row keeps the
            // key; claimed, retried, sent or oversized rows release it so a new window starts.
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE email_outbox
                SET dedupe_key=NULL
                WHERE dedupe_key={{dedupeKey}}
                  AND (status<>'PENDING' OR retry_count<>0 OR sent_at IS NOT NULL OR next_attempt_at IS NULL
                       OR OCTET_LENGTH(body)+OCTET_LENGTH({{entry}})>{{MessageSummaryMaximumBodyBytes}})
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO email_outbox
                    (event_type,project_id,dedupe_key,recipient_user_id,recipient_email,
                     subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
                VALUES
                    ('MESSAGE_CREATED',{{project.Id}},{{dedupeKey}},{{recipient.Id}},{{recipient.Email}},
                     {{subject}},{{body}},'PENDING',0,{{nextAttemptAt}},NULL,NULL,{{createdAt}})
                ON DUPLICATE KEY UPDATE
                    recipient_email=IF(status='PENDING' AND retry_count=0 AND sent_at IS NULL AND next_attempt_at IS NOT NULL,
                                       VALUES(recipient_email),recipient_email),
                    body=IF(status='PENDING' AND retry_count=0 AND sent_at IS NULL AND next_attempt_at IS NOT NULL,
                            CONCAT(body,{{entry}}),body)
                """, ct);
        }
    }

    internal static string MessageSummaryDedupeKey(ulong projectId, ulong recipientUserId) =>
        $"message-summary:{projectId}:{recipientUserId}";

    private static string SafeMailLine(string value, int maximumRunes) =>
        Yf.Api.Modules.Files.UploadService.SafeMailLine(value, maximumRunes);

    internal static async Task EnqueueWorkflowAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        string action,
        string? confirmSide,
        string? reason,
        ulong? latestSubmitterId,
        CurrentUser actor,
        string baseUrl,
        AuditService audit,
        CancellationToken ct)
    {
        var (eventType, verb) = action switch
        {
            "SUBMIT" => ("PROJECT_SUBMITTED", "已提交验收"),
            "CONFIRM" => ("PROJECT_CONFIRMED", "已确认完成"),
            "REJECT" => ("PROJECT_REJECTED", "已驳回"),
            "WITHDRAW" => ("PROJECT_WITHDRAWN", "已撤回验收"),
            _ => (string.Empty, string.Empty),
        };
        if (eventType.Length == 0)
        {
            return;
        }
        if (action == "SUBMIT" && confirmSide != ProjectWorkflowRules.InternalAcceptanceSide)
        {
            throw new InvalidOperationException("项目提交通知的确认方必须为公司内部");
        }

        var latestSubmissionId = action is "SUBMIT" or "CONFIRM" or "REJECT" or "WITHDRAW"
            ? await LatestSubmissionIdAsync(conn, tx, project.Id, ct)
            : null;
        if (action == "SUBMIT")
        {
            await EnqueuePendingAcceptanceAsync(
                conn,
                tx,
                project,
                latestSubmissionId ?? throw new InvalidOperationException("项目提交通知缺少提交记录"),
                actor,
                baseUrl,
                audit,
                ct);
            return;
        }
        if (action is "CONFIRM" or "REJECT" or "WITHDRAW")
        {
            await CancelPendingAcceptanceAsync(conn, tx, project.Id, SupersededAcceptanceMailReason, ct);
        }

        var sideLine = confirmSide switch
        {
            "COMPANY" => "\n确认方：公司",
            "SUPPLIER" => "\n确认方：供应商",
            _ => string.Empty,
        };
        var reasonLine = reason is null ? string.Empty : $"\n驳回原因：{reason}";
        var targetUsers = latestSubmitterId is null ? Array.Empty<ulong>() : [latestSubmitterId.Value];
        var targetSide = action switch
        {
            "SUBMIT" => ProjectWorkflowRules.InternalAcceptanceSide,
            "WITHDRAW" => confirmSide,
            _ => null,
        };
        if (action == "SUBMIT" && targetSide is null)
        {
            throw new InvalidOperationException("项目提交通知缺少确认方");
        }
        if (action is "CONFIRM" or "REJECT" or "WITHDRAW" && latestSubmitterId is null)
        {
            throw new InvalidOperationException("项目结果通知缺少最近提交者");
        }

        IReadOnlyCollection<string> requiredSidePermissions = action switch
        {
            "SUBMIT" => ["project:list", "project:confirm"],
            "WITHDRAW" => ["project:list"],
            _ => [],
        };
        var targetUrl = ProjectUrl(baseUrl, project.Id, "activity");
        await EnqueueAsync(
            conn,
            tx,
            project,
            eventType,
            targetSide,
            requiredSidePermissions,
            targetUsers,
            action == "WITHDRAW",
            action == "WITHDRAW" ? null : actor.Id,
            $"[协作平台] 项目「{project.Name}」{verb}",
            $"项目：{project.Name}\n结果：{verb}{sideLine}\n操作人：工号 {actor.EmployeeNo}{reasonLine}\n\n请登录平台查看：{targetUrl}\n\n（本邮件由系统自动发送）",
            ct,
            audit);
    }

    internal static async Task EnqueuePendingAcceptanceAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        ulong latestSubmissionId,
        CurrentUser submitter,
        string baseUrl,
        AuditService audit,
        CancellationToken ct)
    {
        var policy = await EmailNotificationPolicy.LoadAsync(conn, tx, ct);
        if (!policy.Allows("PROJECT_SUBMITTED", UserTypes.Internal))
        {
            return;
        }

        var reviewers = await ProjectReviewerService.ListAsync(conn, tx, project, ct);
        var targetUrl = ProjectUrl(baseUrl, project.Id, "activity");
        var subject = $"[协作平台] 项目「{project.Name}」已提交验收";
        var body = $"项目：{project.Name}\n结果：已提交验收\n确认方：公司\n操作人：工号 {submitter.EmployeeNo}\n\n请登录平台查看：{targetUrl}\n\n（本邮件由系统自动发送）";
        var seenRecipients = new HashSet<ulong>();
        await using var db = EfDb.Use(conn, tx);
        foreach (var reviewer in reviewers)
        {
            if (reviewer.Id == submitter.Id)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(reviewer.Email))
            {
                await WriteMissingEmailAuditAsync(conn, tx, audit, "PROJECT_SUBMITTED", reviewer, ct);
                continue;
            }
            if (!seenRecipients.Add(reviewer.Id)) continue;

            // This upsert is intentionally atomic: a cancelled request may be re-submitted
            // while another process observes the same unique dedupe key.
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO email_outbox
                    (event_type,project_id,dedupe_key,recipient_user_id,recipient_email,
                     subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
                VALUES
                    ('PROJECT_SUBMITTED',{{project.Id}},{{AcceptanceDedupeKey(project.Id, latestSubmissionId, reviewer.Id)}},
                     {{reviewer.Id}},{{reviewer.Email}},{{subject}},{{body}},'PENDING',0,NULL,NULL,NULL,UTC_TIMESTAMP(3))
                ON DUPLICATE KEY UPDATE
                    event_type=IF(sent_at IS NULL AND status='CANCELLED',VALUES(event_type),event_type),
                    project_id=IF(sent_at IS NULL AND status='CANCELLED',VALUES(project_id),project_id),
                    recipient_user_id=IF(sent_at IS NULL AND status='CANCELLED',VALUES(recipient_user_id),recipient_user_id),
                    recipient_email=IF(sent_at IS NULL AND status='CANCELLED',VALUES(recipient_email),recipient_email),
                    subject=IF(sent_at IS NULL AND status='CANCELLED',VALUES(subject),subject),
                    body=IF(sent_at IS NULL AND status='CANCELLED',VALUES(body),body),
                    retry_count=IF(sent_at IS NULL AND status='CANCELLED',0,retry_count),
                    next_attempt_at=IF(sent_at IS NULL AND status='CANCELLED',NULL,next_attempt_at),
                    last_error=IF(sent_at IS NULL AND status='CANCELLED',NULL,last_error),
                    status=IF(sent_at IS NULL AND status='CANCELLED','PENDING',status)
                """, ct);
        }
    }

    internal static string AcceptanceDedupeKey(ulong projectId, ulong submissionId, ulong recipientId) =>
        $"project-acceptance:{projectId}:{submissionId}:{recipientId}";

    internal static bool TryParseAcceptanceDedupeKey(
        string? value,
        out ulong projectId,
        out ulong submissionId,
        out ulong recipientId)
    {
        projectId = 0;
        submissionId = 0;
        recipientId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var parts = value.Split(':');
        return parts.Length == 4
            && parts[0] == "project-acceptance"
            && ulong.TryParse(parts[1], out projectId)
            && ulong.TryParse(parts[2], out submissionId)
            && ulong.TryParse(parts[3], out recipientId);
    }

    internal static async Task<bool> IsCurrentPendingAcceptanceAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        ulong submissionId,
        ulong recipientId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var project = await ProjectQueries.Rows(db)
            .SingleOrDefaultAsync(row => row.Id == projectId, ct);
        if (project is null
            || project.Status != ProjectStatuses.PendingConfirmation
            || project.ConfirmSide != ProjectWorkflowRules.InternalAcceptanceSide)
        {
            return false;
        }
        if (project.LatestSubmissionId != submissionId)
        {
            return false;
        }
        var reviewers = await ProjectReviewerService.ListAsync(conn, tx, project, ct);
        return reviewers.Any(reviewer => reviewer.Id == recipientId);
    }

    internal static async Task<int> CancelPendingAcceptanceAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        string reason,
        CancellationToken ct)
    {
        // A SENDING row may already have crossed the SMTP side-effect boundary.
        // Its worker must record the accepted/failure result; an expired lease is
        // re-claimed later and cancelled by the worker's current-request check.
        await using var db = EfDb.Use(conn, tx);
        var cancellableStatuses = new[] { MailStatuses.Pending, MailStatuses.Failed };
        return await db.EmailOutbox
            .Where(mail => mail.ProjectId == projectId
                && mail.EventType == "PROJECT_SUBMITTED"
                && mail.SentAt == null
                && Enumerable.Contains(cancellableStatuses, mail.Status))
            .ExecuteUpdateAsync(update => update
                .SetProperty(mail => mail.Status, MailStatuses.Cancelled)
                .SetProperty(mail => mail.NextAttemptAt, (DateTime?)null)
                .SetProperty(mail => mail.LastError, reason), ct);
    }

    internal static async Task<IReadOnlyList<UserRow>> ParticipantsAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ProjectRow project,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var permittedUsers = AccessService.UsersWithPermission(db, "project:list");
        // Drive from the project's primary-key row so users are looked up by id (owner) or by the
        // supplier index, instead of evaluating a correlated project check for every active user.
        var rows = await (
                from currentProject in db.Projects
                where currentProject.Id == project.Id
                from user in db.Users
                where user.Status == AccountStatuses.Active
                    && ((user.UserType == UserTypes.Internal && user.Id == currentProject.ResponsibleUserId)
                        || (user.UserType == UserTypes.Supplier
                            && user.SupplierId == currentProject.SupplierId
                            && db.Suppliers.Any(supplier =>
                                supplier.Id == currentProject.SupplierId && supplier.Status == AccountStatuses.Active)))
                    && permittedUsers.Contains(user.Id)
                select user)
            .OrderBy(user => user.Id)
            .Select(user => new UserRow
            {
                Id = user.Id,
                EmployeeNo = user.EmployeeNo,
                RealName = user.RealName,
                Email = user.Email,
                UserType = user.UserType,
                SupplierId = user.SupplierId,
                DepartmentId = user.DepartmentId,
                Status = user.Status,
            })
            .ToArrayAsync(ct);
        return rows;
    }

    private static async Task EnqueueAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        string eventType,
        string? targetSide,
        IReadOnlyCollection<string> requiredSidePermissions,
        IReadOnlyCollection<ulong> targetUsers,
        bool sideOrUsers,
        ulong? excludeUser,
        string subject,
        string body,
        CancellationToken ct,
        AuditService? audit = null)
    {
        var recipients = await SelectRecipientsAsync(conn, tx, project, eventType, targetSide,
            requiredSidePermissions, targetUsers, sideOrUsers, excludeUser, ct, audit);
        if (recipients.Count == 0) return;
        await using var db = EfDb.Use(conn, tx);
        var databaseNow = await DbClock.UtcNowAsync(db, ct, 3);
        db.EmailOutbox.AddRange(recipients.Select(recipient => new EmailOutbox
        {
            EventType = eventType,
            ProjectId = project.Id,
            RecipientUserId = recipient.Id,
            RecipientEmail = recipient.Email,
            Subject = subject,
            Body = body,
            Status = MailStatuses.Pending,
            RetryCount = 0,
            CreatedAt = databaseNow,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The recipients an event is queued for: current participants (optionally already loaded by the
    /// caller in the same transaction) plus explicit users, filtered by policy, side, side permissions
    /// (one bulk grant query) and e-mail presence (missing addresses are audited when an audit is given).
    /// </summary>
    private static async Task<IReadOnlyList<UserRow>> SelectRecipientsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        string eventType,
        string? targetSide,
        IReadOnlyCollection<string> requiredSidePermissions,
        IReadOnlyCollection<ulong> targetUsers,
        bool sideOrUsers,
        ulong? excludeUser,
        CancellationToken ct,
        AuditService? audit = null,
        IReadOnlyList<UserRow>? participants = null)
    {
        var policy = await EmailNotificationPolicy.LoadAsync(conn, tx, ct);
        if (!policy.Allows(eventType, null))
        {
            return [];
        }

        var recipients = (participants ?? await ParticipantsAsync(conn, tx, project, ct)).ToList();
        if (targetUsers.Count > 0)
        {
            var participantIds = recipients.Select(user => user.Id).ToHashSet();
            var targetUserIds = targetUsers.ToArray();
            await using var db = EfDb.Use(conn, tx);
            var explicitUsers = await db.Users
                .Where(user => Enumerable.Contains(targetUserIds, user.Id) && user.Status == AccountStatuses.Active)
                .OrderBy(user => user.Id)
                .Select(user => new UserRow
                {
                    Id = user.Id,
                    EmployeeNo = user.EmployeeNo,
                    RealName = user.RealName,
                    Email = user.Email,
                    UserType = user.UserType,
                    SupplierId = user.SupplierId,
                    DepartmentId = user.DepartmentId,
                    Status = user.Status,
                })
                .ToArrayAsync(ct);
            foreach (var explicitUser in explicitUsers)
            {
                if (!participantIds.Add(explicitUser.Id))
                {
                    continue;
                }
                if (await IsCurrentProjectRecipientAsync(conn, tx, project.Id, explicitUser.Id, ct))
                {
                    recipients.Add(explicitUser);
                }
            }
        }
        var candidates = new List<(UserRow Recipient, bool NeedsSidePermissions)>();
        foreach (var recipient in recipients)
        {
            if (excludeUser == recipient.Id)
            {
                continue;
            }

            if (!policy.Allows(eventType, recipient.UserType))
            {
                continue;
            }

            var sideMatch = targetSide is not null && recipient.UserType == SideUserType(targetSide);
            var userMatch = targetUsers.Contains(recipient.Id);
            if (sideOrUsers ? !sideMatch && !userMatch : targetSide is not null ? !sideMatch : !userMatch)
            {
                continue;
            }
            candidates.Add((recipient, sideMatch && !userMatch && requiredSidePermissions.Count > 0));
        }
        var sidePermitted = await UsersWithAllPermissionsAsync(conn, tx,
            candidates.Where(item => item.NeedsSidePermissions).Select(item => item.Recipient.Id).ToArray(),
            requiredSidePermissions, ct);

        var seenRecipients = new HashSet<ulong>();
        var selected = new List<UserRow>();
        foreach (var (recipient, needsSidePermissions) in candidates)
        {
            if (needsSidePermissions && !sidePermitted.Contains(recipient.Id))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                if (audit is null)
                {
                    continue;
                }
                await WriteMissingEmailAuditAsync(conn, tx, audit, eventType, recipient, ct);
                continue;
            }
            if (!seenRecipients.Add(recipient.Id))
            {
                continue;
            }
            selected.Add(recipient);
        }
        return selected;
    }

    internal static async Task<bool> IsCurrentProjectRecipientAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        ulong recipientId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        // Mirrors what the recipient could see after signing in: an enabled account that is not waiting
        // for a forced password change and, for supplier accounts, an enabled supplier company.
        var recipient = await db.Users
            .Where(user => user.Id == recipientId
                && user.Status == AccountStatuses.Active
                && !user.MustChangePassword
                && (user.UserType != UserTypes.Supplier
                    || db.Suppliers.Any(supplier => supplier.Id == user.SupplierId && supplier.Status == AccountStatuses.Active)))
            .Select(user => new UserRow
            {
                Id = user.Id,
                EmployeeNo = user.EmployeeNo,
                UserType = user.UserType,
                SupplierId = user.SupplierId,
                Status = user.Status,
            })
            .SingleOrDefaultAsync(ct);
        if (recipient is null)
        {
            return false;
        }

        try
        {
            await ProjectAccessService.RequireViewForValidatedActorAsync(
                conn,
                tx,
                new CurrentUser(recipient.Id, recipient.EmployeeNo, recipient.UserType, recipient.SupplierId),
                projectId,
                false,
                ct);
            return true;
        }
        catch (ApiException)
        {
            return false;
        }
    }

    /// <summary>The subset of <paramref name="userIds"/> holding every permission, in one grant query.</summary>
    private static async Task<IReadOnlySet<ulong>> UsersWithAllPermissionsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        IReadOnlyCollection<ulong> userIds,
        IReadOnlyCollection<string> permissions,
        CancellationToken ct)
    {
        if (userIds.Count == 0 || permissions.Count == 0) return new HashSet<ulong>(userIds);
        var ids = userIds.Distinct().ToArray();
        var codes = permissions.Distinct(StringComparer.Ordinal).ToArray();
        await using var db = EfDb.Use(conn, tx);
        var grants = await AccessService.EffectivePermissionGrants(db)
            .Where(grant => Enumerable.Contains(ids, grant.UserId) && Enumerable.Contains(codes, grant.Code))
            .Select(grant => new { grant.UserId, grant.Code })
            .Distinct()
            .ToArrayAsync(ct);
        return grants.GroupBy(grant => grant.UserId)
            .Where(group => codes.All(code => group.Any(grant => string.Equals(grant.Code, code, StringComparison.Ordinal))))
            .Select(group => group.Key)
            .ToHashSet();
    }

    private static string OppositeSide(CurrentUser actor) => actor.IsInternal ? "SUPPLIER" : "COMPANY";

    private static Task WriteMissingEmailAuditAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        AuditService audit,
        string eventType,
        UserRow recipient,
        CancellationToken ct) =>
        audit.WriteAsync(
            conn,
            tx,
            null,
            "EMAIL_SKIPPED_MISSING_EMAIL",
            "user",
            recipient.Id,
            new
            {
                eventType,
                reason = "RECIPIENT_EMAIL_MISSING",
                employeeNo = recipient.EmployeeNo,
                realName = recipient.RealName,
            },
            null,
            ct);

    private static async Task<ulong?> LatestSubmissionIdAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        return await db.ProjectStatusLogs
            .Where(log => log.ProjectId == projectId && log.Action == "SUBMIT")
            .Select(log => (ulong?)log.Id)
            .MaxAsync(ct);
    }

    private static string SideUserType(string side) => side == "SUPPLIER" ? UserTypes.Supplier : UserTypes.Internal;

    private static string ProjectUrl(string baseUrl, ulong projectId, string tab, ulong? targetId = null) =>
        $"{baseUrl.TrimEnd('/')}/projects/{projectId}?tab={tab}" + (targetId is null ? string.Empty : $"&target={targetId.Value}");

}
