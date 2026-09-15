using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectNotificationService
{
    internal const string SupersededAcceptanceMailReason = "验收申请已失效或收件人已无验收权限，通知已取消";
    internal const string StaleProjectMailReason = "收件人已无项目访问权限，通知已取消";

    internal static async Task EnqueueMessageAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        ulong messageId,
        string content,
        CurrentUser actor,
        string baseUrl,
        AuditService audit,
        CancellationToken ct)
    {
        var preview = Truncate(content, 80);
        var suffix = content.EnumerateRunes().Count() > 80 ? "…" : string.Empty;
        var targetUrl = ProjectUrl(baseUrl, project.Id, "messages", messageId);
        await EnqueueAsync(
            conn,
            tx,
            project,
            "MESSAGE_CREATED",
            OppositeSide(actor),
            ["project:list"],
            [],
            false,
            actor.Id,
            $"[协作平台] 项目「{project.Name}」有新留言",
            $"项目：{project.Name}\n留言人：工号 {actor.EmployeeNo}\n内容：{preview}{suffix}\n\n请登录平台查看：{targetUrl}\n\n（本邮件由系统自动发送）",
            ct,
            audit);
    }

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
        if (!await NotificationsEnabledAsync(conn, tx, ct))
        {
            return;
        }

        var reviewers = await ProjectReviewerService.ListAsync(conn, tx, project, ct);
        var targetUrl = ProjectUrl(baseUrl, project.Id, "activity");
        var subject = $"[协作平台] 项目「{project.Name}」已提交验收";
        var body = $"项目：{project.Name}\n结果：已提交验收\n确认方：公司\n操作人：工号 {submitter.EmployeeNo}\n\n请登录平台查看：{targetUrl}\n\n（本邮件由系统自动发送）";
        var seenEmails = new HashSet<string>(StringComparer.Ordinal);
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
            if (!seenEmails.Add(reviewer.Email)) continue;

            const string insert = """
                INSERT INTO email_outbox
                    (event_type,project_id,dedupe_key,recipient_user_id,recipient_email,
                     subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
                VALUES
                    ('PROJECT_SUBMITTED',@ProjectId,@DedupeKey,@RecipientUserId,@RecipientEmail,
                     @Subject,@Body,'PENDING',0,NULL,NULL,NULL,UTC_TIMESTAMP(3))
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
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                insert,
                new
                {
                    ProjectId = project.Id,
                    DedupeKey = AcceptanceDedupeKey(project.Id, latestSubmissionId, reviewer.Id),
                    RecipientUserId = reviewer.Id,
                    RecipientEmail = reviewer.Email,
                    Subject = subject,
                    Body = body,
                },
                tx,
                cancellationToken: ct));
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
        var project = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT id AS Id,name AS Name,supplier_id AS SupplierId,status AS Status,
                   confirm_side AS ConfirmSide,created_by AS CreatedBy,
                   responsible_user_id AS ResponsibleUserId,
                   created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM projects WHERE id=@ProjectId
            """,
            new { ProjectId = projectId },
            tx,
            cancellationToken: ct));
        if (project is null
            || project.Status != ProjectStatuses.PendingConfirmation
            || project.ConfirmSide != ProjectWorkflowRules.InternalAcceptanceSide)
        {
            return false;
        }
        var latestSubmissionId = await LatestSubmissionIdAsync(conn, tx, projectId, ct);
        if (latestSubmissionId != submissionId)
        {
            return false;
        }
        var reviewers = await ProjectReviewerService.ListAsync(conn, tx, project, ct);
        return reviewers.Any(reviewer => reviewer.Id == recipientId);
    }

    internal static Task<int> CancelPendingAcceptanceAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        string reason,
        CancellationToken ct) =>
        // A SENDING row may already have crossed the SMTP side-effect boundary.
        // Its worker must record the accepted/failure result; an expired lease is
        // re-claimed later and cancelled by the worker's current-request check.
        conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE email_outbox
            SET status='CANCELLED',next_attempt_at=NULL,last_error=@Reason
            WHERE project_id=@ProjectId AND event_type='PROJECT_SUBMITTED'
              AND sent_at IS NULL AND status IN ('PENDING','FAILED')
            """,
            new { ProjectId = projectId, Reason = reason },
            tx,
            cancellationToken: ct));

    internal static async Task<IReadOnlyList<UserRow>> ParticipantsAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ProjectRow project,
        CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT u.id AS Id, u.employee_no AS EmployeeNo, u.real_name AS RealName,
                   u.email AS Email, u.user_type AS UserType, u.supplier_id AS SupplierId,
                   u.department_id AS DepartmentId, u.status AS Status
            FROM users u
            INNER JOIN projects p ON p.id = @ProjectId
            WHERE u.status = 'ACTIVE'
              AND EXISTS(
                  SELECT 1
                  FROM user_roles ur
                  INNER JOIN roles r ON r.id = ur.role_id AND r.status = 'ACTIVE'
                  INNER JOIN role_permissions rp ON rp.role_id = r.id
                  INNER JOIN permissions permission ON permission.id = rp.permission_id
                  WHERE ur.user_id = u.id AND permission.code = 'project:list'
              )
              AND (
                    (u.user_type = 'INTERNAL' AND (
                        u.id = p.responsible_user_id
                    ))
                    OR
                    (u.user_type = 'SUPPLIER' AND u.supplier_id = p.supplier_id AND EXISTS(
                        SELECT 1 FROM suppliers s
                        WHERE s.id = p.supplier_id AND s.status = 'ACTIVE'
                    ))
              )
            ORDER BY u.id
            """;
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            sql,
            new { ProjectId = project.Id },
            tx,
            cancellationToken: ct));
        return rows.AsList();
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
        if (!await NotificationsEnabledAsync(conn, tx, ct))
        {
            return;
        }

        var recipients = (await ParticipantsAsync(conn, tx, project, ct)).ToList();
        if (targetUsers.Count > 0)
        {
            var participantIds = recipients.Select(user => user.Id).ToHashSet();
            var explicitUsers = await conn.QueryAsync<UserRow>(new CommandDefinition(
                """
                SELECT id AS Id, employee_no AS EmployeeNo, real_name AS RealName,
                       email AS Email, user_type AS UserType, supplier_id AS SupplierId,
                       department_id AS DepartmentId, status AS Status
                FROM users
                WHERE id IN @TargetUserIds AND status='ACTIVE'
                ORDER BY id
                """,
                new { TargetUserIds = targetUsers.ToArray() },
                tx,
                cancellationToken: ct));
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
        var seenEmails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipient in recipients)
        {
            if (excludeUser == recipient.Id)
            {
                continue;
            }

            var sideMatch = targetSide is not null && recipient.UserType == SideUserType(targetSide);
            var userMatch = targetUsers.Contains(recipient.Id);
            if (sideOrUsers ? !sideMatch && !userMatch : targetSide is not null ? !sideMatch : !userMatch)
            {
                continue;
            }
            if (sideMatch
                && !userMatch
                && requiredSidePermissions.Count > 0
                && !await HasAllPermissionsAsync(conn, tx, recipient.Id, requiredSidePermissions, ct))
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
            if (!seenEmails.Add(recipient.Email))
            {
                continue;
            }

            const string insert = """
                INSERT INTO email_outbox
                    (event_type, project_id, dedupe_key, recipient_user_id, recipient_email,
                     subject, body, status, retry_count, next_attempt_at, last_error, sent_at, created_at)
                VALUES
                    (@EventType, @ProjectId, NULL, @RecipientUserId, @RecipientEmail,
                     @Subject, @Body, 'PENDING', 0, NULL, NULL, NULL, UTC_TIMESTAMP(3))
                """;
            await conn.ExecuteAsync(new CommandDefinition(
                insert,
                new
                {
                    EventType = eventType,
                    ProjectId = project.Id,
                    RecipientUserId = recipient.Id,
                    RecipientEmail = recipient.Email,
                    Subject = subject,
                    Body = body,
                },
                tx,
                cancellationToken: ct));
        }
    }

    internal static async Task<bool> IsCurrentProjectRecipientAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        ulong recipientId,
        CancellationToken ct)
    {
        var recipient = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT id AS Id,employee_no AS EmployeeNo,user_type AS UserType,
                   supplier_id AS SupplierId,status AS Status
            FROM users
            WHERE id=@RecipientId AND status='ACTIVE'
            """,
            new { RecipientId = recipientId },
            tx,
            cancellationToken: ct));
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

    private static async Task<bool> HasAllPermissionsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong userId,
        IReadOnlyCollection<string> permissions,
        CancellationToken ct)
    {
        var granted = await AccessService.PermissionCodesAsync(conn, tx, userId, ct);
        return permissions.All(permission => granted.Contains(permission, StringComparer.Ordinal));
    }

    private static string OppositeSide(CurrentUser actor) => actor.IsInternal ? "SUPPLIER" : "COMPANY";

    private static async Task<bool> NotificationsEnabledAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CancellationToken ct)
    {
        var enabledValue = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key = 'notify.enabled'",
            transaction: tx,
            cancellationToken: ct));
        return enabledValue is null
            || enabledValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            || enabledValue.Trim() == "1";
    }

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

    private static Task<ulong?> LatestSubmissionIdAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct) =>
        conn.QuerySingleAsync<ulong?>(new CommandDefinition(
            "SELECT MAX(id) FROM project_status_logs WHERE project_id=@ProjectId AND action='SUBMIT'",
            new { ProjectId = projectId },
            tx,
            cancellationToken: ct));

    private static string SideUserType(string side) => side == "SUPPLIER" ? "SUPPLIER" : "INTERNAL";

    private static string ProjectUrl(string baseUrl, ulong projectId, string tab, ulong? targetId = null) =>
        $"{baseUrl.TrimEnd('/')}/projects/{projectId}?tab={tab}" + (targetId is null ? string.Empty : $"&target={targetId.Value}");

    private static string Truncate(string value, int count) => string.Concat(value.EnumerateRunes().Take(count));
}
