using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class DashboardService
{
    internal async Task<object> PendingProjectsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        if (!await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct))
        {
            await tx.CommitAsync(ct);
            return ProjectJson.Page(Array.Empty<object>(), 0, actualPage, size);
        }
        var (scope, parameters) = await VisibleScopeAsync(conn, tx, current, ct);
        parameters.Add("ConfirmSide", UserSide(current));
        parameters.Add("Offset", (actualPage - 1) * size);
        parameters.Add("Size", size);
        var filter = $"p.status='PENDING_CONFIRMATION' AND p.confirm_side=@ConfirmSide AND {scope}";
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"SELECT COUNT(*) FROM projects p WHERE {filter}", parameters, tx, cancellationToken: ct));
        var rows = await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            $"""
            SELECT p.id AS Id,p.name AS Name,p.status AS Status,p.confirm_side AS ConfirmSide,
                   p.updated_at AS UpdatedAt
            FROM projects p WHERE {filter}
            ORDER BY p.updated_at DESC,p.id DESC LIMIT @Size OFFSET @Offset
            """,
            parameters, tx, cancellationToken: ct));
        var list = rows.Select(row => new
        {
            id = row.Id,
            name = row.Name,
            status = ProjectStatuses.PendingConfirmation,
            confirmSide = row.ConfirmSide == "COMPANY" ? "COMPANY" : "SUPPLIER",
            updatedAt = ProjectJson.Utc(row.UpdatedAt),
        }).ToArray();
        await tx.CommitAsync(ct);
        return ProjectJson.Page(list, total, actualPage, size);
    }

    internal async Task<object> SummaryAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        var (scope, parameters) = await VisibleScopeAsync(conn, tx, current, ct);
        var projects = (await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            $"""
            SELECT p.id AS Id,p.name AS Name,p.status AS Status,p.confirm_side AS ConfirmSide
            FROM projects p WHERE {scope}
            """,
            parameters, tx, cancellationToken: ct))).AsList();
        var projectIds = projects.Select(project => project.Id).ToArray();
        var canConfirm = await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct);
        var unreadMessages = 0UL;
        var recentMessages = Array.Empty<object>();
        if (projectIds.Length > 0)
        {
            unreadMessages = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                """
                SELECT COUNT(*) FROM messages m
                WHERE m.project_id IN @ProjectIds AND m.status='NORMAL' AND m.sender_id<>@UserId
                  AND NOT EXISTS(SELECT 1 FROM message_reads mr WHERE mr.message_id=m.id AND mr.user_id=@UserId)
                """,
                new { ProjectIds = projectIds, UserId = current.Id }, tx, cancellationToken: ct));
            var rows = (await conn.QueryAsync<RecentMessageRow>(new CommandDefinition(
                """
                SELECT m.id AS Id,m.project_id AS ProjectId,m.content AS Content,m.sender_id AS SenderId,
                       m.created_at AS CreatedAt,u.real_name AS SenderName,
                       EXISTS(SELECT 1 FROM message_reads mr WHERE mr.message_id=m.id AND mr.user_id=@UserId) AS ReadByMe
                FROM messages m INNER JOIN users u ON u.id=m.sender_id
                WHERE m.project_id IN @ProjectIds AND m.status='NORMAL'
                ORDER BY m.id DESC LIMIT 5
                """,
                new { ProjectIds = projectIds, UserId = current.Id }, tx, cancellationToken: ct))).AsList();
            var names = projects.ToDictionary(project => project.Id, project => project.Name);
            recentMessages = rows.Select(row => (object)new
            {
                id = row.Id,
                projectId = row.ProjectId,
                projectName = names.GetValueOrDefault(row.ProjectId),
                content = string.Concat(row.Content.EnumerateRunes().Take(60)),
                senderName = row.SenderName,
                createdAt = ProjectJson.Utc(row.CreatedAt),
                unread = row.SenderId != current.Id && !row.ReadByMe,
            }).ToArray();
        }
        await tx.CommitAsync(ct);
        return new
        {
            projectCount = projects.Count,
            activeProjectCount = projects.Count(project => project.Status == ProjectStatuses.InProgress),
            pendingConfirmations = canConfirm
                ? projects.Count(project => project.Status == ProjectStatuses.PendingConfirmation && project.ConfirmSide == UserSide(current))
                : 0,
            unreadMessages,
            recentMessages,
        };
    }

    private static async Task<(string Clause, DynamicParameters Parameters)> VisibleScopeAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CurrentUser actor,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        if (!actor.IsInternal)
        {
            if (actor.SupplierId is null)
            {
                throw ApiException.OutOfScope();
            }
            parameters.Add("ActorSupplierId", actor.SupplierId.Value);
            return ("p.supplier_id=@ActorSupplierId", parameters);
        }
        if (await ProjectAccessService.HasPermissionAsync(conn, tx, actor.Id, "project:view_all", ct))
        {
            return ("1=1", parameters);
        }
        parameters.Add("ActorId", actor.Id);
        return ("(p.created_by=@ActorId OR EXISTS(SELECT 1 FROM project_members pm WHERE pm.project_id=p.id AND pm.user_id=@ActorId))", parameters);
    }

    private static string UserSide(CurrentUser actor) => actor.IsInternal ? "COMPANY" : "SUPPLIER";

    private sealed class RecentMessageRow
    {
        public ulong Id { get; init; }
        public ulong ProjectId { get; init; }
        public string Content { get; init; } = string.Empty;
        public ulong SenderId { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? SenderName { get; init; }
        public bool ReadByMe { get; init; }
    }
}
