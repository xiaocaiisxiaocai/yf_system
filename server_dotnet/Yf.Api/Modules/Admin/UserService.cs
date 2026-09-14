using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class UserService(AppDb db, PermissionService permissionCeiling, AuditService audit)
{
    private const string Select = "SELECT id Id,employee_no EmployeeNo,password_hash PasswordHash,real_name RealName,email Email,user_type UserType,supplier_id SupplierId,department_id DepartmentId,status Status,must_change_password MustChangePassword,last_login_at LastLoginAt,created_at CreatedAt FROM users";

    public async Task<object> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, string? keyword, ulong? departmentId, string? status, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, null, actor, "user:manage", ct);
        var where = " WHERE u.user_type='INTERNAL'";
        if (!string.IsNullOrWhiteSpace(keyword)) where += " AND (u.employee_no LIKE CONCAT('%',@keyword,'%') OR u.real_name LIKE CONCAT('%',@keyword,'%') OR u.email LIKE CONCAT('%',@keyword,'%'))";
        if (departmentId.HasValue) where += " AND u.department_id=@departmentId";
        if (!string.IsNullOrWhiteSpace(status)) where += " AND u.status=@status";
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM users u" + where, new { keyword = keyword?.Trim(), departmentId, status }, cancellationToken: ct));
        var rows = (await conn.QueryAsync<AdminUserRow>(new CommandDefinition(
            "SELECT u.id Id,u.employee_no EmployeeNo,u.password_hash PasswordHash,u.real_name RealName,u.email Email,u.user_type UserType,u.supplier_id SupplierId,u.department_id DepartmentId,u.status Status,u.must_change_password MustChangePassword,u.last_login_at LastLoginAt,u.created_at CreatedAt FROM users u" + where + " ORDER BY u.id DESC LIMIT @size OFFSET @offset",
            new { keyword = keyword?.Trim(), departmentId, status, size, offset }, cancellationToken: ct))).AsList();
        var list = new List<object>(); foreach (var row in rows) list.Add(await JsonAsync(conn, null, row, ct));
        return new { list, total, page, pageSize = size };
    }

    public async Task<object> RoleOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, null, actor, "user:manage", ct);
        var owned = await permissionCeiling.GetCodesAsync(conn, null, actor.Id, ct);
        var isAdmin = await AccessService.IsSystemAdminAsync(conn, null, actor.Id, ct);
        var rows = await conn.QueryAsync<RoleRow>(new CommandDefinition("""
            SELECT r.id Id,r.name Name FROM roles r
            WHERE r.status='ACTIVE'
              AND NOT(r.is_built_in=1 AND r.name='供应商人员')
              AND (@isAdmin=1 OR (
                    NOT(r.is_built_in=1 AND r.name='系统管理员')
                    AND NOT EXISTS(
                        SELECT 1 FROM role_permissions rp
                        JOIN permissions p ON p.id=rp.permission_id
                        WHERE rp.role_id=r.id AND p.code NOT IN @owned)))
              AND (@keyword IS NULL OR r.name LIKE CONCAT('%',@keyword,'%'))
            ORDER BY r.id
            """, new
            {
                isAdmin,
                owned = owned.ToArray(),
                keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim()
            }, cancellationToken: ct));
        return rows.Select(x => new { x.Id, x.Name }).ToArray();
    }

    public async Task<object> CreateAsync(CurrentUser actor, UserCreate request, CancellationToken ct)
    {
        AdminValidation.EmployeeNo(request.EmployeeNo); ValidateName(request.RealName); AdminValidation.Email(request.Email); PasswordService.Validate(request.Password);
        var roleId = AdminValidation.OneRole(request.RoleId, request.RoleIds, true);
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await ManagementAsync(conn, tx, actor, "user:manage", ct); await permissionCeiling.EnsureManageRoleAsync(conn, tx, actor, roleId, ct);
        if (request.DepartmentId is ulong dept) await DepartmentService.EnsureActiveAsync(conn, tx, dept, ct);
        await EnsureRoleAssignableAsync(conn, tx, roleId, ct);
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM users WHERE employee_no=@employeeNo)", new { employeeNo = request.EmployeeNo.Trim() }, tx, cancellationToken: ct)) == 1) throw ApiException.Conflict("工号已存在");
        var hash = await PasswordService.HashAsync(request.Password, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,locked_until,last_login_at,last_login_ip,created_by,created_at,updated_at)
            VALUES(@employeeNo,@hash,@realName,@email,'INTERNAL',NULL,@departmentId,'ACTIVE',1,0,NULL,NULL,NULL,@actorId,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """, new { employeeNo = request.EmployeeNo.Trim(), hash, realName = request.RealName.Trim(), email = request.Email.Trim(), departmentId = request.DepartmentId, actorId = actor.Id }, tx, cancellationToken: ct));
        var id = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO user_roles(user_id,role_id) VALUES(@id,@roleId)", new { id, roleId }, tx, cancellationToken: ct));
        var created = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        var role = await FindRoleAsync(conn, tx, roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        var department = created.DepartmentId is ulong createdDepartmentId ? await FindDepartmentAsync(conn, tx, createdDepartmentId, ct) : null;
        await audit.WriteAsync(conn, tx, actor.Id, "USER_CREATE", "user", id, new
        {
            employeeNo = created.EmployeeNo,
            request.DepartmentId,
            roleId,
            created.RealName,
            created.Email,
            departmentName = department?.Name,
            roleName = role.Name,
            targetName = UserAuditName(created),
            changes = AuditChange.OnlyChanged(
                new AuditChange("employeeNo", "工号", null, created.EmployeeNo),
                new AuditChange("realName", "姓名", null, created.RealName),
                new AuditChange("email", "邮箱", null, created.Email),
                new AuditChange("departmentId", "所属组织", null, DepartmentAuditJson(department)),
                new AuditChange("roleId", "角色", null, RoleAuditJson(role)),
                new AuditChange("status", "状态", null, created.Status))
        }, null, ct);
        var result = await JsonAsync(conn, tx, created, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<object> UpdateAsync(CurrentUser actor, ulong id, UserUpdate request, CancellationToken ct)
    {
        if (request.RealName is not null) ValidateName(request.RealName); if (request.Email is not null) AdminValidation.Email(request.Email);
        var departmentSpecified = request.DepartmentId.ValueKind != System.Text.Json.JsonValueKind.Undefined;
        ulong? departmentId = request.DepartmentId.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Number when request.DepartmentId.TryGetUInt64(out var parsed) => parsed,
            _ => throw ApiException.BadRequest("组织编号格式不正确")
        };
        var roleId = AdminValidation.OneRole(request.RoleId, request.RoleIds, false);
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct); await ManagementAsync(conn, tx, actor, "user:manage", ct);
        var user = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); if (user.UserType != "INTERNAL") throw ApiException.BadRequest("供应商人员请在供应商模块维护");
        await permissionCeiling.EnsureManageUserAsync(conn, tx, actor, id, ct);
        if (departmentId is ulong dept) await DepartmentService.EnsureActiveAsync(conn, tx, dept, ct);
        var oldRoles = (await conn.QueryAsync<ulong>(new CommandDefinition("SELECT DISTINCT role_id FROM user_roles WHERE user_id=@id", new { id }, tx, cancellationToken: ct))).ToArray();
        if (oldRoles.Length != 1) throw ApiException.BadRequest("启用的内部用户必须且只能绑定一个角色");
        var oldRoleId = oldRoles[0];
        var oldRole = await FindRoleAsync(conn, tx, oldRoleId, ct);
        var oldDepartment = user.DepartmentId is ulong oldDepartmentId ? await FindDepartmentAsync(conn, tx, oldDepartmentId, ct) : null;
        var roleChanged = roleId != 0 && roleId != oldRoleId;
        if (roleChanged)
        {
            await EnsureAdminRemovalSafeAsync(conn, tx, actor.Id, id, roleId, ct); await permissionCeiling.EnsureManageRoleAsync(conn, tx, actor, roleId, ct); await EnsureRoleAssignableAsync(conn, tx, roleId, ct);
        }
        else if (user.Status == "ACTIVE") await EnsureRoleAssignableAsync(conn, tx, oldRoles[0], ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET real_name=COALESCE(@realName,real_name),email=COALESCE(@email,email),department_id=IF(@departmentSpecified,@departmentId,department_id),updated_at=UTC_TIMESTAMP(6) WHERE id=@id",
            new { realName = request.RealName?.Trim(), email = request.Email?.Trim(), departmentSpecified, departmentId, id }, tx, cancellationToken: ct));
        if (roleChanged) { await conn.ExecuteAsync(new CommandDefinition("DELETE FROM user_roles WHERE user_id=@id; INSERT INTO user_roles(user_id,role_id) VALUES(@id,@roleId)", new { id, roleId }, tx, cancellationToken: ct)); }
        var updated = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        var newRoleId = roleChanged ? roleId : oldRoleId;
        var newRole = await FindRoleAsync(conn, tx, newRoleId, ct);
        var newDepartment = updated.DepartmentId is ulong newDepartmentId ? await FindDepartmentAsync(conn, tx, newDepartmentId, ct) : null;
        var changes = AuditChange.OnlyChanged(
            new AuditChange("realName", "姓名", user.RealName, updated.RealName),
            new AuditChange("email", "邮箱", user.Email, updated.Email),
            new AuditChange("departmentId", "所属组织", DepartmentAuditJson(oldDepartment), DepartmentAuditJson(newDepartment)),
            new AuditChange("roleId", "角色", RoleAuditJson(oldRole), RoleAuditJson(newRole)));
        var changedFields = changes.Select(change => change.Field).ToArray();
        await audit.WriteAsync(conn, tx, actor.Id, "USER_UPDATE", "user", id, new
        {
            updated.EmployeeNo,
            changedFields,
            oldDepartmentId = user.DepartmentId,
            newDepartmentId = updated.DepartmentId,
            oldRoleId,
            newRoleId,
            oldDepartmentName = oldDepartment?.Name,
            newDepartmentName = newDepartment?.Name,
            oldRoleName = oldRole?.Name,
            newRoleName = newRole?.Name,
            targetName = UserAuditName(updated),
            changes
        }, null, ct);
        var result = await JsonAsync(conn, tx, updated, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status); await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct); await ManagementAsync(conn, tx, actor, "user:manage", ct);
        if (id == actor.Id) throw ApiException.BadRequest("不能禁用自己的账号");
        var user = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); if (user.UserType != "INTERNAL") throw ApiException.BadRequest("供应商人员请在供应商模块维护"); await permissionCeiling.EnsureManageUserAsync(conn, tx, actor, id, ct);
        if (status == "DISABLED") await EnsureAdminRemovalSafeAsync(conn, tx, actor.Id, id, null, ct); else { var roleIds = (await conn.QueryAsync<ulong>(new CommandDefinition("SELECT DISTINCT role_id FROM user_roles WHERE user_id=@id", new { id }, tx, cancellationToken: ct))).ToArray(); if (roleIds.Length != 1) throw ApiException.BadRequest("启用的内部用户必须且只能绑定一个角色"); await EnsureRoleAssignableAsync(conn, tx, roleIds[0], ct); }
        await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, tx, cancellationToken: ct)); if (status == "DISABLED") await IdentityService.RevokeAllAsync(conn, tx, id, ct);
        await audit.WriteAsync(conn, tx, actor.Id, "USER_STATUS", "user", id, new
        {
            user.EmployeeNo,
            oldStatus = user.Status,
            newStatus = status,
            targetName = UserAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", user.Status, status))
        }, null, ct);
        var result = await JsonAsync(conn, tx, await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(), ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task ResetPasswordAsync(CurrentUser actor, ulong id, string password, CancellationToken ct)
    {
        PasswordService.Validate(password); await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct); await ManagementAsync(conn, tx, actor, "user:manage", ct);
        var user = await conn.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(Select + " WHERE id=@id FOR UPDATE", new { id }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound(); if (user.UserType != "INTERNAL") throw ApiException.BadRequest("供应商人员请在供应商模块维护"); await permissionCeiling.EnsureManageUserAsync(conn, tx, actor, id, ct);
        var hash = await PasswordService.HashAsync(password, ct); await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET password_hash=@hash,must_change_password=1,failed_login_attempts=0,locked_until=NULL,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { hash, id }, tx, cancellationToken: ct)); await IdentityService.RevokeAllAsync(conn, tx, id, ct);
        await audit.WriteAsync(conn, tx, actor.Id, "USER_RESET_PASSWORD", "user", id, new
        {
            user.EmployeeNo,
            sessionsRevoked = true,
            mustChangePassword = true,
            passwordChanged = true,
            targetName = UserAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("passwordChanged", "密码已重置", false, true))
        }, null, ct); await tx.CommitAsync(ct);
    }

    public async Task AssignRoleAsync(CurrentUser actor, ulong id, IReadOnlyList<ulong> roles, CancellationToken ct)
    {
        var roleId = AdminValidation.OneRole(null, roles, true); await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct); await ManagementAsync(conn, tx, actor, "user:manage", ct);
        var user = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound(); if (user.UserType != "INTERNAL") throw ApiException.BadRequest("供应商人员角色固定，不可调整"); await permissionCeiling.EnsureManageUserAsync(conn, tx, actor, id, ct); await permissionCeiling.EnsureManageRoleAsync(conn, tx, actor, roleId, ct); await EnsureAdminRemovalSafeAsync(conn, tx, actor.Id, id, roleId, ct); await EnsureRoleAssignableAsync(conn, tx, roleId, ct);
        var old = await conn.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition("SELECT role_id FROM user_roles WHERE user_id=@id", new { id }, tx, cancellationToken: ct));
        var oldRole = old.HasValue ? await FindRoleAsync(conn, tx, old.Value, ct) : null;
        var newRole = await FindRoleAsync(conn, tx, roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM user_roles WHERE user_id=@id; INSERT INTO user_roles(user_id,role_id) VALUES(@id,@roleId)", new { id, roleId }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, actor.Id, "USER_ASSIGN_ROLE", "user", id, new
        {
            user.EmployeeNo,
            oldRoleId = old,
            newRoleId = roleId,
            oldRoleName = oldRole?.Name,
            newRoleName = newRole.Name,
            targetName = UserAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("roleId", "角色", RoleAuditJson(oldRole), RoleAuditJson(newRole)))
        }, null, ct); await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct); await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "user:manage", ct); await AccessService.RequirePermissionAsync(conn, tx, actor, "user:delete", ct); var user = await conn.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(Select + " WHERE id=@id FOR UPDATE", new { id }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        if (id == actor.Id) throw ApiException.BadRequest("不能删除自己的账号"); if (user.UserType != "INTERNAL") throw ApiException.BadRequest("供应商人员请在供应商模块删除"); if (user.EmployeeNo == "admin") throw ApiException.BadRequest("系统管理员账号不可删除"); await permissionCeiling.EnsureManageUserAsync(conn, tx, actor, id, ct); await EnsureAdminRemovalSafeAsync(conn, tx, actor.Id, id, null, ct); await EnsureNoHistoryAsync(conn, tx, id, ct);
        await audit.WriteAsync(conn, tx, actor.Id, "USER_DELETE", "user", id, new { user.EmployeeNo, user.RealName, targetName = UserAuditName(user), changes = Array.Empty<AuditChange>() }, null, ct); await RemoveAsync(conn, tx, id, ct); await tx.CommitAsync(ct);
    }

    internal static async Task EnsureNoHistoryAsync(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct)
    {
        var sql = """SELECT EXISTS(SELECT 1 FROM files WHERE uploader_id=@id) OR EXISTS(SELECT 1 FROM messages WHERE sender_id=@id OR deleted_by=@id) OR EXISTS(SELECT 1 FROM upload_sessions WHERE uploader_id=@id) OR EXISTS(SELECT 1 FROM projects WHERE created_by=@id) OR EXISTS(SELECT 1 FROM project_status_logs WHERE operator_id=@id) OR EXISTS(SELECT 1 FROM project_members WHERE created_by=@id) OR EXISTS(SELECT 1 FROM message_reads WHERE user_id=@id) OR EXISTS(SELECT 1 FROM collaboration_reads WHERE user_id=@id) OR EXISTS(SELECT 1 FROM suppliers WHERE created_by=@id) OR EXISTS(SELECT 1 FROM users WHERE created_by=@id) OR EXISTS(SELECT 1 FROM email_outbox WHERE recipient_user_id=@id) OR EXISTS(SELECT 1 FROM audit_logs WHERE user_id=@id)""";
        if (await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { id }, t, cancellationToken: ct)) == 1) throw ApiException.BadRequest("该账号仍有业务或历史记录，请禁用账号，不要删除");
    }
    internal static Task RemoveAsync(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct) => c.ExecuteAsync(new CommandDefinition("DELETE FROM user_roles WHERE user_id=@id; DELETE FROM refresh_tokens WHERE user_id=@id; DELETE FROM project_members WHERE user_id=@id; DELETE FROM users WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static async Task ManagementAsync(MySqlConnection c, MySqlTransaction t, CurrentUser actor, string permission, CancellationToken ct) { await AccessService.LockManagementAsync(c, t, ct); actor = await AccessService.RecheckActorAsync(c, t, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(c, t, actor, permission, ct); }
    private static Task<AdminUserRow?> FindAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(Select + " WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static Task<RoleRow?> FindRoleAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition("SELECT id Id,name Name FROM roles WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static Task<DeptRow?> FindDepartmentAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<DeptRow>(new CommandDefinition("SELECT id Id,name Name FROM departments WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static object? RoleAuditJson(RoleRow? role) => role is null ? null : new { role.Id, role.Name };
    private static object? DepartmentAuditJson(DeptRow? department) => department is null ? null : new { department.Id, department.Name };
    private static string UserAuditName(AdminUserRow user) => $"{user.RealName}（{user.EmployeeNo}）";
    private static async Task EnsureRoleAssignableAsync(MySqlConnection c, MySqlTransaction t, ulong roleId, CancellationToken ct) { var role = await c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition("SELECT id Id,name Name,is_built_in IsBuiltIn,status Status FROM roles WHERE id=@roleId", new { roleId }, t, cancellationToken: ct)) ?? throw ApiException.BadRequest($"角色不存在: {roleId}"); if (role.Status != "ACTIVE") throw ApiException.BadRequest("不能绑定已禁用的角色"); if (role.IsBuiltIn && role.Name == "供应商人员") throw ApiException.BadRequest("供应商角色只能由供应商账号使用"); }
    private static async Task EnsureAdminRemovalSafeAsync(MySqlConnection c, MySqlTransaction t, ulong actorId, ulong targetId, ulong? newRoleId, CancellationToken ct) { var adminRole = await c.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition("SELECT id FROM roles WHERE is_built_in=1 AND name='系统管理员'", transaction: t, cancellationToken: ct)); if (!adminRole.HasValue || newRoleId == adminRole) return; var targetStatus = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT u.status FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE u.id=@targetId AND ur.role_id=@adminRole", new { targetId, adminRole }, t, cancellationToken: ct)); if (targetStatus is null) return; if (actorId == targetId) throw ApiException.BadRequest("不能移除自己的系统管理员角色"); var active = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE ur.role_id=@adminRole AND u.status='ACTIVE'", new { adminRole }, t, cancellationToken: ct)); if (RequiresLastActiveAdminProtection(targetStatus, active)) throw ApiException.BadRequest("不能移除系统中最后一个启用管理员"); }
    internal static bool RequiresLastActiveAdminProtection(string targetStatus, int activeAdminCount) => targetStatus == "ACTIVE" && activeAdminCount <= 1;
    private static async Task<object> JsonAsync(MySqlConnection c, MySqlTransaction? t, AdminUserRow u, CancellationToken ct) { var roles = (await c.QueryAsync<(ulong Id,string Name)>(new CommandDefinition("SELECT r.id Id,r.name Name FROM roles r JOIN user_roles ur ON ur.role_id=r.id WHERE ur.user_id=@id ORDER BY r.id", new { id = u.Id }, t, cancellationToken: ct))).AsList(); var departmentName = u.DepartmentId is ulong d ? await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT name FROM departments WHERE id=@d", new { d }, t, cancellationToken: ct)) : null; return new { u.Id, u.EmployeeNo, u.RealName, u.Email, u.UserType, u.SupplierId, u.DepartmentId, departmentName, u.Status, u.MustChangePassword, u.LastLoginAt, u.CreatedAt, roleId = roles.FirstOrDefault().Id == 0 ? (ulong?)null : roles[0].Id, roleName = roles.FirstOrDefault().Name, roleIds = roles.Select(x=>x.Id).ToArray(), roleNames = roles.Select(x=>x.Name).ToArray() }; }
    private static void ValidateName(string value) { if (string.IsNullOrWhiteSpace(value) || value.Trim().EnumerateRunes().Count() > 32) throw ApiException.BadRequest("姓名需为 1~32 个字符"); }
}
