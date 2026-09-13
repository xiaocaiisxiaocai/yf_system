using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectActivityService : IProjectAuditCapture
{
    internal static async Task<string> RevisionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleAsync<ProjectActivityRevisionRow>(new CommandDefinition(
            "SELECT COUNT(*) AS ActivityCount,COALESCE(MAX(id),0) AS LatestId FROM project_activities WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        return $"{row.ActivityCount}:{row.LatestId}";
    }

    public async Task CaptureAsync(MySqlConnection db, MySqlTransaction? tx, ulong auditId, CancellationToken ct)
    {
        var audit = await db.QuerySingleOrDefaultAsync<AuditRow>(new CommandDefinition(
            """
            SELECT id AS Id,user_id AS UserId,employee_no AS EmployeeNo,action AS Action,
                   target_type AS TargetType,target_id AS TargetId,CAST(detail AS CHAR) AS Detail,
                   created_at AS CreatedAt
            FROM audit_logs WHERE id=@AuditId
            """,
            new { AuditId = auditId }, tx, cancellationToken: ct));
        if (audit?.TargetId is null
            || !ulong.TryParse(audit.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId)
            || targetId.ToString(CultureInfo.InvariantCulture) != audit.TargetId)
        {
            return;
        }

        NewActivity? activity = audit.Action switch
        {
            "PROJECT_CREATE" or "PROJECT_UPDATE" or "PROJECT_MEMBERS" or "PROJECT_START"
                or "PROJECT_SUBMIT" or "PROJECT_CONFIRM" or "PROJECT_REJECT" or "PROJECT_WITHDRAW"
                or "PROJECT_TERMINATE" or "PROJECT_RESTART" when audit.TargetType == "project" =>
                await ProjectActivityAsync(db, tx, audit, targetId, ct),
            "FILE_UPLOAD" or "FILE_DELETE" when audit.TargetType == "file" =>
                await FileActivityAsync(db, tx, audit, targetId, ct),
            "MESSAGE_CREATE" or "MESSAGE_DELETE" when audit.TargetType == "message" =>
                await MessageActivityAsync(db, tx, audit, targetId, ct),
            _ => null,
        };
        if (activity is null)
        {
            return;
        }
        var actorName = await ResolveActorNameAsync(db, tx, audit.UserId, audit.EmployeeNo, ct);
        await db.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO project_activities
                (project_id,activity_type,action,actor_id,actor_name,occurred_at,title,summary,target_id,source_key)
            VALUES
                (@ProjectId,@ActivityType,@Action,@ActorId,@ActorName,@OccurredAt,@Title,@Summary,@TargetId,@SourceKey)
            """,
            new
            {
                activity.ProjectId,
                activity.ActivityType,
                activity.Action,
                ActorId = audit.UserId,
                ActorName = actorName,
                activity.OccurredAt,
                activity.Title,
                activity.Summary,
                activity.TargetId,
                activity.SourceKey,
            },
            tx,
            cancellationToken: ct));
    }

    internal async Task<object> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string? activityType,
        string? cursor,
        ulong pageSize,
        CancellationToken ct)
    {
        var project = await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var type = string.IsNullOrWhiteSpace(activityType) ? null : activityType.Trim().ToUpperInvariant();
        if (type is not null and not ("PROJECT" or "FILE" or "MESSAGE"))
        {
            throw ApiException.BadRequest("非法的项目动态类型");
        }
        (DateTime Time, ulong Id)? cursorValue = cursor is null ? null : DecodeCursor(cursor);
        var size = pageSize == 0 ? 20UL : Math.Min(pageSize, 50UL);
        var clauses = new List<string> { "project_id=@ProjectId" };
        var parameters = new DynamicParameters(new { ProjectId = projectId, Size = size + 1 });
        if (type is not null)
        {
            clauses.Add("activity_type=@ActivityType");
            parameters.Add("ActivityType", type);
        }
        if (cursorValue is not null)
        {
            clauses.Add("(occurred_at<@CursorTime OR (occurred_at=@CursorTime AND id<@CursorId))");
            parameters.Add("CursorTime", cursorValue.Value.Time);
            parameters.Add("CursorId", cursorValue.Value.Id);
        }
        var rows = (await conn.QueryAsync<ActivityRow>(new CommandDefinition(
            $"""
            SELECT id AS Id,project_id AS ProjectId,activity_type AS ActivityType,action AS Action,
                   actor_id AS ActorId,actor_name AS ActorName,occurred_at AS OccurredAt,title AS Title,
                   summary AS Summary,target_id AS TargetId,source_key AS SourceKey
            FROM project_activities WHERE {string.Join(" AND ", clauses)}
            ORDER BY occurred_at DESC,id DESC LIMIT @Size
            """,
            parameters,
            cancellationToken: ct))).AsList();
        var hasMore = rows.Count > (int)size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var fileIds = rows.Where(row => row.ActivityType == "FILE" && row.TargetId is not null).Select(row => row.TargetId!.Value).ToArray();
        var messageIds = rows.Where(row => row.ActivityType == "MESSAGE" && row.TargetId is not null).Select(row => row.TargetId!.Value).ToArray();
        var availableFiles = fileIds.Length == 0
            ? new HashSet<ulong>()
            : (await conn.QueryAsync<ulong>(new CommandDefinition(
                "SELECT id FROM files WHERE id IN @Ids AND project_id=@ProjectId AND status='AVAILABLE'",
                new { Ids = fileIds, ProjectId = projectId }, cancellationToken: ct))).ToHashSet();
        var availableMessages = messageIds.Length == 0
            ? new HashSet<ulong>()
            : (await conn.QueryAsync<ulong>(new CommandDefinition(
                "SELECT id FROM messages WHERE id IN @Ids AND project_id=@ProjectId AND status='NORMAL'",
                new { Ids = messageIds, ProjectId = projectId }, cancellationToken: ct))).ToHashSet();
        var list = rows.Select(row =>
        {
            var available = row.ActivityType switch
            {
                "PROJECT" when row.TargetId == projectId => true,
                "FILE" when row.TargetId is not null => availableFiles.Contains(row.TargetId.Value),
                "MESSAGE" when row.TargetId is not null => availableMessages.Contains(row.TargetId.Value),
                _ => false,
            };
            return new
            {
                id = row.Id,
                type = row.ActivityType,
                action = row.Action,
                actorName = row.ActorName,
                occurredAt = ProjectJson.Utc(row.OccurredAt),
                title = row.Title,
                summary = row.ActivityType == "MESSAGE" && !available ? null : row.Summary,
                targetId = row.TargetId,
                targetAvailable = available,
            };
        }).ToArray();
        var lastActivityAt = await conn.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT occurred_at FROM project_activities WHERE project_id=@ProjectId ORDER BY occurred_at DESC,id DESC LIMIT 1",
            new { ProjectId = projectId }, cancellationToken: ct));
        return new
        {
            list,
            nextCursor = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].OccurredAt, rows[^1].Id) : null,
            summary = new
            {
                status = project.Status,
                pendingConfirmation = project.Status == ProjectStatuses.PendingConfirmation,
                confirmSide = project.ConfirmSide,
                lastActivityAt = lastActivityAt is null ? (DateTime?)null : ProjectJson.Utc(lastActivityAt.Value),
            },
        };
    }

    private static async Task<NewActivity?> ProjectActivityAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        AuditRow audit,
        ulong projectId,
        CancellationToken ct)
    {
        var project = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            "SELECT id AS Id,name AS Name,created_at AS CreatedAt FROM projects WHERE id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        if (project is null)
        {
            return null;
        }
        var (action, title, summary) = audit.Action switch
        {
            "PROJECT_CREATE" => ("CREATE", "创建项目", Truncate(project.Name)),
            "PROJECT_UPDATE" => ("UPDATE", "编辑项目", Truncate(project.Name)),
            "PROJECT_MEMBERS" => ("MEMBERS_CHANGE", "调整项目成员", null),
            "PROJECT_START" => ("START", "开始项目", WorkflowReason(audit.Detail)),
            "PROJECT_RESTART" => ("RESTART", "重新开始项目", WorkflowReason(audit.Detail)),
            "PROJECT_SUBMIT" => ("SUBMIT", "提交项目验收", WorkflowReason(audit.Detail)),
            "PROJECT_CONFIRM" => ("CONFIRM", "确认项目完成", WorkflowReason(audit.Detail)),
            "PROJECT_REJECT" => ("REJECT", "驳回项目验收", WorkflowReason(audit.Detail)),
            "PROJECT_WITHDRAW" => ("WITHDRAW", "撤回项目验收", WorkflowReason(audit.Detail)),
            "PROJECT_TERMINATE" => ("TERMINATE", "终止项目", WorkflowReason(audit.Detail)),
            _ => (string.Empty, string.Empty, null),
        };
        if (action.Length == 0)
        {
            return null;
        }
        var statusLogId = JsonUlong(audit.Detail, "statusLogId");
        return new(
            projectId,
            "PROJECT",
            action,
            title,
            summary,
            action == "CREATE" ? project.CreatedAt : audit.CreatedAt,
            projectId,
            action == "CREATE" ? $"project:{projectId}:create" : statusLogId is not null ? $"project-status-log:{statusLogId}" : $"audit:{audit.Id}");
    }

    private static async Task<NewActivity?> FileActivityAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        AuditRow audit,
        ulong fileId,
        CancellationToken ct)
    {
        var file = await conn.QuerySingleOrDefaultAsync<FileActivityRow>(new CommandDefinition(
            "SELECT project_id AS ProjectId,original_name AS OriginalName,created_at AS CreatedAt,deleted_at AS DeletedAt FROM files WHERE id=@FileId",
            new { FileId = fileId }, tx, cancellationToken: ct));
        if (file is null)
        {
            return null;
        }
        var upload = audit.Action == "FILE_UPLOAD";
        return new(file.ProjectId, "FILE", upload ? "UPLOAD" : "DELETE", upload ? "上传文件" : "删除文件",
            Truncate(file.OriginalName), upload ? file.CreatedAt : file.DeletedAt ?? audit.CreatedAt, fileId,
            $"file:{fileId}:{(upload ? "upload" : "delete")}");
    }

    private static async Task<NewActivity?> MessageActivityAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        AuditRow audit,
        ulong messageId,
        CancellationToken ct)
    {
        var message = await conn.QuerySingleOrDefaultAsync<MessageActivityRow>(new CommandDefinition(
            "SELECT project_id AS ProjectId,content AS Content,created_at AS CreatedAt,deleted_at AS DeletedAt FROM messages WHERE id=@MessageId",
            new { MessageId = messageId }, tx, cancellationToken: ct));
        if (message is null)
        {
            return null;
        }
        var create = audit.Action == "MESSAGE_CREATE";
        return new(message.ProjectId, "MESSAGE", create ? "CREATE" : "DELETE", create ? "发表留言" : "删除留言",
            create ? Truncate(message.Content) : null, create ? message.CreatedAt : message.DeletedAt ?? audit.CreatedAt,
            messageId, $"message:{messageId}:{(create ? "create" : "delete")}");
    }

    private static async Task<string> ResolveActorNameAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong? userId,
        string? employeeNo,
        CancellationToken ct)
    {
        if (userId is not null)
        {
            var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
                "SELECT real_name AS RealName,employee_no AS EmployeeNo FROM users WHERE id=@UserId",
                new { UserId = userId.Value }, tx, cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(user?.RealName)) return user.RealName;
            if (!string.IsNullOrWhiteSpace(user?.EmployeeNo)) return user.EmployeeNo;
        }
        return string.IsNullOrWhiteSpace(employeeNo) ? "未知" : employeeNo;
    }

    private static string EncodeCursor(DateTime time, ulong id)
    {
        var utc = ProjectJson.Utc(time);
        var micros = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        var encoded = unchecked((ulong)micros) ^ (1UL << 63);
        return $"{encoded:x16}{id:x16}";
    }

    private static (DateTime Time, ulong Id) DecodeCursor(string value)
    {
        if (value.Length != 32
            || !ulong.TryParse(value[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var encoded)
            || !ulong.TryParse(value[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            throw ApiException.BadRequest("无效的项目动态游标");
        }
        var micros = unchecked((long)(encoded ^ (1UL << 63)));
        try
        {
            var time = DateTime.UnixEpoch.AddTicks(checked(micros * 10));
            if (time.Year is < 1000 or > 9999)
            {
                throw ApiException.BadRequest("无效的项目动态游标");
            }
            return (time, id);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw ApiException.BadRequest("无效的项目动态游标");
        }
        catch (OverflowException)
        {
            throw ApiException.BadRequest("无效的项目动态游标");
        }
    }

    private static string? WorkflowReason(string? detail) => JsonString(detail, "reason") is { } reason ? Truncate(reason) : null;

    private static string? JsonString(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ulong? JsonUlong(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.TryGetUInt64(out var result) ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : string.Concat(trimmed.EnumerateRunes().Take(160));
    }

    private sealed record NewActivity(
        ulong ProjectId,
        string ActivityType,
        string Action,
        string Title,
        string? Summary,
        DateTime OccurredAt,
        ulong? TargetId,
        string SourceKey);

    private sealed class FileActivityRow
    {
        public ulong ProjectId { get; init; }
        public string OriginalName { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class MessageActivityRow
    {
        public ulong ProjectId { get; init; }
        public string Content { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class ProjectActivityRevisionRow
    {
        public ulong ActivityCount { get; init; }
        public ulong LatestId { get; init; }
    }
}
