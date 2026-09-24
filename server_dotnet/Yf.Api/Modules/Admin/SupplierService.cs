using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class SupplierService(IDbContextFactory<YfDbContext> dbFactory, PermissionService permissions, AuditService audit)
{
    public async Task<PageResponse<SupplierResponse>> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, string? keyword, string? status, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await RequireSupplierReadAsync(context, actor, ct);
        var query = context.Suppliers.AsNoTracking();
        var term = keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = QueryValues.ContainsPattern(term);
            query = query.Where(x => EF.Functions.Like(x.Name, pattern, QueryValues.LikeEscape));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = offset > int.MaxValue
            ? []
            : await query.OrderByDescending(x => x.Id).Skip((int)offset).Take(checked((int)size)).ToListAsync(ct);
        return new(rows.Select(Json).ToArray(), total, page, size);
    }

    public async Task<SupplierResponse> DetailAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await RequireSupplierReadAsync(context, actor, ct);
        return Json(await FindAsync(context, id, ct) ?? throw ApiException.NotFound());
    }

    public Task<SupplierResponse> CreateAsync(CurrentUser actor, SupplierUpsert request, CancellationToken ct) => UpsertAsync(actor, null, request, ct);
    public Task<SupplierResponse> UpdateAsync(CurrentUser actor, ulong id, SupplierUpsert request, CancellationToken ct) => UpsertAsync(actor, id, request, ct);

    private async Task<SupplierResponse> UpsertAsync(CurrentUser actor, ulong? id, SupplierUpsert request, CancellationToken ct)
    {
        Validate(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:manage", ct);
        var existing = id.HasValue ? await FindAsync(context, id.Value, ct) ?? throw ApiException.NotFound() : null;
        var name = request.Name.Trim();
        if (await context.Suppliers.AnyAsync(x => x.Name == name && (!id.HasValue || x.Id != id.Value), ct)) throw ApiException.Conflict("供应商名称已存在");
        ulong resultId;
        if (id is null)
        {
            var created = new Supplier { Name = name, Remark = request.Remark, Status = AccountStatuses.Active, CreatedBy = actor.Id };
            context.Suppliers.Add(created); await context.SaveChangesAsync(ct); resultId = created.Id;
        }
        else
        {
            resultId = id.Value;
            await context.Suppliers.Where(x => x.Id == resultId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name).SetProperty(x => x.Remark, request.Remark), ct);
        }
        var updated = await FindAsync(context, resultId, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            id is null ? "SUPPLIER_CREATE" : "SUPPLIER_UPDATE", "supplier", resultId, new
            {
                oldName = existing?.Name, newName = updated.Name, targetName = updated.Name,
                changes = AuditChange.OnlyChanged(new AuditChange("name", "供应商名称", existing?.Name, updated.Name),
                    new AuditChange("remark", "备注", existing?.Remark, updated.Remark),
                    new AuditChange("status", "状态", existing?.Status, updated.Status))
            }, null, ct);
        var result = Json(updated); await transaction.CommitAsync(ct); return result;
    }

    public async Task<SupplierResponse> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:manage", ct);
        var row = await context.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        await context.Suppliers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        var revokedSessionCount = 0;
        if (status == AccountStatuses.Disabled)
        {
            var lockedAccounts = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE user_type='SUPPLIER' AND supplier_id={id} ORDER BY id FOR UPDATE")
                .AsNoTracking().ToListAsync(ct);
            var accountIds = lockedAccounts.Select(x => x.Id).ToArray();
            if (accountIds.Length > 0)
                revokedSessionCount = await context.RefreshTokens
                    .Where(x => Enumerable.Contains(accountIds, x.UserId) && !x.Revoked)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true), ct);
        }
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_STATUS", "supplier", id,
            new
            {
                row.Name, oldStatus = row.Status, newStatus = status, sessionsRevoked = status == AccountStatuses.Disabled, revokedSessionCount,
                targetName = row.Name, changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", row.Status, status))
            }, null, ct);
        await transaction.CommitAsync(ct);
        return new SupplierResponse(row.Id, row.Name, row.Remark, status, row.CreatedAt);
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:manage", ct);
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await AccessService.RequirePermissionAsync(connection, dbTransaction, actor, "supplier:delete", ct);
        var row = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (await context.Projects.AnyAsync(x => x.SupplierId == id, ct) || await context.ProjectGroups.AnyAsync(x => x.SupplierId == id, ct))
            throw ApiException.BadRequest("该供应商仍有关联项目，请先删除项目");
        if (await context.RobotParts.AnyAsync(x => x.SupplierId == id, ct))
            throw ApiException.BadRequest("该供应商仍有关联 Robot 料号，请先处理料号");
        var accounts = await context.Users.CountAsync(x => x.UserType == UserTypes.Supplier && x.SupplierId == id, ct);
        if (accounts > 0) throw ApiException.BadRequest($"该供应商仍有 {accounts} 个账号，请先逐个处理账号");
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "SUPPLIER_DELETE", "supplier", id,
            new { row.Name, accountCount = 0, targetName = row.Name, changes = Array.Empty<AuditChange>() }, null, ct);
        await context.Suppliers.Where(x => x.Id == id).ExecuteDeleteAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task<SupplierAccountResponse[]> AccountsAsync(CurrentUser actor, ulong supplierId, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, "supplier:account", ct);
        if (await FindAsync(context, supplierId, ct) is null) throw ApiException.NotFound();
        var users = await context.Users.AsNoTracking().Where(x => x.UserType == UserTypes.Supplier && x.SupplierId == supplierId).OrderBy(x => x.Id).ToListAsync(ct);
        var results = new List<SupplierAccountResponse>(users.Count);
        results.AddRange((await AccountRowsAsync(context, users, ct)).Select(AccountJson));
        return results.ToArray();
    }

    public async Task<RoleOption[]> RoleOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        await AccessService.RequirePermissionAsync(connection, null, actor, "supplier:account", ct);
        var owned = (await permissions.GetCodesAsync(connection, null, actor.Id, ct))
            .Concat(RoleService.SupplierExclusivePermissionCodes).ToArray();
        var allowed = RoleService.SupplierPermissionCodes.ToArray();
        var isAdmin = await AccessService.IsSystemAdminAsync(connection, null, actor.Id, ct);
        var term = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var query = context.Roles.AsNoTracking().Where(role => role.Status == AccountStatuses.Active
            && !(role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator)
            && !context.RolePermissions.Where(rp => rp.RoleId == role.Id)
                .Join(context.Permissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Code)
                .Any(code => !Enumerable.Contains(allowed, code))
            && (isAdmin || !context.RolePermissions.Where(rp => rp.RoleId == role.Id)
                .Join(context.Permissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Code)
                .Any(code => !Enumerable.Contains(owned, code))));
        if (term is not null)
        {
            var pattern = QueryValues.ContainsPattern(term);
            query = query.Where(role => EF.Functions.Like(role.Name, pattern, QueryValues.LikeEscape));
        }
        return await query.OrderBy(role => role.Id).Select(role => new RoleOption(role.Id, role.Name)).ToArrayAsync(ct);
    }

    public async Task<SupplierAccountResponse> CreateAccountAsync(CurrentUser actor, ulong supplierId, SupplierAccountCreate request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); AdminValidation.EmployeeNo(request.EmployeeNo); ValidateName(request.RealName); AdminValidation.Email(request.Email); PasswordService.Validate(request.Password);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:account", ct);
        var supplier = await FindAsync(context, supplierId, ct) ?? throw ApiException.NotFound();
        if (supplier.Status != AccountStatuses.Active) throw ApiException.BadRequest("供应商已被禁用");
        var employeeNo = request.EmployeeNo.Trim();
        if (await context.Users.AnyAsync(x => x.EmployeeNo == employeeNo, ct)) throw ApiException.Conflict("工号已存在");
        var roleId = request.RoleId ?? await FallbackSupplierRoleIdAsync(context, ct);
        var role = await EnsureSupplierRoleAssignableAsync(context, actor, roleId, ct);
        var user = new User
        {
            EmployeeNo = employeeNo, PasswordHash = await PasswordService.HashAsync(request.Password, ct), RealName = request.RealName.Trim(),
            Email = request.Email.Trim(), UserType = UserTypes.Supplier, SupplierId = supplierId, Status = AccountStatuses.Active,
            MustChangePassword = true, FailedLoginAttempts = 0, CreatedBy = actor.Id
        };
        context.Users.Add(user); await context.SaveChangesAsync(ct);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id }); await context.SaveChangesAsync(ct);
        var created = await FindAccountAsync(context, user.Id, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_ACCOUNT_CREATE", "user", user.Id, new
        {
            employeeNo = created.EmployeeNo, supplierId, roleId = role.Id, roleName = role.Name, supplierName = supplier.Name,
            created.RealName, created.Email, targetName = AccountAuditName(created),
            changes = AuditChange.OnlyChanged(new AuditChange("employeeNo", "工号", null, created.EmployeeNo),
                new AuditChange("realName", "姓名", null, created.RealName), new AuditChange("email", "邮箱", null, created.Email),
                new AuditChange("supplierId", "所属供应商", null, new { supplier.Id, supplier.Name }),
                new AuditChange("roleId", "角色", null, new { role.Id, role.Name }), new AuditChange("status", "状态", null, created.Status))
        }, null, ct);
        var result = AccountJson(created); await transaction.CommitAsync(ct); return result;
    }

    public async Task<SupplierAccountResponse> UpdateAccountAsync(CurrentUser actor, ulong id, SupplierAccountUpdate request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        if (request.RealName is not null) ValidateName(request.RealName); if (request.Email is not null) AdminValidation.Email(request.Email);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:account", ct);
        var user = await FindAccountAsync(context, id, ct) ?? throw ApiException.NotFound();
        var realName = request.RealName?.Trim() ?? user.RealName;
        var email = request.Email?.Trim() ?? user.Email;
        await context.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RealName, realName).SetProperty(x => x.Email, email), ct);
        var updated = await FindAccountAsync(context, id, ct) ?? throw ApiException.NotFound();
        var changes = AuditChange.OnlyChanged(new AuditChange("realName", "姓名", user.RealName, updated.RealName), new AuditChange("email", "邮箱", user.Email, updated.Email));
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_ACCOUNT_UPDATE", "user", id,
            new { user.EmployeeNo, changedFields = changes.Select(x => x.Field).ToArray(), targetName = AccountAuditName(updated), changes }, null, ct);
        var result = AccountJson(updated); await transaction.CommitAsync(ct); return result;
    }

    public async Task<SupplierAccountResponse> SetAccountStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:account", ct);
        var locked = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id={id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        var user = await AccountRowAsync(context, locked, ct);
        if (user.UserType != UserTypes.Supplier) throw ApiException.BadRequest("该账号不是供应商人员");
        if (status == AccountStatuses.Active)
        {
            if (!user.RoleId.HasValue) throw ApiException.BadRequest("供应商账号必须绑定一个角色");
            await EnsureSupplierRoleAssignableAsync(context, actor, user.RoleId.Value, ct);
        }
        await context.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        if (status == AccountStatuses.Disabled) await IdentityService.RevokeAllAsync(context.Database.Connection(), context.Database.RequireTransaction(), id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_ACCOUNT_STATUS", "user", id, new
        {
            user.EmployeeNo, oldStatus = user.Status, newStatus = status, sessionsRevoked = status == AccountStatuses.Disabled, targetName = AccountAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", user.Status, status))
        }, null, ct);
        var result = AccountJson(await FindAccountAsync(context, id, ct) ?? throw ApiException.NotFound()); await transaction.CommitAsync(ct); return result;
    }

    public async Task ResetAccountPasswordAsync(CurrentUser actor, ulong id, string password, CancellationToken ct)
    {
        AccessService.RequireInternal(actor); PasswordService.Validate(password);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:account", ct);
        var user = await FindAccountAsync(context, id, ct) ?? throw ApiException.NotFound(); var hash = await PasswordService.HashAsync(password, ct);
        await context.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PasswordHash, hash)
            .SetProperty(x => x.MustChangePassword, true).SetProperty(x => x.FailedLoginAttempts, 0).SetProperty(x => x.LockedUntil, (DateTime?)null), ct);
        await IdentityService.RevokeAllAsync(context.Database.Connection(), context.Database.RequireTransaction(), id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_ACCOUNT_RESET_PASSWORD", "user", id, new
        {
            user.EmployeeNo, sessionsRevoked = true, mustChangePassword = true, passwordChanged = true, targetName = AccountAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("passwordChanged", "密码已重置", false, true))
        }, null, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteAccountAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "supplier:account", ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "supplier:account_delete", ct);
        var locked = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id={id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        var user = await AccountRowAsync(context, locked, ct);
        if (user.UserType != UserTypes.Supplier) throw ApiException.BadRequest("该账号不是供应商人员");
        await UserService.EnsureNoHistoryAsync(context, id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, "SUPPLIER_ACCOUNT_DELETE", "user", id,
            new { user.EmployeeNo, user.RealName, targetName = AccountAuditName(user), changes = Array.Empty<AuditChange>() }, null, ct);
        await UserService.RemoveAsync(context, id, ct); await transaction.CommitAsync(ct);
    }

    private static async Task GateAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        var connection = context.Database.Connection(); var transaction = context.Database.RequireTransaction();
        await AccessService.LockManagementAsync(connection, transaction, ct); actor = await AccessService.RecheckActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(connection, transaction, actor, permission, ct);
    }

    private async Task RequireSupplierReadAsync(YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        var codes = await permissions.GetCodesAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, ct);
        if (!codes.Contains("supplier:manage", StringComparer.Ordinal) && !codes.Contains("supplier:account", StringComparer.Ordinal)) throw ApiException.Forbidden();
    }

    private static async Task<ulong> FallbackSupplierRoleIdAsync(YfDbContext context, CancellationToken ct) =>
        await context.Roles.AsNoTracking().Where(x => x.IsBuiltIn && x.Name == "供应商人员").Select(x => (ulong?)x.Id).SingleOrDefaultAsync(ct)
        ?? throw ApiException.BadRequest("请选择供应商角色；当前没有可用的内置供应商人员角色");

    private async Task<Role> EnsureSupplierRoleAssignableAsync(YfDbContext context, CurrentUser actor, ulong roleId, CancellationToken ct)
    {
        if (roleId == 0) throw ApiException.BadRequest("请选择供应商角色");
        var role = await context.Roles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        if (role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator) throw ApiException.BadRequest("系统管理员角色不能绑定供应商账号");
        if (role.Status != AccountStatuses.Active) throw ApiException.BadRequest("不能绑定已禁用的角色");
        var codes = await context.RolePermissions.Where(x => x.RoleId == roleId)
            .Join(context.Permissions, x => x.PermissionId, x => x.Id, (_, permission) => permission.Code).ToArrayAsync(ct);
        if (!RoleService.IsSupplierAccountPermissionSetAllowed(codes)) throw ApiException.BadRequest("供应商账号角色只能包含供应商自有项目所需权限");
        await EnsureSupplierRoleWithinCeilingAsync(context, actor, codes, ct);
        return role;
    }

    // Same delegation ceiling as internal roles, except supplier-only codes the manager cannot hold effectively.
    private async Task EnsureSupplierRoleWithinCeilingAsync(YfDbContext context, CurrentUser actor, IEnumerable<string> codes, CancellationToken ct)
    {
        var connection = context.Database.Connection(); var transaction = context.Database.RequireTransaction();
        if (await AccessService.IsSystemAdminAsync(connection, transaction, actor.Id, ct)) return;
        var owned = (await permissions.GetCodesAsync(connection, transaction, actor.Id, ct)).ToHashSet(StringComparer.Ordinal);
        if (codes.Any(code => !RoleService.SupplierExclusivePermissionCodes.Contains(code) && !owned.Contains(code))) throw ApiException.Forbidden();
    }

    private static Task<Supplier?> FindAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    private static async Task<AdminUserRow?> FindAccountAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null) return null;
        if (user.UserType != UserTypes.Supplier) throw ApiException.BadRequest("该账号不是供应商人员");
        return await AccountRowAsync(context, user, ct);
    }

    private static async Task<AdminUserRow> AccountRowAsync(YfDbContext context, User user, CancellationToken ct) =>
        (await AccountRowsAsync(context, [user], ct))[0];

    /// <summary>Supplier accounts carry exactly one role; loads them for every account in one query.</summary>
    private static async Task<List<AdminUserRow>> AccountRowsAsync(YfDbContext context, IReadOnlyList<User> users, CancellationToken ct)
    {
        var userIds = users.Select(user => user.Id).ToArray();
        var roles = (await context.UserRoles.AsNoTracking().Where(x => Enumerable.Contains(userIds, x.UserId))
                .Join(context.Roles.AsNoTracking(), x => x.RoleId, x => x.Id, (userRole, item) => new { userRole.UserId, item.Id, item.Name })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            // SingleOrDefault keeps the one-role invariant loud, as the per-account query did.
            .ToDictionary(group => group.Key, group => group.SingleOrDefault());
        return users.Select(user =>
        {
            var role = roles.GetValueOrDefault(user.Id);
            return new AdminUserRow
            {
                Id = user.Id, EmployeeNo = user.EmployeeNo, PasswordHash = user.PasswordHash, RealName = user.RealName, Email = user.Email,
                UserType = user.UserType, SupplierId = user.SupplierId, DepartmentId = user.DepartmentId, Status = user.Status,
                MustChangePassword = user.MustChangePassword, LastLoginAt = user.LastLoginAt, CreatedAt = user.CreatedAt,
                RoleId = role?.Id, RoleName = role?.Name
            };
        }).ToList();
    }

    private static SupplierResponse Json(Supplier supplier) => new(supplier.Id, supplier.Name, supplier.Remark, supplier.Status, supplier.CreatedAt);
    private static SupplierAccountResponse AccountJson(AdminUserRow user) => new(user.Id, user.EmployeeNo, user.RealName, user.Email, user.SupplierId, user.Status, user.LastLoginAt, user.CreatedAt, user.RoleId, user.RoleName);
    private static string AccountAuditName(AdminUserRow user) => $"{user.RealName}（{user.EmployeeNo}）";
    private static void Validate(SupplierUpsert request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw ApiException.BadRequest("供应商名称不能为空");
        if (request.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("供应商名称过长");
        if (request.Remark?.EnumerateRunes().Count() > 500) throw ApiException.BadRequest("备注过长");
    }
    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().EnumerateRunes().Count() > 32) throw ApiException.BadRequest("姓名需为 1~32 个字符");
    }
}
