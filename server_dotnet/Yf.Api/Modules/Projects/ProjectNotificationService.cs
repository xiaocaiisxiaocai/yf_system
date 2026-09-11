using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectNotificationService
{
    internal static async Task EnqueueMessageAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectRow project,
        string content,
        CurrentUser actor,
        string baseUrl,
        AuditService audit,
        CancellationToken ct)
    {
        var preview = Truncate(content, 80);
        var suffix = content.EnumerateRunes().Count() > 80 ? "…" : string.Empty;
        await EnqueueAsync(
            conn,
            tx,
            project,
            "MESSAGE_CREATED",
            OppositeSide(actor),
            null,
            [],
            false,
            actor.Id,
            $"[协作平台] 项目「{project.Name}」有新留言",
            $"项目：{project.Name}\n留言人：工号 {actor.EmployeeNo}\n内容：{preview}{suffix}\n\n请登录平台查看：{baseUrl}\n\n（本邮件由系统自动发送）",
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
            "SUBMIT" or "WITHDRAW" => confirmSide,
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

        await EnqueueAsync(
            conn,
            tx,
            project,
            eventType,
            targetSide,
            action == "SUBMIT" ? "project:confirm" : null,
            targetUsers,
            action == "WITHDRAW",
            action == "WITHDRAW" ? null : actor.Id,
            $"[协作平台] 项目「{project.Name}」{verb}",
            $"项目：{project.Name}\n结果：{verb}{sideLine}\n操作人：工号 {actor.EmployeeNo}{reasonLine}\n\n请登录平台查看：{baseUrl}\n\n（本邮件由系统自动发送）",
            ct,
            audit);
    }

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
            WHERE u.status = 'ACTIVE'
              AND (
                    (u.user_type = 'INTERNAL' AND (
                        u.id = @CreatedBy OR EXISTS(
                            SELECT 1 FROM project_members pm
                            WHERE pm.project_id = @ProjectId AND pm.user_id = u.id
                        )
                    ))
                    OR
                    (u.user_type = 'SUPPLIER' AND u.supplier_id = @SupplierId AND EXISTS(
                        SELECT 1 FROM suppliers s
                        WHERE s.id = @SupplierId AND s.status = 'ACTIVE'
                    ))
              )
            ORDER BY u.id
            """;
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            sql,
            new { project.CreatedBy, ProjectId = project.Id, project.SupplierId },
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
        string? requiredPermission,
        IReadOnlyCollection<ulong> targetUsers,
        bool sideOrUsers,
        ulong? excludeUser,
        string subject,
        string body,
        CancellationToken ct,
        AuditService? audit = null)
    {
        var enabledValue = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key = 'notify.enabled'",
            transaction: tx,
            cancellationToken: ct));
        if (enabledValue is not null
            && !enabledValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            && enabledValue.Trim() != "1")
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
            recipients.AddRange(explicitUsers.Where(user => participantIds.Add(user.Id)));
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
            if (requiredPermission is not null
                && !await ProjectAccessService.HasPermissionAsync(conn, tx, recipient.Id, requiredPermission, ct))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                if (audit is null)
                {
                    continue;
                }
                await audit.WriteAsync(
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

    private static string OppositeSide(CurrentUser actor) => actor.IsInternal ? "SUPPLIER" : "COMPANY";

    private static string SideUserType(string side) => side == "SUPPLIER" ? "SUPPLIER" : "INTERNAL";

    private static string Truncate(string value, int count) => string.Concat(value.EnumerateRunes().Take(count));
}
