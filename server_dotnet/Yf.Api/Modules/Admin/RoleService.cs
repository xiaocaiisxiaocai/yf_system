using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class RoleService(AppDb db, PermissionService ceiling, AuditService audit)
{
    public async Task<object> PermissionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var all = await conn.QueryAsync<PermissionRow>(new CommandDefinition("SELECT id Id,code Code,name Name,type Type,parent_id ParentId,sort_no SortNo FROM permissions ORDER BY sort_no,id", cancellationToken: ct));
        return all.Select(x => new { x.Id, x.Code, x.Name, x.Type, x.ParentId, x.SortNo }).ToArray();
    }

    public async Task<object> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct); await AccessService.RequirePermissionAsync(conn, null, actor, "role:manage", ct); var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM roles", cancellationToken: ct));
        var rows = await conn.QueryAsync<RoleRow>(new CommandDefinition("SELECT id Id,name Name,description Description,is_built_in IsBuiltIn,status Status,created_at CreatedAt FROM roles ORDER BY id LIMIT @size OFFSET @offset", new { size, offset }, cancellationToken: ct));
        var list = new List<object>(); foreach (var r in rows) list.Add(await JsonAsync(conn, null, r, ct)); return new { list, total, page, pageSize = size };
    }

    public async Task<object> CreateAsync(CurrentUser actor, RoleUpsert request, CancellationToken ct)
    {
        Validate(request); await using var conn = await db.OpenAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct); await GateAsync(conn, tx, actor, "role:manage", ct); await UniqueAsync(conn, tx, request.Name.Trim(), null, ct);
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO roles(name,description,is_built_in,status,created_at,updated_at) VALUES(@name,@description,0,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))", new { name = request.Name.Trim(), request.Description }, tx, cancellationToken: ct)); var id = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, actor.Id, "ROLE_CREATE", "role", id, new { name = request.Name.Trim(), status = "ACTIVE" }, null, ct); await tx.CommitAsync(ct); return await JsonAsync(conn, null, (await FindAsync(conn, null, id, ct))!, ct);
    }
    public async Task<object> UpdateAsync(CurrentUser actor, ulong id, RoleUpsert request, CancellationToken ct)
    {
        Validate(request); await using var conn = await db.OpenAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct); await GateAsync(conn, tx, actor, "role:manage", ct); await ceiling.EnsureManageRoleAsync(conn, tx, actor, id, ct); var role = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); if (role.IsBuiltIn && request.Name.Trim() != role.Name) throw ApiException.BadRequest("内置角色名称不可修改"); await UniqueAsync(conn, tx, request.Name.Trim(), id, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE roles SET name=@name,description=@description,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { name = request.Name.Trim(), request.Description, id }, tx, cancellationToken: ct)); await audit.WriteAsync(conn, tx, actor.Id, "ROLE_UPDATE", "role", id, new { oldName = role.Name, newName = request.Name.Trim(), descriptionChanged = role.Description != request.Description }, null, ct); await tx.CommitAsync(ct); return await JsonAsync(conn, null, (await FindAsync(conn, null, id, ct))!, ct);
    }
    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status); await using var conn = await db.OpenAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct); await GateAsync(conn, tx, actor, "role:manage", ct); await ceiling.EnsureManageRoleAsync(conn, tx, actor, id, ct); var role = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        if (status == "DISABLED") { var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE ur.role_id=@id AND u.user_type='INTERNAL' AND u.status='ACTIVE'", new { id }, tx, cancellationToken: ct)); if (count > 0) throw ApiException.BadRequest($"该角色仍绑定 {count} 个启用用户，请先为这些用户更换角色或禁用账号"); }
        await conn.ExecuteAsync(new CommandDefinition("UPDATE roles SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, tx, cancellationToken: ct)); await audit.WriteAsync(conn, tx, actor.Id, "ROLE_STATUS", "role", id, new { role.Name, oldStatus = role.Status, newStatus = status }, null, ct); await tx.CommitAsync(ct); return await JsonAsync(conn, null, (await FindAsync(conn, null, id, ct))!, ct);
    }
    public async Task AssignPermissionsAsync(CurrentUser actor, ulong id, IReadOnlyList<ulong> requested, CancellationToken ct)
    {
        if (requested is null) throw ApiException.BadRequest("权限点不能为空"); if (requested.Count > 500) throw ApiException.BadRequest("权限点数量超过上限"); var ids = requested.Distinct().ToArray(); await using var conn = await db.OpenAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct); await GateAsync(conn, tx, actor, "role:manage", ct); await ceiling.EnsureManageRoleAsync(conn, tx, actor, id, ct); var role = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); await ceiling.EnsureGrantableAsync(conn, tx, actor, ids, ct);
        var valid = ids.Length == 0 ? Array.Empty<ulong>() : (await conn.QueryAsync<ulong>(new CommandDefinition("SELECT id FROM permissions WHERE id IN @ids", new { ids }, tx, cancellationToken: ct))).ToArray(); if (valid.Length != ids.Length) throw ApiException.BadRequest("权限点不存在");
        if (role.IsBuiltIn && role.Name == "系统管理员" && await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE ur.role_id=@id AND u.user_type='INTERNAL' AND u.status='ACTIVE'", new { id }, tx, cancellationToken: ct)) > 0)
        {
            var required = (await conn.QueryAsync<PermissionRow>(new CommandDefinition("SELECT id Id,code Code FROM permissions WHERE code IN ('rbac:role','role:manage','org:user','user:manage')", transaction: tx, cancellationToken: ct))).ToArray(); if (required.Length != 4 || required.Any(x => !ids.Contains(x.Id))) throw ApiException.BadRequest("系统管理员角色绑定启用用户时，必须保留用户管理和角色管理权限");
        }
        var old = (await conn.QueryAsync<ulong>(new CommandDefinition("SELECT permission_id FROM role_permissions WHERE role_id=@id", new { id }, tx, cancellationToken: ct))).ToArray(); await conn.ExecuteAsync(new CommandDefinition("DELETE FROM role_permissions WHERE role_id=@id", new { id }, tx, cancellationToken: ct)); foreach (var permissionId in ids) await conn.ExecuteAsync(new CommandDefinition("INSERT INTO role_permissions(role_id,permission_id) VALUES(@id,@permissionId)", new { id, permissionId }, tx, cancellationToken: ct)); await audit.WriteAsync(conn, tx, actor.Id, "ROLE_ASSIGN_PERMS", "role", id, new { oldPermissionCount = old.Length, newPermissionCount = ids.Length, oldPermissionIds = old, newPermissionIds = ids }, null, ct); await tx.CommitAsync(ct);
    }
    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct); await GateAsync(conn, tx, actor, "role:manage", ct); await AccessService.RequirePermissionAsync(conn, tx, actor, "role:delete", ct); await ceiling.EnsureManageRoleAsync(conn, tx, actor, id, ct); var role = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); var assigned = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM user_roles WHERE role_id=@id", new { id }, tx, cancellationToken: ct)); if (assigned > 0) throw ApiException.BadRequest($"该角色仍绑定 {assigned} 个用户，请先为这些用户更换角色"); await conn.ExecuteAsync(new CommandDefinition("DELETE FROM role_permissions WHERE role_id=@id; DELETE FROM roles WHERE id=@id", new { id }, tx, cancellationToken: ct)); await audit.WriteAsync(conn, tx, actor.Id, "ROLE_DELETE", "role", id, new { role.Name }, null, ct); await tx.CommitAsync(ct);
    }
    private static async Task GateAsync(MySqlConnection c, MySqlTransaction t, CurrentUser a, string p, CancellationToken ct) { await AccessService.LockManagementAsync(c, t, ct); a = await AccessService.RecheckActorAsync(c, t, a, ct); await AccessService.RequirePermissionAsync(c, t, a, p, ct); }
    private static void Validate(RoleUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("角色名称需为 1~64 个字符"); if (r.Description?.EnumerateRunes().Count() > 255) throw ApiException.BadRequest("角色说明不能超过 255 个字符"); }
    private static async Task UniqueAsync(MySqlConnection c, MySqlTransaction t, string name, ulong? id, CancellationToken ct) { if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM roles WHERE name=@name AND (@id IS NULL OR id<>@id))", new { name, id }, t, cancellationToken: ct)) == 1) throw ApiException.Conflict("角色名称已存在"); }
    private static Task<RoleRow?> FindAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition("SELECT id Id,name Name,description Description,is_built_in IsBuiltIn,status Status,created_at CreatedAt FROM roles WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static async Task<object> JsonAsync(MySqlConnection c, MySqlTransaction? t, RoleRow r, CancellationToken ct) { var ids = (await c.QueryAsync<ulong>(new CommandDefinition("SELECT permission_id FROM role_permissions WHERE role_id=@id", new { id = r.Id }, t, cancellationToken: ct))).ToArray(); var assigned = await c.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM user_roles WHERE role_id=@id", new { id = r.Id }, t, cancellationToken: ct)); return new { r.Id, r.Name, r.Description, r.IsBuiltIn, r.Status, permissionIds = ids, assignedUserCount = assigned, r.CreatedAt }; }
    private sealed class PermissionRow { public ulong Id { get; init; } public string Code { get; init; } = ""; public string Name { get; init; } = ""; public string Type { get; init; } = ""; public ulong? ParentId { get; init; } public int SortNo { get; init; } }
}
