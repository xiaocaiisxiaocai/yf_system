using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using ProjectEntity = Yf.Api.Infrastructure.Entities.Project;

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
    internal static async Task<IQueryable<ProjectEntity>> VisibleQueryAsync(
        YfDbContext db,
        CurrentUser current,
        CancellationToken ct)
    {
        if (!await HasPermissionAsync(db, current.Id, "project:list", ct))
            throw ApiException.Forbidden();
        if (!current.IsInternal)
        {
            if (current.SupplierId is null) throw ApiException.OutOfScope();
            var supplierId = current.SupplierId.Value;
            return db.Projects.Where(project => project.SupplierId == supplierId);
        }
        if (await HasPermissionAsync(db, current.Id, "project:view_all", ct)) return db.Projects;
        return db.Projects.Where(project => project.ResponsibleUserId == current.Id);
    }

    public static async Task<ProjectAccess> RequireViewAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
        CancellationToken ct = default)
    {
        if (tx is not null) return await RequireViewCoreAsync(conn, tx, actor, projectId, false, ct);
        await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
        var project = await RequireViewCoreAsync(conn, owned, actor, projectId, false, ct);
        await owned.CommitAsync(ct);
        return project;
    }

    public static async Task<ProjectAccess> RequireFileUploadAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
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
        if (project.Status != ProjectStatuses.InProgress) throw ApiException.Conflict("项目当前不可上传文件");
        return project;
    }

    public static async Task<ProjectAccess> RequireFileDeleteAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
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
        if (project.Status != ProjectStatuses.InProgress) throw ApiException.Conflict("项目当前不可删除文件");
        return project;
    }

    public static async Task<bool> CanDeleteFilesAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, string projectStatus,
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
        await using var db = EfDb.Use(conn, tx);
        return current.IsInternal
            && projectStatus == ProjectStatuses.InProgress
            && await HasPermissionAsync(db, current.Id, "file:delete", ct);
    }

    internal static Task<bool> HasPermissionAsync(
        YfDbContext db, ulong userId, string permission, CancellationToken ct) =>
        (from userRole in db.UserRoles
         join role in db.Roles on userRole.RoleId equals role.Id
         join rolePermission in db.RolePermissions on role.Id equals rolePermission.RoleId
         join permissionRow in db.Permissions on rolePermission.PermissionId equals permissionRow.Id
         where userRole.UserId == userId && role.Status == "ACTIVE" && permissionRow.Code == permission
         select permissionRow.Id).AnyAsync(ct);

    internal static async Task<bool> HasPermissionAsync(
        MySqlConnection conn, MySqlTransaction? tx, ulong userId, string permission, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        return await HasPermissionAsync(db, userId, permission, ct);
    }

    internal static async Task<ProjectAccess> RequireViewCoreAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId, bool forUpdate,
        CancellationToken ct)
    {
        if (tx is null) throw new InvalidOperationException("Project access validation requires a transaction.");
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        return await RequireViewForValidatedActorAsync(conn, tx, current, projectId, forUpdate, ct);
    }

    internal static async Task<ProjectAccess> RequireViewForValidatedActorAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser current, ulong projectId, bool forUpdate,
        CancellationToken ct)
    {
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate)
        {
            var groupId = await db.Projects.Where(item => item.Id == projectId)
                .Select(item => (ulong?)item.ProjectGroupId).SingleOrDefaultAsync(ct);
            if (groupId is null) throw ApiException.NotFound();
            if (await db.Database.SqlQuery<ulong>(
                    $"SELECT id AS Value FROM project_groups WHERE id={groupId.Value} FOR UPDATE")
                    .SingleOrDefaultAsync(ct) == 0)
                throw ApiException.NotFound();
            var locked = await db.Database.SqlQuery<LockedProjectAccess>($"""
                SELECT id AS Id,project_group_id AS ProjectGroupId,supplier_id AS SupplierId,
                       created_by AS CreatedBy,status AS Status,confirm_side AS ConfirmSide,
                       responsible_user_id AS ResponsibleUserId
                FROM projects WHERE id={projectId} FOR UPDATE
                """).SingleOrDefaultAsync(ct);
            if (locked is null) throw ApiException.NotFound();
            return await AuthorizeAsync(db, current, new ProjectAccess(
                locked.Id, locked.ProjectGroupId, locked.SupplierId, locked.CreatedBy,
                locked.Status, locked.ConfirmSide, locked.ResponsibleUserId), ct);
        }
        else
        {
            var row = await db.Projects.Where(item => item.Id == projectId).Select(item => new ProjectAccess(
                item.Id, (ulong?)item.ProjectGroupId, item.SupplierId, item.CreatedBy, item.Status,
                item.ConfirmSide, item.ResponsibleUserId)).SingleOrDefaultAsync(ct);
            if (row is null) throw ApiException.NotFound();
            return await AuthorizeAsync(db, current, row, ct);
        }
    }

    private static async Task<ProjectAccess> AuthorizeAsync(
        YfDbContext db, CurrentUser current, ProjectAccess access, CancellationToken ct)
    {
        if (!current.IsInternal)
        {
            if (current.SupplierId is null || current.SupplierId.Value != access.SupplierId)
                throw ApiException.OutOfScope();
            if (!await db.Suppliers.AnyAsync(
                    supplier => supplier.Id == current.SupplierId.Value && supplier.Status == "ACTIVE", ct))
                throw ApiException.OutOfScope();
            return access;
        }
        if (await HasPermissionAsync(db, current.Id, "project:view_all", ct)
            || access.ResponsibleUserId == current.Id)
            return access;
        throw ApiException.OutOfScope();
    }

    private sealed class LockedProjectAccess
    {
        public ulong Id { get; init; }
        public ulong? ProjectGroupId { get; init; }
        public ulong SupplierId { get; init; }
        public ulong CreatedBy { get; init; }
        public string Status { get; init; } = "";
        public string? ConfirmSide { get; init; }
        public ulong? ResponsibleUserId { get; init; }
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
