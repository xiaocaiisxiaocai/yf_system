using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed record ProjectGroupAccess(
    ulong Id,
    ulong SupplierId,
    ulong CreatedBy,
    string Status,
    ulong? ResponsibleUserId);

internal static class ProjectGroupAccessService
{
    internal static async Task<(string Clause, DynamicParameters Parameters)> VisibleScopeAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        await AccessService.RequirePermissionAsync(conn, tx, actor, "project:list", ct);
        if (!actor.IsInternal)
        {
            if (actor.SupplierId is null) throw ApiException.OutOfScope();
            parameters.Add("ActorSupplierId", actor.SupplierId.Value);
            return ("g.supplier_id=@ActorSupplierId", parameters);
        }
        if (await ProjectAccessService.HasPermissionAsync(conn, tx, actor.Id, "project:view_all", ct))
            return ("1=1", parameters);
        parameters.Add("ActorId", actor.Id);
        return ("g.responsible_user_id=@ActorId", parameters);
    }

    internal static async Task<ProjectGroupAccess> RequireViewAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CurrentUser current,
        ulong groupId,
        bool forUpdate,
        CancellationToken ct)
    {
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var group = await conn.QuerySingleOrDefaultAsync<ProjectGroupAccess>(new CommandDefinition(
            """
            SELECT id AS Id,supplier_id AS SupplierId,created_by AS CreatedBy,status AS Status,
                   responsible_user_id AS ResponsibleUserId
            FROM project_groups WHERE id=@GroupId
            """ + (forUpdate ? " FOR UPDATE" : string.Empty),
            new { GroupId = groupId }, tx, cancellationToken: ct));
        if (group is null) throw ApiException.NotFound();

        if (!current.IsInternal)
        {
            if (current.SupplierId != group.SupplierId) throw ApiException.OutOfScope();
            var active = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM suppliers WHERE id=@SupplierId AND status='ACTIVE')",
                new { SupplierId = group.SupplierId }, tx, cancellationToken: ct));
            if (!active) throw ApiException.OutOfScope();
            return group;
        }
        if (await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct)
            || group.ResponsibleUserId == current.Id)
            return group;
        throw ApiException.OutOfScope();
    }
}
