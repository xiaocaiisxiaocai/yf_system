using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class CollaborationService
{
    private const string MeaningfulActivity = """
        ((pa.activity_type='FILE' AND pa.action='UPLOAD')
          OR (pa.activity_type='MESSAGE' AND pa.action='CREATE')
          OR (pa.activity_type='PROJECT' AND pa.action IN ('START','RESTART','SUBMIT','CONFIRM','REJECT','WITHDRAW','TERMINATE')))
        """;
    private const string InternalAcceptanceVisibility = """
        (pa.activity_type<>'PROJECT' OR pa.action<>'SUBMIT' OR @CanReceivePendingAcceptance=TRUE)
        """;

    internal async Task<object> SummaryAsync(
        MySqlConnection conn,
        CurrentUser actor,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var (scope, parameters) = await ProjectAccessService.VisibleScopeAsync(conn, tx, current, ct);
        parameters.Add("UserId", current.Id);
        parameters.Add("CanReceivePendingAcceptance", ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct)));
        var row = await conn.QuerySingleAsync<CollaborationFingerprintRow>(new CommandDefinition(
            $"""
            SELECT COUNT(*) AS ActivityCount,COALESCE(MAX(pa.id),0) AS LatestId,
                   CAST(COALESCE(SUM(pa.id),0) AS CHAR) AS ActivitySum,
                   COALESCE(BIT_XOR(pa.id),0) AS ActivityXor,
                   COUNT(cr.activity_id) AS ReadCount,
                   CAST(COALESCE(SUM(CASE WHEN cr.activity_id IS NULL THEN 0 ELSE pa.id END),0) AS CHAR) AS ReadSum,
                   COALESCE(BIT_XOR(CASE WHEN cr.activity_id IS NULL THEN 0 ELSE pa.id END),0) AS ReadXor,
                   COALESCE(SUM(CASE WHEN {MeaningfulActivity} AND {InternalAcceptanceVisibility}
                       AND pa.actor_id<>@UserId AND cr.activity_id IS NULL THEN 1 ELSE 0 END),0) AS UnreadCount
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            LEFT JOIN collaboration_reads cr ON cr.activity_id=pa.id AND cr.user_id=@UserId
            WHERE {scope}
            """,
            parameters, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new
        {
            unreadCount = row.UnreadCount,
            latestId = row.LatestId,
            revision = Revision(current.Id, row),
        };
    }

    internal async Task<object> NotificationsAsync(
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
        var (scope, parameters) = await ProjectAccessService.VisibleScopeAsync(conn, tx, current, ct);
        parameters.Add("UserId", current.Id);
        parameters.Add("CanReceivePendingAcceptance", ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct)));
        parameters.Add("Offset", (actualPage - 1) * size);
        parameters.Add("Size", size);
        var baseFilter = $"{scope} AND {MeaningfulActivity} AND {InternalAcceptanceVisibility} AND pa.actor_id<>@UserId";
        var filter = baseFilter + (unreadOnly ? " AND cr.activity_id IS NULL" : string.Empty);
        var unreadCount = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"""
            SELECT COUNT(*)
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            LEFT JOIN collaboration_reads cr ON cr.activity_id=pa.id AND cr.user_id=@UserId
            WHERE {baseFilter} AND cr.activity_id IS NULL
            """,
            parameters, tx, cancellationToken: ct));
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"""
            SELECT COUNT(*)
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            LEFT JOIN collaboration_reads cr ON cr.activity_id=pa.id AND cr.user_id=@UserId
            WHERE {filter}
            """,
            parameters, tx, cancellationToken: ct));
        var rows = (await conn.QueryAsync<CollaborationNotificationRow>(new CommandDefinition(
            $"""
            SELECT pa.id AS Id,pa.activity_type AS ActivityType,pa.action AS Action,
                   pa.project_id AS ProjectId,p.name AS ProjectName,g.name AS ProjectGroupName,pa.actor_name AS ActorName,
                   pa.title AS Title,
                   CASE WHEN pa.activity_type='MESSAGE' AND message_target.id IS NULL THEN NULL ELSE pa.summary END AS Summary,
                   pa.occurred_at AS OccurredAt,pa.target_id AS TargetId,
                   CASE
                       WHEN pa.activity_type='PROJECT' AND pa.target_id=p.id THEN TRUE
                       WHEN pa.activity_type='FILE' AND file_target.id IS NOT NULL THEN TRUE
                       WHEN pa.activity_type='MESSAGE' AND message_target.id IS NOT NULL THEN TRUE
                       ELSE FALSE
                   END AS TargetAvailable,
                   cr.activity_id IS NOT NULL AS IsRead
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            INNER JOIN project_groups g ON g.id=p.project_group_id
            LEFT JOIN collaboration_reads cr ON cr.activity_id=pa.id AND cr.user_id=@UserId
            LEFT JOIN files file_target ON pa.activity_type='FILE' AND file_target.id=pa.target_id
                AND file_target.project_id=pa.project_id AND file_target.status='AVAILABLE'
            LEFT JOIN messages message_target ON pa.activity_type='MESSAGE' AND message_target.id=pa.target_id
                AND message_target.project_id=pa.project_id AND message_target.status='NORMAL'
            WHERE {filter}
            ORDER BY pa.occurred_at DESC,pa.id DESC
            LIMIT @Size OFFSET @Offset
            """,
            parameters, tx, cancellationToken: ct))).AsList();
        var list = rows.Select(row => new
        {
            id = row.Id,
            type = row.ActivityType,
            action = row.Action,
            projectId = row.ProjectId,
            projectName = row.ProjectName,
            projectGroupName = row.ProjectGroupName,
            actorName = row.ActorName,
            title = row.Title,
            summary = row.Summary,
            occurredAt = ProjectJson.Utc(row.OccurredAt),
            targetId = row.TargetId,
            targetAvailable = row.TargetAvailable,
            read = row.IsRead,
        }).ToArray();
        await tx.CommitAsync(ct);
        return new { list, total, page = actualPage, pageSize = size, unreadCount };
    }

    internal async Task MarkReadAsync(
        MySqlConnection conn,
        CurrentUser actor,
        MarkCollaborationReadRequest request,
        CancellationToken ct)
    {
        var ids = (request.Ids ?? []).Distinct().ToArray();
        if (ids.Length > 100)
        {
            throw ApiException.BadRequest("单次标记数量超过上限（100）");
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }
        var (scope, parameters) = await ProjectAccessService.VisibleScopeAsync(conn, tx, current, ct);
        parameters.Add("UserId", current.Id);
        parameters.Add("CanReceivePendingAcceptance", ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct)));
        parameters.Add("Ids", ids);
        var validated = (await conn.QueryAsync<ulong>(new CommandDefinition(
            $"""
            SELECT pa.id
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            WHERE pa.id IN @Ids AND {scope} AND {MeaningfulActivity}
              AND {InternalAcceptanceVisibility} AND pa.actor_id<>@UserId
            FOR UPDATE
            """,
            parameters, tx, cancellationToken: ct))).AsList();
        if (validated.Count != ids.Length)
        {
            throw ApiException.OutOfScope("通知不存在或无权访问");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT IGNORE INTO collaboration_reads(activity_id,user_id,read_at)
            SELECT pa.id,@UserId,UTC_TIMESTAMP(3)
            FROM project_activities pa
            INNER JOIN projects p ON p.id=pa.project_id
            WHERE pa.id IN @Ids AND {scope} AND {MeaningfulActivity}
              AND {InternalAcceptanceVisibility} AND pa.actor_id<>@UserId
            """,
            parameters, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static string Revision(ulong userId, CollaborationFingerprintRow row)
    {
        var value = $"{userId}:{row.ActivityCount}:{row.LatestId}:{row.ActivitySum}:" +
            $"{row.ActivityXor}:{row.ReadCount}:{row.ReadSum}:{row.ReadXor}:{row.UnreadCount}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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

    private sealed class CollaborationNotificationRow
    {
        public ulong Id { get; init; }
        public string ActivityType { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public ulong ProjectId { get; init; }
        public string ProjectName { get; init; } = string.Empty;
        public string ProjectGroupName { get; init; } = string.Empty;
        public string ActorName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Summary { get; init; }
        public DateTime OccurredAt { get; init; }
        public ulong? TargetId { get; init; }
        public bool TargetAvailable { get; init; }
        public bool IsRead { get; init; }
    }
}
