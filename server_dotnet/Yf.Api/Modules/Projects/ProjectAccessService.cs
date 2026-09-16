using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public sealed record ProjectAccess(
    ulong Id,
    ulong? ProjectGroupId,
    ulong SupplierId,
    ulong CreatedBy,
    string Status,
    string? ConfirmSide,
    ulong? ResponsibleUserId = null);

public static class ProjectAccessService
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
            if (actor.SupplierId is null)
            {
                throw ApiException.OutOfScope();
            }
            parameters.Add("ActorSupplierId", actor.SupplierId.Value);
            return ("p.supplier_id=@ActorSupplierId", parameters);
        }
        if (await HasPermissionAsync(conn, tx, actor.Id, "project:view_all", ct))
        {
            return ("1=1", parameters);
        }
        parameters.Add("ActorId", actor.Id);
        return ("p.responsible_user_id=@ActorId", parameters);
    }

    public static async Task<ProjectAccess> RequireViewAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        ulong projectId,
        CancellationToken ct = default)
    {
        if (tx is not null)
        {
            return await RequireViewCoreAsync(conn, tx, actor, projectId, false, ct);
        }

        await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
        var project = await RequireViewCoreAsync(conn, owned, actor, projectId, false, ct);
        await owned.CommitAsync(ct);
        return project;
    }

    public static async Task<ProjectAccess> RequireFileUploadAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        ulong projectId,
        CancellationToken ct = default)
    {
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await RequireFileUploadAsync(conn, owned, actor, projectId, ct);
            await owned.CommitAsync(ct);
            return result;
        }

        var project = await RequireViewCoreAsync(conn, tx, actor, projectId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "file:upload", ct);
        if (project.Status != ProjectStatuses.InProgress)
        {
            throw ApiException.Conflict("项目当前不可上传文件");
        }

        return project;
    }

    public static async Task<ProjectAccess> RequireFileDeleteAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        ulong projectId,
        CancellationToken ct = default)
    {
        AccessService.RequireInternal(actor);
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await RequireFileDeleteAsync(conn, owned, actor, projectId, ct);
            await owned.CommitAsync(ct);
            return result;
        }

        var project = await RequireViewCoreAsync(conn, tx, actor, projectId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "file:delete", ct);
        if (project.Status != ProjectStatuses.InProgress)
        {
            throw ApiException.Conflict("项目当前不可删除文件");
        }

        return project;
    }

    public static async Task<bool> CanDeleteFilesAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        string projectStatus,
        CancellationToken ct = default)
    {
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await CanDeleteFilesAsync(conn, owned, actor, projectStatus, ct);
            await owned.CommitAsync(ct);
            return result;
        }

        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        return current.IsInternal
            && projectStatus == ProjectStatuses.InProgress
            && await HasPermissionAsync(conn, tx, current.Id, "file:delete", ct);
    }

    internal static async Task<bool> HasPermissionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong userId,
        string permission,
        CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1
                FROM user_roles ur
                INNER JOIN roles r ON r.id = ur.role_id AND r.status = 'ACTIVE'
                INNER JOIN role_permissions rp ON rp.role_id = r.id
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE ur.user_id = @UserId AND p.code = @Permission
            )
            """;
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql,
            new { UserId = userId, Permission = permission },
            tx,
            cancellationToken: ct));
    }

    internal static async Task<ProjectAccess> RequireViewCoreAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser actor,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        if (tx is null)
        {
            throw new InvalidOperationException("Project access validation requires a transaction.");
        }
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        return await RequireViewForValidatedActorAsync(conn, tx, current, projectId, forUpdate, ct);
    }

    internal static async Task<ProjectAccess> RequireViewForValidatedActorAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CurrentUser current,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        if (forUpdate)
        {
            var groupId = await conn.QuerySingleOrDefaultAsync<ulong>(new CommandDefinition(
                "SELECT COALESCE(project_group_id,0) FROM projects WHERE id=@ProjectId",
                new { ProjectId = projectId }, tx, cancellationToken: ct));
            if (groupId != 0)
                await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                    "SELECT id FROM project_groups WHERE id=@GroupId FOR UPDATE",
                    new { GroupId = groupId }, tx, cancellationToken: ct));
        }
        var sql = """
            SELECT id AS Id,project_group_id AS ProjectGroupId,supplier_id AS SupplierId,created_by AS CreatedBy,
                   status AS Status, confirm_side AS ConfirmSide,
                   responsible_user_id AS ResponsibleUserId
            FROM projects
            WHERE id = @ProjectId
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);
        var project = await conn.QuerySingleOrDefaultAsync<ProjectAccess>(new CommandDefinition(
            sql,
            new { ProjectId = projectId },
            tx,
            cancellationToken: ct));
        if (project is null)
        {
            throw ApiException.NotFound();
        }

        if (current.UserType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase))
        {
            if (current.SupplierId is null || current.SupplierId.Value != project.SupplierId)
            {
                throw ApiException.OutOfScope();
            }

            var supplierActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM suppliers WHERE id = @SupplierId AND status = 'ACTIVE')",
                new { SupplierId = current.SupplierId.Value },
                tx,
                cancellationToken: ct));
            if (!supplierActive)
            {
                throw ApiException.OutOfScope();
            }

            return project;
        }

        if (await HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct)
            || project.ResponsibleUserId == current.Id)
        {
            return project;
        }
        throw ApiException.OutOfScope();
    }
}

internal static class ProjectStatuses
{
    internal const string Draft = "DRAFT";
    internal const string InProgress = "IN_PROGRESS";
    internal const string PendingConfirmation = "PENDING_CONFIRMATION";
    internal const string Completed = "COMPLETED";
    internal const string Terminated = "TERMINATED";
}
