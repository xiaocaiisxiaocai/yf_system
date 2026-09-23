using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using ProjectGroupEntity = Yf.Api.Infrastructure.Entities.ProjectGroup;

namespace Yf.Api.Modules.Projects;

internal sealed record ProjectGroupAccess(
    ulong Id,
    ulong SupplierId,
    ulong CreatedBy,
    string Status,
    ulong? ResponsibleUserId);

internal static class ProjectGroupAccessService
{
    internal static async Task<IQueryable<ProjectGroupEntity>> VisibleQueryAsync(
        YfDbContext db,
        CurrentUser current,
        CancellationToken ct)
    {
        if (!await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:list", ct))
            throw ApiException.Forbidden();
        if (!current.IsInternal)
        {
            if (current.SupplierId is null) throw ApiException.OutOfScope();
            var supplierId = current.SupplierId.Value;
            return db.ProjectGroups.Where(group => group.SupplierId == supplierId);
        }
        if (await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:view_all", ct))
            return db.ProjectGroups;
        return db.ProjectGroups.Where(group => group.ResponsibleUserId == current.Id);
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
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate)
        {
            var locked = await db.Database.SqlQuery<LockedGroupAccess>($"""
                SELECT id AS Id,supplier_id AS SupplierId,created_by AS CreatedBy,status AS Status,
                       responsible_user_id AS ResponsibleUserId
                FROM project_groups WHERE id={groupId} FOR UPDATE
                """).SingleOrDefaultAsync(ct);
            if (locked is null) throw ApiException.NotFound();
            return await AuthorizeAsync(db, current,
                new ProjectGroupAccess(locked.Id, locked.SupplierId, locked.CreatedBy, locked.Status, locked.ResponsibleUserId), ct);
        }
        var access = await db.ProjectGroups.Where(group => group.Id == groupId).Select(group => new ProjectGroupAccess(
            group.Id, group.SupplierId, group.CreatedBy, group.Status, group.ResponsibleUserId)).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        return await AuthorizeAsync(db, current, access, ct);
    }

    private static async Task<ProjectGroupAccess> AuthorizeAsync(
        YfDbContext db, CurrentUser current, ProjectGroupAccess access, CancellationToken ct)
    {
        if (!current.IsInternal)
        {
            if (current.SupplierId != access.SupplierId) throw ApiException.OutOfScope();
            if (!await db.Suppliers.AnyAsync(
                    supplier => supplier.Id == access.SupplierId && supplier.Status == AccountStatuses.Active, ct))
                throw ApiException.OutOfScope();
            return access;
        }
        if (await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:view_all", ct)
            || access.ResponsibleUserId == current.Id)
            return access;
        throw ApiException.OutOfScope();
    }

    private sealed class LockedGroupAccess
    {
        public ulong Id { get; init; }
        public ulong SupplierId { get; init; }
        public ulong CreatedBy { get; init; }
        public string Status { get; init; } = "";
        public ulong? ResponsibleUserId { get; init; }
    }
}
