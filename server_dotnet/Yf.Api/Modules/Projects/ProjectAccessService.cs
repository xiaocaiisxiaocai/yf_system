using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public sealed record ProjectAccess(
    ulong Id,
    ulong SupplierId,
    ulong CreatedBy,
    string Status,
    string? ConfirmSide);

public static class ProjectAccessService
{
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

        await using var owned = await conn.BeginTransactionAsync(ct);
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
            await using var owned = await conn.BeginTransactionAsync(ct);
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
        if (tx is null)
        {
            await using var owned = await conn.BeginTransactionAsync(ct);
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
        CancellationToken ct = default)
    {
        if (tx is null)
        {
            await using var owned = await conn.BeginTransactionAsync(ct);
            var result = await CanDeleteFilesAsync(conn, owned, actor, ct);
            await owned.CommitAsync(ct);
            return result;
        }

        await AccessService.LockActorAsync(conn, tx, actor, ct);
        return await HasPermissionAsync(conn, tx, actor.Id, "file:delete", ct);
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
        var sql = """
            SELECT id AS Id, supplier_id AS SupplierId, created_by AS CreatedBy,
                   status AS Status, confirm_side AS ConfirmSide
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
            || project.CreatedBy == current.Id)
        {
            return project;
        }

        var isMember = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM project_members WHERE project_id = @ProjectId AND user_id = @UserId)",
            new { ProjectId = projectId, UserId = current.Id },
            tx,
            cancellationToken: ct));
        if (!isMember)
        {
            throw ApiException.OutOfScope();
        }

        return project;
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
