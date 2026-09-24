using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectActivityService(
    IHttpContextAccessor? accessor = null,
    IProjectRealtimePublisher? realtime = null,
    ILogger<ProjectActivityService>? logger = null) : IProjectAuditCapture
{
    private static readonly object ScheduledRealtimeKey = new();
    internal static IReadOnlyDictionary<(string TargetType, string AuditAction), AuditActivityMapping> AuditActionMappings { get; } =
        new Dictionary<(string, string), AuditActivityMapping>
        {
            [("project", "PROJECT_CREATE")] = new("PROJECT", "CREATE", "创建项目"),
            [("project", "PROJECT_UPDATE")] = new("PROJECT", "UPDATE", "编辑项目"),
            [("project", "PROJECT_START")] = new("PROJECT", "START", "开始项目"),
            [("project", "PROJECT_RESTART")] = new("PROJECT", "RESTART", "重新开始项目"),
            [("project", "PROJECT_SUBMIT")] = new("PROJECT", "SUBMIT", "提交项目验收"),
            [("project", "PROJECT_CONFIRM")] = new("PROJECT", "CONFIRM", "确认项目完成"),
            [("project", "PROJECT_REJECT")] = new("PROJECT", "REJECT", "驳回项目验收"),
            [("project", "PROJECT_WITHDRAW")] = new("PROJECT", "WITHDRAW", "撤回项目验收"),
            [("project", "PROJECT_TERMINATE")] = new("PROJECT", "TERMINATE", "终止项目"),
            [("file", "FILE_UPLOAD")] = new("FILE", "UPLOAD", "上传文件"),
            [("file", "FILE_DELETE")] = new("FILE", "DELETE", "删除文件"),
            [("message", "MESSAGE_CREATE")] = new("MESSAGE", "CREATE", "发表留言"),
            [("message", "MESSAGE_DELETE")] = new("MESSAGE", "DELETE", "删除留言"),
        };
    internal static IReadOnlySet<(string TargetType, string AuditAction)> KnownIgnoredAuditActions { get; } =
        new HashSet<(string, string)>
        {
            ("project", "PROJECT_COPY"),
            ("project", "PROJECT_DELETE"),
            ("project", "MESSAGE_READ"),
            ("file", "FILE_PREVIEW"),
            ("file", "FILE_DOWNLOAD"),
            ("file", "FILE_BATCH_DOWNLOAD"),
        };

    internal static async Task<string> RevisionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var latestId = await db.ProjectActivities.Where(activity => activity.ProjectId == projectId)
            .Select(activity => (ulong?)activity.Id)
            .MaxAsync(ct) ?? 0;
        return latestId.ToString(CultureInfo.InvariantCulture);
    }

    public Task CaptureAsync(MySqlConnection connection, MySqlTransaction? tx, AuditLog auditLog,
        string? auditActorName, CancellationToken ct) =>
        CaptureBatchAsync(connection, tx, [auditLog], auditActorName, ct);

    public async Task CaptureBatchAsync(MySqlConnection connection, MySqlTransaction? tx,
        IReadOnlyList<AuditLog> auditLogs, string? auditActorName, CancellationToken ct)
    {
        var captured = auditLogs.Select(auditLog => new AuditRow
        {
            Id = auditLog.Id,
            UserId = auditLog.UserId,
            EmployeeNo = auditLog.EmployeeNo,
            Action = auditLog.Action,
            TargetType = auditLog.TargetType,
            TargetId = auditLog.TargetId,
            Detail = auditLog.Detail,
            CreatedAt = auditLog.CreatedAt,
        }).Select(audit => TryCapturedAudit(audit, out var targetId)
            ? new CapturedAudit(audit, targetId)
            : null).OfType<CapturedAudit>().ToArray();
        if (captured.Length == 0) return;

        await using var db = EfDb.Use(connection, tx);
        var projectIds = captured.Where(item => item.Audit.TargetType == "project")
            .Select(item => item.TargetId).Distinct().ToArray();
        var projects = projectIds.Length == 0
            ? new Dictionary<ulong, ProjectActivityTarget>()
            : await db.Projects.Where(row => Enumerable.Contains(projectIds, row.Id))
                .Select(row => new ProjectActivityTarget(row.Id, row.Name, row.CreatedAt))
                .ToDictionaryAsync(row => row.Id, ct);
        var fileIds = captured.Where(item => item.Audit.TargetType == "file")
            .Select(item => item.TargetId).Distinct().ToArray();
        var files = fileIds.Length == 0
            ? new Dictionary<ulong, FileActivityTarget>()
            : await db.Files.Where(row => Enumerable.Contains(fileIds, row.Id))
                .Select(row => new FileActivityTarget(row.Id, row.ProjectId, row.OriginalName, row.CreatedAt, row.DeletedAt))
                .ToDictionaryAsync(row => row.Id, ct);
        var messageIds = captured.Where(item => item.Audit.TargetType == "message")
            .Select(item => item.TargetId).Distinct().ToArray();
        var messages = messageIds.Length == 0
            ? new Dictionary<ulong, MessageActivityTarget>()
            : await db.Messages.Where(row => Enumerable.Contains(messageIds, row.Id))
                .Select(row => new MessageActivityTarget(row.Id, row.ProjectId, row.Content, row.CreatedAt,
                    row.DeletedAt, db.MessageImages.Any(image => image.MessageId == row.Id)))
                .ToDictionaryAsync(row => row.Id, ct);

        var activities = captured.Select(item => NewActivityFor(item, projects, files, messages))
            .OfType<NewActivity>().ToArray();
        if (activities.Length == 0) return;
        var actorName = string.IsNullOrWhiteSpace(auditActorName)
            ? await ResolveActorNameAsync(db, captured[0].Audit.UserId, captured[0].Audit.EmployeeNo, ct)
            : auditActorName;
        var sourceKeys = activities.Select(activity => activity.SourceKey).Distinct().ToArray();
        var existing = (await db.ProjectActivities.Where(row => Enumerable.Contains(sourceKeys, row.SourceKey))
                .Select(row => row.SourceKey).ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var entities = activities.Where(activity => !existing.Contains(activity.SourceKey)).Select(activity => new ProjectActivity
        {
            ProjectId = activity.ProjectId,
            ActivityType = activity.ActivityType,
            Action = activity.Action,
            ActorId = captured[0].Audit.UserId,
            ActorName = actorName,
            OccurredAt = activity.OccurredAt,
            Title = activity.Title,
            Summary = activity.Summary,
            TargetId = activity.TargetId,
            SourceKey = activity.SourceKey,
        }).ToArray();
        if (entities.Length == 0) return;
        db.ProjectActivities.AddRange(entities);
        IReadOnlyCollection<ProjectActivity> inserted = entities;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            foreach (var entity in entities) db.Entry(entity).State = EntityState.Detached;
            var recovered = new List<ProjectActivity>();
            foreach (var entity in entities)
            {
                if (await db.ProjectActivities.AnyAsync(row => row.SourceKey == entity.SourceKey, ct)) continue;
                db.ProjectActivities.Add(entity);
                try
                {
                    await db.SaveChangesAsync(ct);
                    recovered.Add(entity);
                }
                catch (DbUpdateException retry) when (IsDuplicateKey(retry))
                {
                    db.Entry(entity).State = EntityState.Detached;
                }
            }
            inserted = recovered;
        }
        foreach (var projectId in inserted.Where(activity => activity.ActivityType != "MESSAGE")
                     .Select(activity => activity.ProjectId).Distinct())
            ScheduleRealtime(projectId);
    }

    private static bool TryCapturedAudit(AuditRow audit, out ulong targetId)
    {
        targetId = 0;
        if (audit.TargetType is not ("project" or "file" or "message")) return false;
        var key = (audit.TargetType, audit.Action);
        if (!AuditActionMappings.ContainsKey(key))
        {
            if (KnownIgnoredAuditActions.Contains(key)) return false;
            throw new InvalidOperationException(
                $"Audit action '{audit.Action}' for target '{audit.TargetType}' has no ProjectActivity mapping or explicit ignore contract.");
        }
        return audit.TargetId is not null
            && ulong.TryParse(audit.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out targetId)
            && targetId.ToString(CultureInfo.InvariantCulture) == audit.TargetId;
    }

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
            catch (OperationCanceledException error)
            {
                logger?.LogDebug(error,
                    "Realtime activity notification was canceled after response completion for project {ProjectId}",
                    projectId);
            }
            catch (Exception error)
            {
                logger?.LogWarning(error,
                    "Realtime activity notification failed after response completion for project {ProjectId}",
                    projectId);
            }
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
                    && file.ProjectId == projectId && file.Status == FileStatuses.Available)
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
        var lastActivityAt = type is null && cursorValue is null
            ? rows.FirstOrDefault()?.OccurredAt
            : await db.ProjectActivities.Where(activity => activity.ProjectId == projectId)
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

    private static NewActivity? NewActivityFor(
        CapturedAudit captured,
        IReadOnlyDictionary<ulong, ProjectActivityTarget> projects,
        IReadOnlyDictionary<ulong, FileActivityTarget> files,
        IReadOnlyDictionary<ulong, MessageActivityTarget> messages)
    {
        var audit = captured.Audit;
        var mapping = AuditActionMappings[(audit.TargetType!, audit.Action)];
        if (mapping.ActivityType == "PROJECT")
        {
            if (!projects.TryGetValue(captured.TargetId, out var project)) return null;
            var summary = mapping.Action is "CREATE" or "UPDATE"
                ? Truncate(project.Name)
                : mapping.Action == "MEMBERS_CHANGE" ? null : WorkflowReason(audit.Detail);
            var statusLogId = JsonUlong(audit.Detail, "statusLogId");
            return new(captured.TargetId, mapping.ActivityType, mapping.Action, mapping.Title, summary,
                mapping.Action == "CREATE" ? project.CreatedAt : audit.CreatedAt, captured.TargetId,
                mapping.Action == "CREATE" ? $"project:{captured.TargetId}:create"
                : statusLogId is not null ? $"project-status-log:{statusLogId}" : $"audit:{audit.Id}");
        }
        if (mapping.ActivityType == "FILE")
        {
            if (!files.TryGetValue(captured.TargetId, out var file)) return null;
            var upload = mapping.Action == "UPLOAD";
            return new(file.ProjectId, mapping.ActivityType, mapping.Action, mapping.Title,
                Truncate(file.OriginalName), upload ? file.CreatedAt : file.DeletedAt ?? audit.CreatedAt,
                captured.TargetId, $"file:{captured.TargetId}:{mapping.Action.ToLowerInvariant()}");
        }
        if (!messages.TryGetValue(captured.TargetId, out var message)) return null;
        var create = mapping.Action == "CREATE";
        var messageSummary = create
            ? Truncate(message.Content.Length == 0 && message.HasImages ? "[图片]" : message.Content)
            : null;
        return new(message.ProjectId, mapping.ActivityType, mapping.Action, mapping.Title, messageSummary,
            create ? message.CreatedAt : message.DeletedAt ?? audit.CreatedAt,
            captured.TargetId, $"message:{captured.TargetId}:{mapping.Action.ToLowerInvariant()}");
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

    internal sealed record AuditActivityMapping(string ActivityType, string Action, string Title);
    private sealed record CapturedAudit(AuditRow Audit, ulong TargetId);
    private sealed record ProjectActivityTarget(ulong Id, string Name, DateTime CreatedAt);
    private sealed record FileActivityTarget(
        ulong Id, ulong ProjectId, string OriginalName, DateTime CreatedAt, DateTime? DeletedAt);
    private sealed record MessageActivityTarget(
        ulong Id, ulong ProjectId, string Content, DateTime CreatedAt, DateTime? DeletedAt, bool HasImages);
}
