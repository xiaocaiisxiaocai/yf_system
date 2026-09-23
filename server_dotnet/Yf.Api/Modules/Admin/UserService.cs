using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class UserService(IDbContextFactory<YfDbContext> dbFactory, PermissionService permissionCeiling, AuditService audit)
{
    public async Task<PageResponse<UserResponse>> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, string? keyword, ulong? departmentId, string? status, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, "user:manage", ct);
        var query = context.Users.AsNoTracking().Where(x => x.UserType == UserTypes.Internal);
        var term = keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = QueryValues.ContainsPattern(term);
            query = query.Where(x => EF.Functions.Like(x.EmployeeNo, pattern, QueryValues.LikeEscape) || EF.Functions.Like(x.RealName, pattern, QueryValues.LikeEscape) || EF.Functions.Like(x.Email, pattern, QueryValues.LikeEscape));
        }
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = offset > int.MaxValue
            ? []
            : await query.OrderByDescending(x => x.Id).Skip((int)offset).Take(checked((int)size)).ToListAsync(ct);
        var list = await ToResponsesAsync(context, rows, ct);
        return new(list, total, page, size);
    }

    public async Task<RoleOption[]> RoleOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        await AccessService.RequirePermissionAsync(connection, null, actor, "user:manage", ct);
        var owned = (await permissionCeiling.GetCodesAsync(connection, null, actor.Id, ct)).ToArray();
        var isAdmin = await AccessService.IsSystemAdminAsync(connection, null, actor.Id, ct);
        var term = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var query = context.Roles.AsNoTracking().Where(role => role.Status == AccountStatuses.Active
            && !(role.IsBuiltIn && role.Name == "供应商人员")
            && (isAdmin || (!(role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator)
                && !context.RolePermissions.Where(rp => rp.RoleId == role.Id)
                    .Join(context.Permissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Code)
                    .Any(code => !Enumerable.Contains(owned, code)))));
        if (term is not null)
        {
            var pattern = QueryValues.ContainsPattern(term);
            query = query.Where(role => EF.Functions.Like(role.Name, pattern, QueryValues.LikeEscape));
        }
        return await query.OrderBy(role => role.Id).Select(role => new RoleOption(role.Id, role.Name)).ToArrayAsync(ct);
    }

    public async Task<UserResponse> CreateAsync(CurrentUser actor, UserCreate request, CancellationToken ct)
    {
        AdminValidation.EmployeeNo(request.EmployeeNo); ValidateName(request.RealName); AdminValidation.Email(request.Email); PasswordService.Validate(request.Password);
        var roleId = AdminValidation.OneRole(request.RoleId, request.RoleIds, true);
        if (request.DepartmentId is not ulong departmentId) throw ApiException.BadRequest("请选择所属组织");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAsync(context, actor, "user:manage", ct);
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await permissionCeiling.EnsureManageRoleAsync(connection, dbTransaction, actor, roleId, ct);
        await DepartmentService.EnsureActiveAsync(context, departmentId, ct);
        await EnsureRoleAssignableAsync(context, roleId, ct);
        var employeeNo = request.EmployeeNo.Trim();
        if (await context.Users.AnyAsync(x => x.EmployeeNo == employeeNo, ct)) throw ApiException.Conflict("工号已存在");
        var user = new User
        {
            EmployeeNo = employeeNo, PasswordHash = await PasswordService.HashAsync(request.Password, ct), RealName = request.RealName.Trim(),
            Email = request.Email.Trim(), UserType = UserTypes.Internal, DepartmentId = departmentId, Status = AccountStatuses.Active,
            MustChangePassword = true, FailedLoginAttempts = 0, CreatedBy = actor.Id
        };
        context.Users.Add(user); await context.SaveChangesAsync(ct);
        context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId }); await context.SaveChangesAsync(ct);
        var created = await FindAsync(context, user.Id, ct) ?? throw ApiException.NotFound();
        var role = await FindRoleAsync(context, roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        var department = await FindDepartmentAsync(context, departmentId, ct);
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_CREATE", "user", user.Id, new
        {
            employeeNo = created.EmployeeNo, request.DepartmentId, roleId, created.RealName, created.Email,
            departmentName = department?.Name, roleName = role.Name, targetName = UserAuditName(created),
            changes = AuditChange.OnlyChanged(
                new AuditChange("employeeNo", "工号", null, created.EmployeeNo), new AuditChange("realName", "姓名", null, created.RealName),
                new AuditChange("email", "邮箱", null, created.Email), new AuditChange("departmentId", "所属组织", null, DepartmentAuditJson(department)),
                new AuditChange("roleId", "角色", null, RoleAuditJson(role)), new AuditChange("status", "状态", null, created.Status))
        }, null, ct);
        var result = await JsonAsync(context, created, ct); await transaction.CommitAsync(ct); return result;
    }

    public async Task<UserResponse> UpdateAsync(CurrentUser actor, ulong id, UserUpdate request, CancellationToken ct)
    {
        if (request.RealName is not null) ValidateName(request.RealName);
        if (request.Email is not null) AdminValidation.Email(request.Email);
        var departmentSpecified = request.DepartmentId.ValueKind != System.Text.Json.JsonValueKind.Undefined;
        ulong? departmentId = request.DepartmentId.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Number when request.DepartmentId.TryGetUInt64(out var parsed) => parsed,
            _ => throw ApiException.BadRequest("组织编号格式不正确")
        };
        var roleId = AdminValidation.OneRole(request.RoleId, request.RoleIds, false);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAsync(context, actor, "user:manage", ct);
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        var user = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (user.UserType != UserTypes.Internal) throw ApiException.BadRequest("供应商人员请在供应商模块维护");
        await permissionCeiling.EnsureManageUserAsync(connection, dbTransaction, actor, id, ct);
        if (departmentSpecified && departmentId is null || !departmentSpecified && user.DepartmentId is null) throw ApiException.BadRequest("请选择所属组织");
        var effectiveDepartmentId = departmentSpecified ? departmentId : user.DepartmentId;
        if (effectiveDepartmentId is not ulong department) throw ApiException.BadRequest("请选择所属组织");
        await DepartmentService.EnsureActiveAsync(context, department, ct);
        var oldRoleIds = await context.UserRoles.AsNoTracking().Where(x => x.UserId == id).Select(x => x.RoleId).Distinct().ToArrayAsync(ct);
        if (oldRoleIds.Length != 1) throw ApiException.BadRequest("启用的内部用户必须且只能绑定一个角色");
        var oldRoleId = oldRoleIds[0]; var oldRole = await FindRoleAsync(context, oldRoleId, ct);
        var oldDepartment = user.DepartmentId is ulong oldDepartmentId ? await FindDepartmentAsync(context, oldDepartmentId, ct) : null;
        var oldRealName = user.RealName; var oldEmail = user.Email; var oldDepartmentIdForAudit = user.DepartmentId;
        var roleChanged = roleId != 0 && roleId != oldRoleId; var departmentChanged = departmentSpecified && departmentId != user.DepartmentId;
        if (roleChanged)
        {
            await EnsureAdminRemovalSafeAsync(context, actor.Id, id, roleId, ct);
            await permissionCeiling.EnsureManageRoleAsync(connection, dbTransaction, actor, roleId, ct);
            await EnsureRoleAssignableAsync(context, roleId, ct);
        }
        else if (user.Status == AccountStatuses.Active) await EnsureRoleAssignableAsync(context, oldRoleId, ct);
        if (departmentChanged) await EnsureNoActiveProjectResponsibilityWithSectionAsync(context, id, ct);
        if (roleChanged && !await RoleHasPermissionAsync(context, roleId, "project:list", ct))
            await EnsureNoActiveProjectResponsibilityAsync(context, id, ct);
        context.Attach(user);
        if (request.RealName is not null) user.RealName = request.RealName.Trim();
        if (request.Email is not null) user.Email = request.Email.Trim();
        if (departmentSpecified) user.DepartmentId = departmentId;
        await context.SaveChangesAsync(ct);
        if (roleChanged)
        {
            await context.UserRoles.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
            context.UserRoles.Add(new UserRole { UserId = id, RoleId = roleId }); await context.SaveChangesAsync(ct);
        }
        var updated = await FindAsync(context, id, ct) ?? throw ApiException.NotFound(); var newRoleId = roleChanged ? roleId : oldRoleId;
        var newRole = await FindRoleAsync(context, newRoleId, ct);
        var newDepartment = updated.DepartmentId is ulong newDepartmentId ? await FindDepartmentAsync(context, newDepartmentId, ct) : null;
        var changes = AuditChange.OnlyChanged(
            new AuditChange("realName", "姓名", oldRealName, updated.RealName), new AuditChange("email", "邮箱", oldEmail, updated.Email),
            new AuditChange("departmentId", "所属组织", DepartmentAuditJson(oldDepartment), DepartmentAuditJson(newDepartment)),
            new AuditChange("roleId", "角色", RoleAuditJson(oldRole), RoleAuditJson(newRole)));
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_UPDATE", "user", id, new
        {
            updated.EmployeeNo, changedFields = changes.Select(x => x.Field).ToArray(), oldDepartmentId = oldDepartmentIdForAudit,
            newDepartmentId = updated.DepartmentId, oldRoleId, newRoleId, oldDepartmentName = oldDepartment?.Name,
            newDepartmentName = newDepartment?.Name, oldRoleName = oldRole?.Name, newRoleName = newRole?.Name,
            targetName = UserAuditName(updated), changes
        }, null, ct);
        var result = await JsonAsync(context, updated, ct); await transaction.CommitAsync(ct); return result;
    }

    public async Task<UserResponse> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAsync(context, actor, "user:manage", ct);
        if (id == actor.Id) throw ApiException.BadRequest("不能禁用自己的账号");
        var user = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (user.UserType != UserTypes.Internal) throw ApiException.BadRequest("供应商人员请在供应商模块维护");
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await permissionCeiling.EnsureManageUserAsync(connection, dbTransaction, actor, id, ct);
        if (status == AccountStatuses.Disabled) { await EnsureNoActiveProjectResponsibilityAsync(context, id, ct); await EnsureAdminRemovalSafeAsync(context, actor.Id, id, null, ct); }
        else
        {
            if (user.DepartmentId is not ulong departmentId) throw ApiException.BadRequest("请选择所属组织");
            await DepartmentService.EnsureActiveAsync(context, departmentId, ct);
            var roleIds = await context.UserRoles.AsNoTracking().Where(x => x.UserId == id).Select(x => x.RoleId).Distinct().ToArrayAsync(ct);
            if (roleIds.Length != 1) throw ApiException.BadRequest("启用的内部用户必须且只能绑定一个角色");
            await EnsureRoleAssignableAsync(context, roleIds[0], ct);
        }
        await context.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        if (status == AccountStatuses.Disabled) await IdentityService.RevokeAllAsync(connection, dbTransaction, id, ct);
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_STATUS", "user", id, new
        {
            user.EmployeeNo, oldStatus = user.Status, newStatus = status, targetName = UserAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", user.Status, status))
        }, null, ct);
        var result = await JsonAsync(context, await FindAsync(context, id, ct) ?? throw ApiException.NotFound(), ct);
        await transaction.CommitAsync(ct); return result;
    }

    public async Task ResetPasswordAsync(CurrentUser actor, ulong id, string password, CancellationToken ct)
    {
        PasswordService.Validate(password);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAsync(context, actor, "user:manage", ct);
        var user = await LockUserAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (user.UserType != UserTypes.Internal) throw ApiException.BadRequest("供应商人员请在供应商模块维护");
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await permissionCeiling.EnsureManageUserAsync(connection, dbTransaction, actor, id, ct);
        var hash = await PasswordService.HashAsync(password, ct);
        await context.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PasswordHash, hash)
            .SetProperty(x => x.MustChangePassword, true).SetProperty(x => x.FailedLoginAttempts, 0).SetProperty(x => x.LockedUntil, (DateTime?)null), ct);
        await IdentityService.RevokeAllAsync(connection, dbTransaction, id, ct);
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_RESET_PASSWORD", "user", id, new
        {
            user.EmployeeNo, sessionsRevoked = true, mustChangePassword = true, passwordChanged = true, targetName = UserAuditName(user),
            changes = AuditChange.OnlyChanged(new AuditChange("passwordChanged", "密码已重置", false, true))
        }, null, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task AssignRoleAsync(CurrentUser actor, ulong id, IReadOnlyList<ulong> roles, CancellationToken ct)
    {
        var roleId = AdminValidation.OneRole(null, roles, true);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAsync(context, actor, "user:manage", ct);
        var user = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (user.UserType != UserTypes.Internal) throw ApiException.BadRequest("供应商人员角色固定，不可调整");
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await permissionCeiling.EnsureManageUserAsync(connection, dbTransaction, actor, id, ct);
        await permissionCeiling.EnsureManageRoleAsync(connection, dbTransaction, actor, roleId, ct);
        await EnsureAdminRemovalSafeAsync(context, actor.Id, id, roleId, ct); await EnsureRoleAssignableAsync(context, roleId, ct);
        if (!await RoleHasPermissionAsync(context, roleId, "project:list", ct)) await EnsureNoActiveProjectResponsibilityAsync(context, id, ct);
        var old = await context.UserRoles.AsNoTracking().Where(x => x.UserId == id).Select(x => (ulong?)x.RoleId).SingleOrDefaultAsync(ct);
        var oldRole = old.HasValue ? await FindRoleAsync(context, old.Value, ct) : null;
        var newRole = await FindRoleAsync(context, roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        await context.UserRoles.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
        context.UserRoles.Add(new UserRole { UserId = id, RoleId = roleId }); await context.SaveChangesAsync(ct);
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_ASSIGN_ROLE", "user", id, new
        {
            user.EmployeeNo, oldRoleId = old, newRoleId = roleId, oldRoleName = oldRole?.Name, newRoleName = newRole.Name,
            targetName = UserAuditName(user), changes = AuditChange.OnlyChanged(new AuditChange("roleId", "角色", RoleAuditJson(oldRole), RoleAuditJson(newRole)))
        }, null, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var connection = context.Database.Connection(); var dbTransaction = context.Database.RequireTransaction();
        await AccessService.LockManagementAsync(connection, dbTransaction, ct); actor = await AccessService.RecheckActorAsync(connection, dbTransaction, actor, ct);
        AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(connection, dbTransaction, actor, "user:manage", ct);
        await AccessService.RequirePermissionAsync(connection, dbTransaction, actor, "user:delete", ct);
        var user = await LockUserAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (id == actor.Id) throw ApiException.BadRequest("不能删除自己的账号");
        if (user.UserType != UserTypes.Internal) throw ApiException.BadRequest("供应商人员请在供应商模块删除");
        if (user.EmployeeNo == "admin") throw ApiException.BadRequest("系统管理员账号不可删除");
        await permissionCeiling.EnsureManageUserAsync(connection, dbTransaction, actor, id, ct);
        await EnsureAdminRemovalSafeAsync(context, actor.Id, id, null, ct); await EnsureNoHistoryAsync(context, id, ct);
        await audit.WriteAsync(connection, dbTransaction, actor.Id, "USER_DELETE", "user", id,
            new { user.EmployeeNo, user.RealName, targetName = UserAuditName(user), changes = Array.Empty<AuditChange>() }, null, ct);
        await RemoveAsync(context, id, ct); await transaction.CommitAsync(ct);
    }

    internal static async Task EnsureNoHistoryAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        if (await context.ProjectGroups.AnyAsync(x => x.ResponsibleUserId == id, ct) || await context.Projects.AnyAsync(x => x.ResponsibleUserId == id, ct))
            throw ApiException.BadRequest("该账号仍是主项目或子项目负责人，请先结束相关项目");
        if (await context.ProjectGroups.AnyAsync(x => x.CreatedBy == id, ct) || await context.ProjectGroupStatusLogs.AnyAsync(x => x.OperatorId == id, ct))
            throw ApiException.BadRequest("该账号仍被主项目创建记录或状态历史引用，请禁用账号并保留历史记录");
        var hasHistory = await context.Files.AnyAsync(x => x.UploaderId == id, ct)
            || await context.Messages.AnyAsync(x => x.SenderId == id || x.DeletedBy == id, ct)
            || await context.UploadSessions.AnyAsync(x => x.UploaderId == id, ct)
            || await context.Projects.AnyAsync(x => x.CreatedBy == id, ct)
            || await context.ProjectStatusLogs.AnyAsync(x => x.OperatorId == id, ct)
            || await context.MessageReads.AnyAsync(x => x.UserId == id, ct)
            || await context.CollaborationReads.AnyAsync(x => x.UserId == id, ct)
            || await context.Suppliers.AnyAsync(x => x.CreatedBy == id, ct)
            || await context.Users.AnyAsync(x => x.CreatedBy == id, ct)
            || await context.EmailOutbox.AnyAsync(x => x.RecipientUserId == id, ct)
            || await context.AuditLogs.AnyAsync(x => x.UserId == id, ct)
            || await context.ProjectActivities.AnyAsync(x => x.ActorId == id, ct)
            || await context.ProjectCopies.AnyAsync(x => x.CopiedBy == id, ct)
            || await context.ProjectCopyJobs.AnyAsync(x => x.RequestedBy == id, ct);
        if (hasHistory) throw ApiException.BadRequest("该账号仍有业务或历史记录，请禁用账号，不要删除");
    }

    internal static async Task RemoveAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        await context.UserRoles.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
        await context.RefreshTokens.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
        await context.Users.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    }

    internal static async Task EnsureNoActiveProjectResponsibilityAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var active = new[] { "DRAFT", "IN_PROGRESS" };
        var count = await context.ProjectGroups.LongCountAsync(x => x.ResponsibleUserId == id && Enumerable.Contains(active, x.Status), ct);
        if (count > 0) throw ApiException.BadRequest($"该用户仍负责 {count} 个未结束主项目，请先结束相关项目");
    }

    private static async Task EnsureNoActiveProjectResponsibilityWithSectionAsync(
        YfDbContext context, ulong id, CancellationToken ct)
    {
        var active = new[] { "DRAFT", "IN_PROGRESS" };
        var count = await context.ProjectGroups.LongCountAsync(x => x.ResponsibleUserId == id
            && x.SectionId != null && Enumerable.Contains(active, x.Status), ct);
        if (count > 0)
            throw ApiException.BadRequest($"该用户仍负责 {count} 个带课别的未结束主项目，请先结束相关项目");
    }

    private static Task<bool> RoleHasPermissionAsync(YfDbContext context, ulong roleId, string permission, CancellationToken ct) =>
        context.RolePermissions.Where(x => x.RoleId == roleId).Join(context.Permissions.Where(x => x.Code == permission), x => x.PermissionId, x => x.Id, (_, _) => true).AnyAsync(ct);

    private static async Task ManagementAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        var connection = context.Database.Connection(); var transaction = context.Database.RequireTransaction();
        await AccessService.LockManagementAsync(connection, transaction, ct); actor = await AccessService.RecheckActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(connection, transaction, actor, permission, ct);
    }

    private static Task<User?> FindAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    private static Task<User?> LockUserAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id={id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
    private static Task<Role?> FindRoleAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Roles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    private static Task<Department?> FindDepartmentAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    private static object? RoleAuditJson(Role? role) => role is null ? null : new { role.Id, role.Name };
    private static object? DepartmentAuditJson(Department? department) => department is null ? null : new { department.Id, department.Name };
    private static string UserAuditName(User user) => $"{user.RealName}（{user.EmployeeNo}）";

    private static async Task EnsureRoleAssignableAsync(YfDbContext context, ulong roleId, CancellationToken ct)
    {
        var role = await FindRoleAsync(context, roleId, ct) ?? throw ApiException.BadRequest($"角色不存在: {roleId}");
        if (role.Status != AccountStatuses.Active) throw ApiException.BadRequest("不能绑定已禁用的角色");
        if (role.IsBuiltIn && role.Name == "供应商人员") throw ApiException.BadRequest("供应商角色只能由供应商账号使用");
    }

    private static async Task EnsureAdminRemovalSafeAsync(YfDbContext context, ulong actorId, ulong targetId, ulong? newRoleId, CancellationToken ct)
    {
        var adminRole = await context.Roles.AsNoTracking().Where(x => x.IsBuiltIn && x.Name == BuiltInRoleNames.SystemAdministrator).Select(x => (ulong?)x.Id).SingleOrDefaultAsync(ct);
        if (!adminRole.HasValue || newRoleId == adminRole) return;
        var targetStatus = await context.Users.AsNoTracking().Where(x => x.Id == targetId)
            .Join(context.UserRoles.Where(x => x.RoleId == adminRole.Value), x => x.Id, x => x.UserId, (x, _) => x.Status).SingleOrDefaultAsync(ct);
        if (targetStatus is null) return;
        if (actorId == targetId) throw ApiException.BadRequest("不能移除自己的系统管理员角色");
        var active = await context.Users.AsNoTracking().Where(x => x.Status == AccountStatuses.Active)
            .Join(context.UserRoles.Where(x => x.RoleId == adminRole.Value), x => x.Id, x => x.UserId, (_, _) => true).CountAsync(ct);
        if (RequiresLastActiveAdminProtection(targetStatus, active)) throw ApiException.BadRequest("不能移除系统中最后一个启用管理员");
    }

    internal static bool RequiresLastActiveAdminProtection(string targetStatus, int activeAdminCount) => targetStatus == AccountStatuses.Active && activeAdminCount <= 1;

    private static async Task<UserResponse> JsonAsync(YfDbContext context, User user, CancellationToken ct) =>
        (await ToResponsesAsync(context, [user], ct))[0];

    /// <summary>Builds user responses with a fixed number of queries, however many users are listed.</summary>
    private static async Task<List<UserResponse>> ToResponsesAsync(YfDbContext context, IReadOnlyList<User> users, CancellationToken ct)
    {
        var userIds = users.Select(user => user.Id).ToArray();
        var rolesByUser = (await context.UserRoles.AsNoTracking().Where(x => Enumerable.Contains(userIds, x.UserId))
                .Join(context.Roles.AsNoTracking(), x => x.RoleId, x => x.Id, (userRole, role) => new { userRole.UserId, role.Id, role.Name })
                .OrderBy(x => x.Id).ToListAsync(ct))
            .ToLookup(x => x.UserId);
        var departmentIds = users.Where(user => user.DepartmentId is not null).Select(user => user.DepartmentId!.Value).Distinct().ToArray();
        var departmentNames = departmentIds.Length == 0
            ? new Dictionary<ulong, string>()
            : await context.Departments.AsNoTracking().Where(x => Enumerable.Contains(departmentIds, x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return users.Select(user =>
        {
            var roles = rolesByUser[user.Id].ToList();
            var firstRole = roles.FirstOrDefault();
            var departmentName = user.DepartmentId is ulong departmentId ? departmentNames.GetValueOrDefault(departmentId) : null;
            return new UserResponse(
                user.Id, user.EmployeeNo, user.RealName, user.Email, user.UserType, user.SupplierId, user.DepartmentId, departmentName,
                user.Status, user.MustChangePassword, user.LastLoginAt, user.CreatedAt, firstRole?.Id, firstRole?.Name,
                roles.Select(x => x.Id).ToArray(), roles.Select(x => x.Name).ToArray());
        }).ToList();
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().EnumerateRunes().Count() > 32) throw ApiException.BadRequest("姓名需为 1~32 个字符");
    }
}
