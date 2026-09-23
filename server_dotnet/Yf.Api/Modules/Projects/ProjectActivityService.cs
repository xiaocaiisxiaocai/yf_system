using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectActivityService(
    IHttpContextAccessor? accessor = null,
    IProjectRealtimePublisher? realtime = null) : IProjectAuditCapture
{
    private static readonly object ScheduledRealtimeKey = new();

    internal static async Task<string> RevisionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var row = await db.ProjectActivities.Where(activity => activity.ProjectId == projectId)
            .GroupBy(_ => 1)
            .Select(group => new { ActivityCount = group.LongCount(), LatestId = group.Max(x => x.Id) })
            .SingleOrDefaultAsync(ct);
        return row is null ? "0:0" : $"{row.ActivityCount}:{row.LatestId}";
    }

    public async Task CaptureAsync(MySqlConnection connection, MySqlTransaction? tx, AuditLog auditLog, string? auditActorName, CancellationToken ct)
    {
        var audit = new AuditRow
        {
            Id = auditLog.Id,
            UserId = auditLog.UserId,
            EmployeeNo = auditLog.EmployeeNo,
            Action = auditLog.Action,
            TargetType = auditLog.TargetType,
            TargetId = auditLog.TargetId,
            Detail = auditLog.Detail,
            CreatedAt = auditLog.CreatedAt,
        };
        if (!IsCapturedAction(audit.Action)) return;
        if (audit.TargetId is null
            || !ulong.TryParse(audit.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId)
            || targetId.ToString(CultureInfo.InvariantCulture) != audit.TargetId)
        {
            return;
        }

        await using var db = EfDb.Use(connection, tx);
        NewActivity? activity = audit.Action switch
        {
            "PROJECT_CREATE" or "PROJECT_UPDATE" or "PROJECT_MEMBERS" or "PROJECT_START"
                or "PROJECT_SUBMIT" or "PROJECT_CONFIRM" or "PROJECT_REJECT" or "PROJECT_WITHDRAW"
                or "PROJECT_TERMINATE" or "PROJECT_RESTART" when audit.TargetType == "project" =>
                await ProjectActivityAsync(db, audit, targetId, ct),
            "FILE_UPLOAD" or "FILE_DELETE" when audit.TargetType == "file" =>
                await FileActivityAsync(db, audit, targetId, ct),
            "MESSAGE_CREATE" or "MESSAGE_DELETE" when audit.TargetType == "message" =>
                await MessageActivityAsync(db, audit, targetId, ct),
            _ => null,
        };
        if (activity is null) return;

        var actorName = string.IsNullOrWhiteSpace(auditActorName)
            ? await ResolveActorNameAsync(db, audit.UserId, audit.EmployeeNo, ct)
            : auditActorName;
        if (await db.ProjectActivities.AnyAsync(row => row.SourceKey == activity.SourceKey, ct)) return;
        var entity = new ProjectActivity
        {
            ProjectId = activity.ProjectId,
            ActivityType = activity.ActivityType,
            Action = activity.Action,
            ActorId = audit.UserId,
            ActorName = actorName,
            OccurredAt = activity.OccurredAt,
            Title = activity.Title,
            Summary = activity.Summary,
            TargetId = activity.TargetId,
            SourceKey = activity.SourceKey,
        };
        db.ProjectActivities.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            return;
        }
        if (activity.ActivityType != "MESSAGE") ScheduleRealtime(activity.ProjectId);
    }

    private static bool IsCapturedAction(string action) => action is
        "PROJECT_CREATE" or "PROJECT_UPDATE" or "PROJECT_MEMBERS" or "PROJECT_START" or "PROJECT_SUBMIT"
        or "PROJECT_CONFIRM" or "PROJECT_REJECT" or "PROJECT_WITHDRAW" or "PROJECT_TERMINATE" or "PROJECT_RESTART"
        or "FILE_UPLOAD" or "FILE_DELETE" or "MESSAGE_CREATE" or "MESSAGE_DELETE";

    private void ScheduleRealtime(ulong projectId)
    {
        var context = accessor?.HttpContext;
        if (context is null || realtime is null) return;
        if (!context.Items.TryGetValue(ScheduledRealtimeKey, out var value)
            || value is not HashSet<ulong> scheduled)
        {
            scheduled = [];
            context.Items[ScheduledRealtimeKey] = scheduled;
        }
        if (!scheduled.Add(projectId)) return;
        context.Response.OnCompleted(async () =>
        {
            if (context.Response.StatusCode >= StatusCodes.Status400BadRequest) return;
            try { await realtime.PublishAsync(projectId, RealtimeChangeKinds.Activity, CancellationToken.None); }
            catch { }
        });
    }

    internal async Task<ProjectActivityPage> ListAsync(
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
            throw ApiException.BadRequest("非法的项目动态类型");

        (DateTime Time, ulong Id)? cursorValue = cursor is null ? null : DecodeCursor(cursor);
        var size = pageSize == 0 ? 20UL : Math.Min(pageSize, 50UL);
        await using var db = EfDb.Use(conn);
        var query = db.ProjectActivities.Where(activity => activity.ProjectId == projectId);
        if (type is not null) query = query.Where(activity => activity.ActivityType == type);
        if (cursorValue is not null)
        {
            var cursorTime = cursorValue.Value.Time;
            var cursorId = cursorValue.Value.Id;
            query = query.Where(activity => activity.OccurredAt < cursorTime
                || (activity.OccurredAt == cursorTime && activity.Id < cursorId));
        }
        var rows = await query.OrderByDescending(activity => activity.OccurredAt)
            .ThenByDescending(activity => activity.Id)
            .Take((int)size + 1)
            .ToArrayAsync(ct);
        var hasMore = rows.Length > (int)size;
        if (hasMore) rows = rows[..^1];

        var fileIds = rows.Where(row => row.ActivityType == "FILE" && row.TargetId is not null)
            .Select(row => row.TargetId!.Value).ToArray();
        var messageIds = rows.Where(row => row.ActivityType == "MESSAGE" && row.TargetId is not null)
            .Select(row => row.TargetId!.Value).ToArray();
        var availableFiles = fileIds.Length == 0
            ? new HashSet<ulong>()
            : (await db.Files.Where(file => Enumerable.Contains(fileIds, file.Id)
                    && file.ProjectId == projectId && file.Status == "AVAILABLE")
                .Select(file => file.Id).ToArrayAsync(ct)).ToHashSet();
        var availableMessages = messageIds.Length == 0
            ? new HashSet<ulong>()
            : (await db.Messages.Where(message => Enumerable.Contains(messageIds, message.Id)
                    && message.ProjectId == projectId && message.Status == "NORMAL")
                .Select(message => message.Id).ToArrayAsync(ct)).ToHashSet();
        var list = rows.Select(row =>
        {
            var available = row.ActivityType switch
            {
                "PROJECT" when row.TargetId == projectId => true,
                "FILE" when row.TargetId is not null => availableFiles.Contains(row.TargetId.Value),
                "MESSAGE" when row.TargetId is not null => availableMessages.Contains(row.TargetId.Value),
                _ => false,
            };
            return new ProjectActivityItem(
                row.Id, row.ActivityType, row.Action, row.ActorName, ProjectJson.Utc(row.OccurredAt), row.Title,
                row.ActivityType == "MESSAGE" && !available ? null : row.Summary, row.TargetId, available);
        }).ToArray();
        var lastActivityAt = await db.ProjectActivities
            .Where(activity => activity.ProjectId == projectId)
            .OrderByDescending(activity => activity.OccurredAt)
            .ThenByDescending(activity => activity.Id)
            .Select(activity => (DateTime?)activity.OccurredAt)
            .FirstOrDefaultAsync(ct);
        return new ProjectActivityPage(
            list,
            hasMore && rows.Length > 0 ? EncodeCursor(rows[^1].OccurredAt, rows[^1].Id) : null,
            new ProjectActivitySummary(
                project.Status,
                project.Status == ProjectStatuses.PendingConfirmation,
                project.ConfirmSide,
                lastActivityAt is null ? null : ProjectJson.Utc(lastActivityAt.Value)));
    }

    private static async Task<NewActivity?> ProjectActivityAsync(
        YfDbContext db,
        AuditRow audit,
        ulong projectId,
        CancellationToken ct)
    {
        var project = await db.Projects.Where(row => row.Id == projectId)
            .Select(row => new { row.Name, row.CreatedAt })
            .SingleOrDefaultAsync(ct);
        if (project is null) return null;
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
        if (action.Length == 0) return null;
        var statusLogId = JsonUlong(audit.Detail, "statusLogId");
        return new(projectId, "PROJECT", action, title, summary,
            action == "CREATE" ? project.CreatedAt : audit.CreatedAt, projectId,
            action == "CREATE" ? $"project:{projectId}:create"
                : statusLogId is not null ? $"project-status-log:{statusLogId}" : $"audit:{audit.Id}");
    }

    private static async Task<NewActivity?> FileActivityAsync(
        YfDbContext db,
        AuditRow audit,
        ulong fileId,
        CancellationToken ct)
    {
        var file = await db.Files.Where(row => row.Id == fileId)
            .Select(row => new { row.ProjectId, row.OriginalName, row.CreatedAt, row.DeletedAt })
            .SingleOrDefaultAsync(ct);
        if (file is null) return null;
        var upload = audit.Action == "FILE_UPLOAD";
        return new(file.ProjectId, "FILE", upload ? "UPLOAD" : "DELETE",
            upload ? "上传文件" : "删除文件", Truncate(file.OriginalName),
            upload ? file.CreatedAt : file.DeletedAt ?? audit.CreatedAt, fileId,
            $"file:{fileId}:{(upload ? "upload" : "delete")}");
    }

    private static async Task<NewActivity?> MessageActivityAsync(
        YfDbContext db,
        AuditRow audit,
        ulong messageId,
        CancellationToken ct)
    {
        var message = await db.Messages.Where(row => row.Id == messageId)
            .Select(row => new
            {
                row.ProjectId,
                row.Content,
                row.CreatedAt,
                row.DeletedAt,
                HasImages = db.MessageImages.Any(image => image.MessageId == row.Id),
            })
            .SingleOrDefaultAsync(ct);
        if (message is null) return null;
        var (action, title, summary) = audit.Action switch
        {
            "MESSAGE_CREATE" => ("CREATE", "发表留言",
                message.Content.Length == 0 && message.HasImages ? "[图片]" : message.Content),
            "MESSAGE_DELETE" => ("DELETE", "删除留言", (string?)null),
            _ => (string.Empty, string.Empty, (string?)null),
        };
        if (action.Length == 0) return null;
        var sourceKey = $"message:{messageId}:{action.ToLowerInvariant()}";
        return new(message.ProjectId, "MESSAGE", action, title,
            action == "CREATE" ? Truncate(summary!) : summary,
            action == "CREATE" ? message.CreatedAt : message.DeletedAt ?? audit.CreatedAt,
            messageId, sourceKey);
    }

    private static async Task<string> ResolveActorNameAsync(
        YfDbContext db,
        ulong? userId,
        string? employeeNo,
        CancellationToken ct)
    {
        if (userId is not null)
        {
            var user = await db.Users.Where(row => row.Id == userId.Value)
                .Select(row => new { row.RealName, row.EmployeeNo })
                .SingleOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(user?.RealName)) return user.RealName;
            if (!string.IsNullOrWhiteSpace(user?.EmployeeNo)) return user.EmployeeNo;
        }
        return string.IsNullOrWhiteSpace(employeeNo) ? "未知" : employeeNo;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: 1062 };

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
            throw ApiException.BadRequest("无效的项目动态游标");
        var micros = unchecked((long)(encoded ^ (1UL << 63)));
        try
        {
            var time = DateTime.UnixEpoch.AddTicks(checked(micros * 10));
            if (time.Year is < 1000 or > 9999) throw ApiException.BadRequest("无效的项目动态游标");
            return (time, id);
        }
        catch (ArgumentOutOfRangeException) { throw ApiException.BadRequest("无效的项目动态游标"); }
        catch (OverflowException) { throw ApiException.BadRequest("无效的项目动态游标"); }
    }

    private static string? WorkflowReason(string? detail) =>
        JsonString(detail, "reason") is { } reason ? Truncate(reason) : null;

    private static string? JsonString(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static ulong? JsonUlong(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value)
                && value.TryGetUInt64(out var result) ? result : null;
        }
        catch (JsonException) { return null; }
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
}
