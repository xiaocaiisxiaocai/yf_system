using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class CollaborationService
{
    private static readonly string[] MeaningfulProjectActions =
        ["START", "RESTART", "SUBMIT", "CONFIRM", "REJECT", "WITHDRAW", "TERMINATE"];

    internal async Task<CollaborationSummaryResponse> SummaryAsync(
        MySqlConnection conn,
        CurrentUser actor,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var canReceivePendingAcceptance = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct));
        if (!current.IsInternal && current.SupplierId is null) throw ApiException.OutOfScope();
        await using var db = EfDb.Use(conn, tx);
        var canViewAll = current.IsInternal
            && await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:view_all", ct);
        var supplierId = current.SupplierId ?? 0;

        // BIT_XOR has no provider LINQ translation. Keep this one parameterized aggregate in SQL
        // rather than loading the complete activity/read history just to calculate a revision.
        var row = await db.Database.SqlQuery<CollaborationFingerprintRow>($"""
            SELECT COUNT(*) AS ActivityCount,COALESCE(MAX(pa.id),0) AS LatestId,
                   CAST(COALESCE(SUM(pa.id),0) AS CHAR) AS ActivitySum,
                   COALESCE(BIT_XOR(pa.id),0) AS ActivityXor,
                   COUNT(cr.activity_id) AS ReadCount,
                   CAST(COALESCE(SUM(CASE WHEN cr.activity_id IS NULL THEN 0 ELSE pa.id END),0) AS CHAR) AS ReadSum,
                   COALESCE(BIT_XOR(CASE WHEN cr.activity_id IS NULL THEN 0 ELSE pa.id END),0) AS ReadXor,
                   COALESCE(SUM(CASE WHEN
                       ((pa.activity_type='FILE' AND pa.action='UPLOAD')
                         OR (pa.activity_type='MESSAGE' AND pa.action='CREATE')
                         OR (pa.activity_type='PROJECT' AND pa.action IN ('START','RESTART','SUBMIT','CONFIRM','REJECT','WITHDRAW','TERMINATE')))
                       AND (pa.activity_type<>'PROJECT' OR pa.action<>'SUBMIT' OR {canReceivePendingAcceptance})
                       AND pa.actor_id<>{current.Id} AND cr.activity_id IS NULL THEN 1 ELSE 0 END),0) AS UnreadCount
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            LEFT JOIN collaboration_reads cr ON cr.activity_id=pa.id AND cr.user_id={current.Id}
            WHERE (({current.IsInternal} AND ({canViewAll} OR p.responsible_user_id={current.Id}))
                OR (NOT {current.IsInternal} AND p.supplier_id={supplierId}))
            """).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return new CollaborationSummaryResponse(row.UnreadCount, row.LatestId, Revision(current.Id, row));
    }

    internal async Task<CollaborationNotificationPage> NotificationsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        bool unreadOnly,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var canReceivePendingAcceptance = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct));
        await using var db = EfDb.Use(conn, tx);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var activities = VisibleMeaningfulActivities(db, visibleProjects, current.Id, canReceivePendingAcceptance);
        var query =
            from activity in activities
            join project in visibleProjects on activity.ProjectId equals project.Id
            join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id
            let isRead = db.CollaborationReads.Any(read =>
                read.ActivityId == activity.Id && read.UserId == current.Id)
            let fileAvailable = activity.ActivityType == "FILE" && activity.TargetId != null
                && db.Files.Any(file => file.Id == activity.TargetId
                    && file.ProjectId == activity.ProjectId && file.Status == "AVAILABLE")
            let messageAvailable = activity.ActivityType == "MESSAGE" && activity.TargetId != null
                && db.Messages.Any(message => message.Id == activity.TargetId
                    && message.ProjectId == activity.ProjectId && message.Status == "NORMAL")
            select new
            {
                Activity = activity,
                ProjectName = project.Name,
                ProjectGroupName = projectGroup.Name,
                IsRead = isRead,
                FileAvailable = fileAvailable,
                MessageAvailable = messageAvailable,
            };

        var unreadCount = (ulong)await query.LongCountAsync(row => !row.IsRead, ct);
        var filtered = unreadOnly ? query.Where(row => !row.IsRead) : query;
        var total = (ulong)await filtered.LongCountAsync(ct);
        var rows = await filtered.OrderByDescending(row => row.Activity.OccurredAt)
            .ThenByDescending(row => row.Activity.Id)
            .Page((actualPage - 1) * size, size)
            .ToArrayAsync(ct);
        var list = rows.Select(row => new CollaborationNotification(
            row.Activity.Id,
            row.Activity.ActivityType,
            row.Activity.Action,
            row.Activity.ProjectId,
            row.ProjectName,
            row.ProjectGroupName,
            row.Activity.ActorName,
            row.Activity.Title,
            row.Activity.ActivityType == "MESSAGE" && !row.MessageAvailable ? null : row.Activity.Summary,
            ProjectJson.Utc(row.Activity.OccurredAt),
            row.Activity.TargetId,
            row.Activity.ActivityType switch
            {
                "PROJECT" => row.Activity.TargetId == row.Activity.ProjectId,
                "FILE" => row.FileAvailable,
                "MESSAGE" => row.MessageAvailable,
                _ => false,
            },
            row.IsRead)).ToArray();
        await tx.CommitAsync(ct);
        return new CollaborationNotificationPage(list, total, actualPage, size, unreadCount);
    }

    internal async Task MarkReadAsync(
        MySqlConnection conn,
        CurrentUser actor,
        MarkCollaborationReadRequest request,
        CancellationToken ct)
    {
        var ids = (request.Ids ?? []).Distinct().Order().ToArray();
        if (ids.Length > 100) throw ApiException.BadRequest("单次标记数量超过上限（100）");

        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var canReceivePendingAcceptance = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct));
        await using var db = EfDb.Use(conn, tx);

        // Lock in stable key order before the no-tracking visibility check and insert. EF cannot
        // translate FOR UPDATE, and interpolating one id at a time keeps every lock parameterized.
        var projectIds = new HashSet<ulong>();
        foreach (var id in ids)
        {
            var locked = await db.ProjectActivities
                .FromSqlInterpolated($"SELECT * FROM project_activities WHERE id={id} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
            if (locked is not null) projectIds.Add(locked.ProjectId);
        }
        foreach (var projectId in projectIds.Order())
        {
            await db.Projects
                .FromSqlInterpolated($"SELECT * FROM projects WHERE id={projectId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
        }

        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var validated = await VisibleMeaningfulActivities(
                db, visibleProjects, current.Id, canReceivePendingAcceptance)
            .Where(activity => Enumerable.Contains(ids, activity.Id))
            .Select(activity => activity.Id)
            .ToArrayAsync(ct);
        if (validated.Length != ids.Length) throw ApiException.OutOfScope("通知不存在或无权访问");

        var existing = await db.CollaborationReads
            .Where(read => read.UserId == current.Id && Enumerable.Contains(ids, read.ActivityId))
            .Select(read => read.ActivityId)
            .ToArrayAsync(ct);
        var existingSet = existing.ToHashSet();
        db.CollaborationReads.AddRange(ids
            .Where(id => !existingSet.Contains(id))
            .Select(id => new CollaborationRead { ActivityId = id, UserId = current.Id }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static IQueryable<ProjectActivity> VisibleMeaningfulActivities(
        YfDbContext db,
        IQueryable<Yf.Api.Infrastructure.Entities.Project> visibleProjects,
        ulong userId,
        bool canReceivePendingAcceptance) =>
        from activity in db.ProjectActivities
        join project in visibleProjects on activity.ProjectId equals project.Id
        where activity.ActorId != null && activity.ActorId != userId
            && ((activity.ActivityType == "FILE" && activity.Action == "UPLOAD")
                || (activity.ActivityType == "MESSAGE" && activity.Action == "CREATE")
                || (activity.ActivityType == "PROJECT"
                    && Enumerable.Contains(MeaningfulProjectActions, activity.Action)))
            && (activity.ActivityType != "PROJECT" || activity.Action != "SUBMIT"
                || canReceivePendingAcceptance)
        select activity;

    private static string Revision(ulong userId, CollaborationFingerprintRow row)
    {
        var value = $"{userId}:{row.ActivityCount}:{row.LatestId}:{row.ActivitySum}:" +
            $"{row.ActivityXor}:{row.ReadCount}:{row.ReadSum}:{row.ReadXor}:{row.UnreadCount}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class CollaborationFingerprintRow
    {
        public ulong ActivityCount { get; init; }
        public ulong LatestId { get; init; }
        public string ActivitySum { get; init; } = "0";
        public ulong ActivityXor { get; init; }
        public ulong ReadCount { get; init; }
        public string ReadSum { get; init; } = "0";
        public ulong ReadXor { get; init; }
        public ulong UnreadCount { get; init; }
    }
}
