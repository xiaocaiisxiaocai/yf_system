using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class PermissionService
{
    public async Task<IReadOnlyList<string>> GetCodesAsync(MySqlConnection conn, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        return await AccessService.PermissionCodesAsync(conn, tx, userId, ct);
    }

    public async Task<(IReadOnlyList<string> Permissions, IReadOnlyList<string> Menus)> GetCodesAndMenusAsync(
        MySqlConnection conn, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        return await AccessService.PermissionCodesAndMenusAsync(conn, tx, userId, ct);
    }

    public async Task EnsureGrantableAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, IReadOnlyCollection<ulong> ids, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        var distinct = ids.Distinct().ToArray();
        var owned = await GetCodesAsync(conn, tx, actor.Id, ct);
        await using var context = EfDb.Use(conn, tx);
        var requested = distinct.Length == 0
            ? Array.Empty<PermissionGuard>()
            : await context.Permissions.Where(permission => Enumerable.Contains(distinct, permission.Id))
                .Select(permission => new PermissionGuard(permission.Id, permission.Code)).ToArrayAsync(ct);
        if (requested.Length != distinct.Length || requested.Any(x => !owned.Contains(x.Code))) throw ApiException.Forbidden();
    }

    public async Task EnsureManageRoleAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, ulong roleId, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        await using var context = EfDb.Use(conn, tx);
        var role = await context.Roles.SingleOrDefaultAsync(role => role.Id == roleId, ct);
        if (role is null) throw ApiException.NotFound();
        if (role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator) throw ApiException.Forbidden();
        var ids = await context.RolePermissions.Where(rolePermission => rolePermission.RoleId == roleId)
            .Select(rolePermission => rolePermission.PermissionId).ToArrayAsync(ct);
        await EnsureGrantableAsync(conn, tx, actor, ids, ct);
    }

    public async Task<IReadOnlySet<ulong>> GetManageableRoleIdsAsync(MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, IReadOnlyCollection<ulong> roleIds, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var distinct = roleIds.Distinct().ToArray();
        if (distinct.Length == 0) return new HashSet<ulong>();
        await using var context = EfDb.Use(conn, tx);
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct))
            return (await context.Roles.Where(role => Enumerable.Contains(distinct, role.Id)).Select(role => role.Id).ToArrayAsync(ct)).ToHashSet();
        var owned = (await GetCodesAsync(conn, tx, actor.Id, ct)).ToArray();
        var manageable = await context.Roles
            .Where(role => Enumerable.Contains(distinct, role.Id)
                && !(role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator)
                && !context.RolePermissions.Where(rolePermission => rolePermission.RoleId == role.Id)
                    .Join(context.Permissions, rolePermission => rolePermission.PermissionId, permission => permission.Id, (_, permission) => permission.Code)
                    .Any(code => !Enumerable.Contains(owned, code)))
            .Select(role => role.Id).ToArrayAsync(ct);
        return manageable.ToHashSet();
    }

    public async Task EnsureManageUserAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, ulong userId, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        await using var context = EfDb.Use(conn, tx);
        var roleIds = await context.UserRoles.Where(userRole => userRole.UserId == userId).Select(userRole => userRole.RoleId).ToArrayAsync(ct);
        foreach (var roleId in roleIds)
            await EnsureManageRoleAsync(conn, tx, actor, roleId, ct);
    }

    private sealed record PermissionGuard(ulong Id, string Code);
}
