using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class SupplierService(AppDb db, PermissionService permissions, AuditService audit)
{
    private const string SupplierSelect = "SELECT id Id,name Name,remark Remark,status Status,created_at CreatedAt FROM suppliers";
    private const string UserSelect = "SELECT u.id Id,u.employee_no EmployeeNo,u.password_hash PasswordHash,u.real_name RealName,u.email Email,u.user_type UserType,u.supplier_id SupplierId,u.department_id DepartmentId,u.status Status,u.must_change_password MustChangePassword,u.last_login_at LastLoginAt,u.created_at CreatedAt,(SELECT ur.role_id FROM user_roles ur WHERE ur.user_id=u.id LIMIT 1) RoleId,(SELECT r.name FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=u.id LIMIT 1) RoleName FROM users u";
    public async Task<object> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, string? keyword, string? status, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); await using var c = await db.OpenAsync(ct); await RequireSupplierReadAsync(c, null, actor, ct); var where = " WHERE 1=1"; if (!string.IsNullOrWhiteSpace(keyword)) where += " AND name LIKE CONCAT('%',@keyword,'%')"; if (!string.IsNullOrWhiteSpace(status)) where += " AND status=@status"; var p = new { keyword = keyword?.Trim(), status, size, offset }; var total = await c.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM suppliers" + where, p, cancellationToken: ct)); var rows = await c.QueryAsync<SupplierRow>(new CommandDefinition(SupplierSelect + where + " ORDER BY id DESC LIMIT @size OFFSET @offset", p, cancellationToken: ct)); return new { list = rows.Select(Json).ToArray(), total, page, pageSize = size };
    }
    public async Task<object> DetailAsync(CurrentUser actor, ulong id, CancellationToken ct) { AccessService.RequireInternal(actor); await using var c = await db.OpenAsync(ct); await RequireSupplierReadAsync(c, null, actor, ct); return Json(await FindAsync(c, null, id, ct) ?? throw ApiException.NotFound()); }
    public Task<object> CreateAsync(CurrentUser a, SupplierUpsert r, CancellationToken ct) => UpsertAsync(a, null, r, ct);
    public Task<object> UpdateAsync(CurrentUser a, ulong id, SupplierUpsert r, CancellationToken ct) => UpsertAsync(a, id, r, ct);
    private async Task<object> UpsertAsync(CurrentUser actor, ulong? id, SupplierUpsert request, CancellationToken ct)
    {
        Validate(request); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await GateAsync(c, t, actor, "supplier:manage", ct); var existing = id.HasValue ? await FindAsync(c, t, id.Value, ct) ?? throw ApiException.NotFound() : null; if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM suppliers WHERE name=@name AND (@id IS NULL OR id<>@id))", new { name = request.Name.Trim(), id }, t, cancellationToken: ct)) == 1) throw ApiException.Conflict("供应商名称已存在"); ulong resultId;
        if (id is null) { await c.ExecuteAsync(new CommandDefinition("INSERT INTO suppliers(name,remark,status,created_by,created_at,updated_at) VALUES(@name,@remark,'ACTIVE',@actorId,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))", new { name = request.Name.Trim(), request.Remark, actorId = actor.Id }, t, cancellationToken: ct)); resultId = await c.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: t, cancellationToken: ct)); }
        else { resultId = id.Value; await c.ExecuteAsync(new CommandDefinition("UPDATE suppliers SET name=@name,remark=@remark,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { name = request.Name.Trim(), request.Remark, id }, t, cancellationToken: ct)); }
        var updated = await FindAsync(c, t, resultId, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(c, t, actor.Id, id is null ? "SUPPLIER_CREATE" : "SUPPLIER_UPDATE", "supplier", resultId, new
        {
            oldName = existing?.Name,
            newName = updated.Name,
            targetName = updated.Name,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "供应商名称", existing?.Name, updated.Name),
                new AuditChange("remark", "备注", existing?.Remark, updated.Remark),
                new AuditChange("status", "状态", existing?.Status, updated.Status))
        }, null, ct); var result = Json(updated); await t.CommitAsync(ct); return result;
    }
    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var c = await db.OpenAsync(ct);
        await using var t = await AppDb.BeginTransactionAsync(c, ct);
        await AccessService.LockManagementAsync(c, t, ct);
        actor = await AccessService.RecheckActorAsync(c, t, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(c, t, actor, "supplier:manage", ct);
        var row = await c.QuerySingleOrDefaultAsync<SupplierRow>(new CommandDefinition(
            SupplierSelect + " WHERE id=@id FOR UPDATE", new { id }, t, cancellationToken: ct)) ?? throw ApiException.NotFound();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE suppliers SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, t, cancellationToken: ct));
        var revokedSessionCount = 0;
        if (status == "DISABLED")
        {
            var accountIds = (await c.QueryAsync<ulong>(new CommandDefinition(
                "SELECT id FROM users WHERE user_type='SUPPLIER' AND supplier_id=@id ORDER BY id FOR UPDATE",
                new { id }, t, cancellationToken: ct))).ToArray();
            if (accountIds.Length > 0)
                revokedSessionCount = await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE refresh_tokens SET revoked=1 WHERE user_id IN @accountIds AND revoked=0",
                    new { accountIds }, t, cancellationToken: ct));
        }
        await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_STATUS", "supplier", id,
            new
            {
                row.Name,
                oldStatus = row.Status,
                newStatus = status,
                sessionsRevoked = status == "DISABLED",
                revokedSessionCount,
                targetName = row.Name,
                changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", row.Status, status))
            }, null, ct);
        await t.CommitAsync(ct);
        return new { row.Id, row.Name, row.Remark, Status = status, row.CreatedAt };
    }
    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await GateAsync(c, t, actor, "supplier:manage", ct); await AccessService.RequirePermissionAsync(c, t, actor, "supplier:delete", ct); var row = await FindAsync(c, t, id, ct) ?? throw ApiException.NotFound(); if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM projects WHERE supplier_id=@id) OR EXISTS(SELECT 1 FROM project_groups WHERE supplier_id=@id)", new { id }, t, cancellationToken: ct)) == 1) throw ApiException.BadRequest("该供应商仍有关联项目，请先删除项目"); var accounts = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM users WHERE user_type='SUPPLIER' AND supplier_id=@id", new { id }, t, cancellationToken: ct)); if (accounts > 0) throw ApiException.BadRequest($"该供应商仍有 {accounts} 个账号，请先逐个处理账号"); await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_DELETE", "supplier", id, new { row.Name, accountCount = 0, targetName = row.Name, changes = Array.Empty<AuditChange>() }, null, ct); await c.ExecuteAsync(new CommandDefinition("DELETE FROM suppliers WHERE id=@id", new { id }, t, cancellationToken: ct)); await t.CommitAsync(ct);
    }
    public async Task<object> AccountsAsync(CurrentUser actor, ulong supplierId, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); await using var c = await db.OpenAsync(ct); await AccessService.RequirePermissionAsync(c, null, actor, "supplier:account", ct); if (await FindAsync(c, null, supplierId, ct) is null) throw ApiException.NotFound(); var rows = await c.QueryAsync<AdminUserRow>(new CommandDefinition(UserSelect + " WHERE user_type='SUPPLIER' AND supplier_id=@supplierId ORDER BY id", new { supplierId }, cancellationToken: ct)); return rows.Select(AccountJson).ToArray();
    }
    public async Task<object> RoleOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var c = await db.OpenAsync(ct);
        await AccessService.RequirePermissionAsync(c, null, actor, "supplier:account", ct);
        var owned = await permissions.GetCodesAsync(c, null, actor.Id, ct);
        var isAdmin = await AccessService.IsSystemAdminAsync(c, null, actor.Id, ct);
        var rows = await c.QueryAsync<RoleRow>(new CommandDefinition("""
            SELECT r.id Id,r.name Name FROM roles r
            WHERE r.status='ACTIVE'
              AND NOT(r.is_built_in=1 AND r.name='系统管理员')
              AND NOT EXISTS(
                  SELECT 1 FROM role_permissions rp
                  JOIN permissions p ON p.id=rp.permission_id
                  WHERE rp.role_id=r.id AND p.code NOT IN @allowed)
              AND (@isAdmin=1 OR NOT EXISTS(
                  SELECT 1 FROM role_permissions rp
                  JOIN permissions p ON p.id=rp.permission_id
                  WHERE rp.role_id=r.id AND p.code NOT IN @owned))
              AND (@keyword IS NULL OR r.name LIKE CONCAT('%',@keyword,'%'))
            ORDER BY r.id
            """, new
            {
                allowed = RoleService.SupplierPermissionCodes.ToArray(),
                owned = owned.ToArray(),
                isAdmin,
                keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim()
            }, cancellationToken: ct));
        return rows.Select(role => new { role.Id, role.Name }).ToArray();
    }
    public async Task<object> CreateAccountAsync(CurrentUser actor, ulong supplierId, SupplierAccountCreate request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); AdminValidation.EmployeeNo(request.EmployeeNo); ValidateName(request.RealName); AdminValidation.Email(request.Email); PasswordService.Validate(request.Password); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await GateAsync(c, t, actor, "supplier:account", ct); var supplier = await FindAsync(c, t, supplierId, ct) ?? throw ApiException.NotFound(); if (supplier.Status != "ACTIVE") throw ApiException.BadRequest("供应商已被禁用"); if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM users WHERE employee_no=@eno)", new { eno = request.EmployeeNo.Trim() }, t, cancellationToken: ct)) == 1) throw ApiException.Conflict("工号已存在"); var roleId = request.RoleId ?? await FallbackSupplierRoleIdAsync(c, t, ct); var role = await EnsureSupplierRoleAssignableAsync(c, t, actor, roleId, ct); var hash = await PasswordService.HashAsync(request.Password, ct);
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO users(employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,locked_until,last_login_at,last_login_ip,created_by,created_at,updated_at) VALUES(@eno,@hash,@name,@email,'SUPPLIER',@supplierId,NULL,'ACTIVE',1,0,NULL,NULL,NULL,@actorId,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))", new { eno = request.EmployeeNo.Trim(), hash, name = request.RealName.Trim(), email = request.Email.Trim(), supplierId, actorId = actor.Id }, t, cancellationToken: ct)); var id = await c.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: t, cancellationToken: ct)); await c.ExecuteAsync(new CommandDefinition("INSERT INTO user_roles(user_id,role_id) VALUES(@id,@roleId)", new { id, roleId = role.Id }, t, cancellationToken: ct));
        var created = await FindAccountAsync(c, t, id, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_ACCOUNT_CREATE", "user", id, new
        {
            employeeNo = created.EmployeeNo,
            supplierId,
            roleId = role.Id,
            roleName = role.Name,
            supplierName = supplier.Name,
            created.RealName,
            created.Email,
            targetName = AccountAuditName(created),
            changes = AuditChange.OnlyChanged(
                new AuditChange("employeeNo", "工号", null, created.EmployeeNo),
                new AuditChange("realName", "姓名", null, created.RealName),
                new AuditChange("email", "邮箱", null, created.Email),
                new AuditChange("supplierId", "所属供应商", null, new { supplier.Id, supplier.Name }),
                new AuditChange("roleId", "角色", null, new { role.Id, role.Name }),
                new AuditChange("status", "状态", null, created.Status))
        }, null, ct); var result = AccountJson(created); await t.CommitAsync(ct); return result;
    }
    public async Task<object> UpdateAccountAsync(CurrentUser actor, ulong id, SupplierAccountUpdate request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); if (request.RealName is not null) ValidateName(request.RealName); if (request.Email is not null) AdminValidation.Email(request.Email); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await GateAsync(c, t, actor, "supplier:account", ct); var u = await FindAccountAsync(c, t, id, ct) ?? throw ApiException.NotFound(); await c.ExecuteAsync(new CommandDefinition("UPDATE users SET real_name=COALESCE(@name,real_name),email=COALESCE(@email,email),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { name = request.RealName?.Trim(), email = request.Email?.Trim(), id }, t, cancellationToken: ct));
        var updated = await FindAccountAsync(c, t, id, ct) ?? throw ApiException.NotFound();
        var changes = AuditChange.OnlyChanged(
            new AuditChange("realName", "姓名", u.RealName, updated.RealName),
            new AuditChange("email", "邮箱", u.Email, updated.Email));
        await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_ACCOUNT_UPDATE", "user", id, new
        {
            u.EmployeeNo,
            changedFields = changes.Select(change => change.Field).ToArray(),
            targetName = AccountAuditName(updated),
            changes
        }, null, ct); var result = AccountJson(updated); await t.CommitAsync(ct); return result;
    }
    public async Task<object> SetAccountStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); status = AdminValidation.Status(status); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await AccessService.LockManagementAsync(c, t, ct); actor = await AccessService.RecheckActorAsync(c, t, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(c, t, actor, "supplier:account", ct); var u = await c.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(UserSelect + " WHERE u.id=@id FOR UPDATE", new { id }, t, cancellationToken: ct)) ?? throw ApiException.NotFound(); if (u.UserType != "SUPPLIER") throw ApiException.BadRequest("该账号不是供应商人员"); if (status == "ACTIVE") { if (!u.RoleId.HasValue) throw ApiException.BadRequest("供应商账号必须绑定一个角色"); await EnsureSupplierRoleAssignableAsync(c, t, actor, u.RoleId.Value, ct); } await c.ExecuteAsync(new CommandDefinition("UPDATE users SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, t, cancellationToken: ct)); if (status == "DISABLED") await IdentityService.RevokeAllAsync(c, t, id, ct); await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_ACCOUNT_STATUS", "user", id, new
        {
            u.EmployeeNo,
            oldStatus = u.Status,
            newStatus = status,
            sessionsRevoked = status == "DISABLED",
            targetName = AccountAuditName(u),
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", u.Status, status))
        }, null, ct); var result = AccountJson(await FindAccountAsync(c, t, id, ct) ?? throw ApiException.NotFound()); await t.CommitAsync(ct); return result;
    }
    public async Task ResetAccountPasswordAsync(CurrentUser actor, ulong id, string password, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); PasswordService.Validate(password); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await GateAsync(c, t, actor, "supplier:account", ct); var u = await FindAccountAsync(c, t, id, ct) ?? throw ApiException.NotFound(); var hash = await PasswordService.HashAsync(password, ct); await c.ExecuteAsync(new CommandDefinition("UPDATE users SET password_hash=@hash,must_change_password=1,failed_login_attempts=0,locked_until=NULL,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { hash, id }, t, cancellationToken: ct)); await IdentityService.RevokeAllAsync(c, t, id, ct); await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_ACCOUNT_RESET_PASSWORD", "user", id, new
        {
            u.EmployeeNo,
            sessionsRevoked = true,
            mustChangePassword = true,
            passwordChanged = true,
            targetName = AccountAuditName(u),
            changes = AuditChange.OnlyChanged(new AuditChange("passwordChanged", "密码已重置", false, true))
        }, null, ct); await t.CommitAsync(ct);
    }
    public async Task DeleteAccountAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); await using var c = await db.OpenAsync(ct); await using var t = await AppDb.BeginTransactionAsync(c, ct); await AccessService.LockManagementAsync(c, t, ct); actor = await AccessService.RecheckActorAsync(c, t, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(c, t, actor, "supplier:account", ct); await AccessService.RequirePermissionAsync(c, t, actor, "supplier:account_delete", ct); var u = await c.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(UserSelect + " WHERE id=@id FOR UPDATE", new { id }, t, cancellationToken: ct)) ?? throw ApiException.NotFound(); if (u.UserType != "SUPPLIER") throw ApiException.BadRequest("该账号不是供应商人员"); await UserService.EnsureNoHistoryAsync(c, t, id, ct); await audit.WriteAsync(c, t, actor.Id, "SUPPLIER_ACCOUNT_DELETE", "user", id, new { u.EmployeeNo, u.RealName, targetName = AccountAuditName(u), changes = Array.Empty<AuditChange>() }, null, ct); await UserService.RemoveAsync(c, t, id, ct); await t.CommitAsync(ct);
    }
    private static async Task GateAsync(MySqlConnection c, MySqlTransaction t, CurrentUser a, string p, CancellationToken ct) { await AccessService.LockManagementAsync(c, t, ct); a = await AccessService.RecheckActorAsync(c, t, a, ct); AccessService.RequireInternal(a); await AccessService.RequirePermissionAsync(c, t, a, p, ct); }
    private async Task RequireSupplierReadAsync(MySqlConnection c, MySqlTransaction? t, CurrentUser actor, CancellationToken ct)
    {
        var codes = await permissions.GetCodesAsync(c, t, actor.Id, ct);
        if (!codes.Contains("supplier:manage", StringComparer.Ordinal) && !codes.Contains("supplier:account", StringComparer.Ordinal))
            throw ApiException.Forbidden();
    }
    private static async Task<ulong> FallbackSupplierRoleIdAsync(MySqlConnection c, MySqlTransaction t, CancellationToken ct)
    {
        return await c.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition(
                   "SELECT id FROM roles WHERE is_built_in=1 AND name='供应商人员'", transaction: t, cancellationToken: ct))
               ?? throw ApiException.BadRequest("请选择供应商角色；当前没有可用的内置供应商人员角色");
    }
    private async Task<RoleRow> EnsureSupplierRoleAssignableAsync(MySqlConnection c, MySqlTransaction t, CurrentUser actor, ulong roleId, CancellationToken ct)
    {
        if (roleId == 0) throw ApiException.BadRequest("请选择供应商角色");
        var role = await c.QuerySingleOrDefaultAsync<RoleRow>(new CommandDefinition(
            "SELECT id Id,name Name,is_built_in IsBuiltIn,status Status FROM roles WHERE id=@roleId",
            new { roleId }, t, cancellationToken: ct)) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        if (role.IsBuiltIn && role.Name == "系统管理员") throw ApiException.BadRequest("系统管理员角色不能绑定供应商账号");
        if (role.Status != "ACTIVE") throw ApiException.BadRequest("不能绑定已禁用的角色");
        var codes = await c.QueryAsync<string>(new CommandDefinition("""
            SELECT p.code FROM permissions p
            JOIN role_permissions rp ON rp.permission_id=p.id
            WHERE rp.role_id=@roleId
            """, new { roleId }, t, cancellationToken: ct));
        if (!RoleService.IsSupplierAccountPermissionSetAllowed(codes))
            throw ApiException.BadRequest("供应商账号角色只能包含供应商自有项目所需权限");
        await permissions.EnsureManageRoleAsync(c, t, actor, roleId, ct);
        return role;
    }
    private static Task<SupplierRow?> FindAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<SupplierRow>(new CommandDefinition(SupplierSelect + " WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static async Task<AdminUserRow?> FindAccountAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) { var u = await c.QuerySingleOrDefaultAsync<AdminUserRow>(new CommandDefinition(UserSelect + " WHERE u.id=@id", new { id }, t, cancellationToken: ct)); if (u is not null && u.UserType != "SUPPLIER") throw ApiException.BadRequest("该账号不是供应商人员"); return u; }
    private static object Json(SupplierRow s) => new { s.Id, s.Name, s.Remark, s.Status, s.CreatedAt };
    private static object AccountJson(AdminUserRow u) => new { u.Id, u.EmployeeNo, u.RealName, u.Email, u.SupplierId, u.Status, u.LastLoginAt, u.CreatedAt, u.RoleId, u.RoleName };
    private static string AccountAuditName(AdminUserRow user) => $"{user.RealName}（{user.EmployeeNo}）";
    private static void Validate(SupplierUpsert r) { if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.BadRequest("供应商名称不能为空"); if (r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("供应商名称过长"); if (r.Remark?.EnumerateRunes().Count() > 500) throw ApiException.BadRequest("备注过长"); }
    private static void ValidateName(string v) { if (string.IsNullOrWhiteSpace(v) || v.Trim().EnumerateRunes().Count() > 32) throw ApiException.BadRequest("姓名需为 1~32 个字符"); }
}
