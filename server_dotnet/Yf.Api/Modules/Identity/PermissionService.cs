using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class PermissionService
{
    public async Task<IReadOnlyList<string>> GetCodesAsync(MySqlConnection conn, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT p.code FROM permissions p
            JOIN role_permissions rp ON rp.permission_id=p.id
            JOIN user_roles ur ON ur.role_id=rp.role_id
            JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
            WHERE ur.user_id=@userId ORDER BY p.code
            """;
        return (await conn.QueryAsync<string>(new CommandDefinition(sql, new { userId }, tx, cancellationToken: ct))).AsList();
    }

    public async Task<(IReadOnlyList<string> Permissions, IReadOnlyList<string> Menus)> GetCodesAndMenusAsync(
        MySqlConnection conn, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        var codes = await GetCodesAsync(conn, tx, userId, ct);
        if (codes.Count == 0) return (codes, Array.Empty<string>());
        var menus = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM permissions WHERE type='MENU' AND code IN @codes ORDER BY sort_no,id",
            new { codes }, tx, cancellationToken: ct))).AsList();
        return (codes, menus);
    }

    public async Task EnsureGrantableAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, IReadOnlyCollection<ulong> ids, CancellationToken ct)
    {
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        var distinct = ids.Distinct().ToArray();
        var owned = await GetCodesAsync(conn, tx, actor.Id, ct);
        var requested = distinct.Length == 0 ? [] : (await conn.QueryAsync<(ulong Id, string Code)>(new CommandDefinition(
            "SELECT id Id,code Code FROM permissions WHERE id IN @distinct", new { distinct }, tx, cancellationToken: ct))).AsList();
        if (requested.Count != distinct.Length || requested.Any(x => !owned.Contains(x.Code))) throw ApiException.Forbidden();
    }

    public async Task EnsureManageRoleAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, ulong roleId, CancellationToken ct)
    {
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        var role = await conn.QuerySingleOrDefaultAsync<RoleGuard>(new CommandDefinition(
            "SELECT is_built_in IsBuiltIn,name Name FROM roles WHERE id=@roleId", new { roleId }, tx, cancellationToken: ct));
        if (role is null) throw ApiException.NotFound();
        if (role.IsBuiltIn && role.Name == "系统管理员") throw ApiException.Forbidden();
        var ids = (await conn.QueryAsync<ulong>(new CommandDefinition("SELECT permission_id FROM role_permissions WHERE role_id=@roleId", new { roleId }, tx, cancellationToken: ct))).AsList();
        await EnsureGrantableAsync(conn, tx, actor, ids, ct);
    }

    public async Task EnsureManageUserAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, ulong userId, CancellationToken ct)
    {
        if (await AccessService.IsSystemAdminAsync(conn, tx, actor.Id, ct)) return;
        foreach (var roleId in await conn.QueryAsync<ulong>(new CommandDefinition("SELECT role_id FROM user_roles WHERE user_id=@userId", new { userId }, tx, cancellationToken: ct)))
            await EnsureManageRoleAsync(conn, tx, actor, roleId, ct);
    }
    private sealed class RoleGuard { public bool IsBuiltIn { get; init; } public string Name { get; init; } = ""; }
}
