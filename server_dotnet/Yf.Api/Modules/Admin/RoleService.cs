using Microsoft.EntityFrameworkCore;
using System.Collections.Frozen;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

public sealed class RoleService(IDbContextFactory<YfDbContext> dbFactory, PermissionService ceiling, AuditService audit)
{
    internal static FrozenSet<string> SupplierPermissionCodes { get; } = new[]
    {
        "dashboard", "project:list", "file:upload", "file:download", "file:preview",
        "message:create"
    }.ToFrozenSet(StringComparer.Ordinal);

    // Supplier-only workflow actions: internal accounts can never exercise them, so they are left out of
    // the delegation ceiling when an internal manager assigns a supplier role.
    internal static FrozenSet<string> SupplierExclusivePermissionCodes { get; } =
        PermissionDelegationPolicy.SupplierExclusivePermissionCodes;

    public async Task<PermissionResponse[]> PermissionsAsync(CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await RequireRoleReadAsync(context, actor, ct);
        var owned = (await ceiling.GetCodesAsync(context.Database.Connection(), null, actor.Id, ct)).ToHashSet(StringComparer.Ordinal);
        var isAdmin = await AccessService.IsSystemAdminAsync(context.Database.Connection(), null, actor.Id, ct);
        var all = await context.Permissions.AsNoTracking().OrderBy(p => p.SortNo).ThenBy(p => p.Id).ToListAsync(ct);
        return all.Select(x => new PermissionResponse(
            x.Id, x.Code, x.Name, x.Type, x.ParentId, x.SortNo,
            isAdmin || owned.Contains(x.Code),
            SupplierPermissionCodes.Contains(x.Code))).ToArray();
    }

    private static async Task RequireRoleReadAsync(YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        var connection = context.Database.Connection();
        if (!await AccessService.HasPermissionAsync(connection, null, actor.Id, "role:view", ct)
            && !await AccessService.HasPermissionAsync(connection, null, actor.Id, "role:manage", ct))
            throw ApiException.Forbidden();
    }

    public async Task<DataScopeResponse[]> GetDataScopesAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await RequireRoleReadAsync(context, actor, ct);
        _ = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        return await DescribeScopesAsync(context, id, ct);
    }

    /// <summary>
    /// Replaces the role's default data ranges. A null scope removes the default so members fall back to SELF
    /// (or to ORG while they lead an organisation). A non-administrator cannot grant more than they hold.
    /// </summary>
    public async Task<DataScopeResponse[]> SetDataScopesAsync(
        CurrentUser actor, ulong id, DataScopeAssign request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        ValidateScopeEntries(request.Scopes);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        await ApplyScopeAssignmentAsync(context, actor, role, id, request.Scopes, ct);
        var result = await DescribeScopesAsync(context, id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Saves a role's action permissions and data ranges in one transaction, so the drawer can no longer leave a
    /// half-applied grant behind when the second write fails. A null member leaves that part untouched.
    /// </summary>
    public async Task<RoleAuthorizationResponse> SetAuthorizationAsync(
        CurrentUser actor, ulong id, RoleAuthorizationUpdate request, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        if (request.PermissionIds is null && request.Scopes is null)
            throw ApiException.BadRequest("请提供权限或数据范围");
        if (request.PermissionIds is { Count: > 500 }) throw ApiException.BadRequest("权限点数量超过上限");
        if (request.Scopes is not null) ValidateScopeEntries(request.Scopes);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (request.PermissionIds is { } permissionIds)
            await ApplyPermissionAssignmentAsync(context, actor, role, id, permissionIds.Distinct().ToArray(), ct);
        if (request.Scopes is { } scopes)
            await ApplyScopeAssignmentAsync(context, actor, role, id, scopes, ct);
        var result = new RoleAuthorizationResponse(
            // 调用到这里已经通过 EnsureManageRoleAsync，因此对本次调用者而言 canManage 恒为真。
            await JsonAsync(context, role, canManage: true, ct),
            await DescribeScopesAsync(context, id, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Writes a role's default data ranges; the caller owns the transaction and the gate.</summary>
    private async Task ApplyScopeAssignmentAsync(
        YfDbContext context, CurrentUser actor, Role role, ulong id,
        IReadOnlyList<DataScopeEntry> scopes, CancellationToken ct)
    {
        // The built-in system administrator always sees every transfer, so a narrower OEM default here would be a
        // value the query ignores; the range is fixed and the editor must not pretend otherwise.
        if (role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator)
            throw ApiException.BadRequest("系统管理员角色的数据范围固定为全部");
        // A default changes the range of every member without a personal override. Setting a level above the actor
        // is refused outright (even for a role nobody holds yet); beyond that, each member's resulting range — ORG
        // compared by the organisations it covers — must stay inside the actor's own range. Clearing a default is
        // checked the same way, because members who lead an organisation fall back to ORG.
        if (!await AccessService.IsSystemAdminAsync(
                context.Database.Connection(), context.Database.RequireTransaction(), actor.Id, ct))
        {
            foreach (var entry in scopes.Where(entry => entry.Scope is not null))
            {
                var grantor = await DataScopeService.ResolveAsync(context, actor.Id, entry.Domain, ct);
                if (!DataScopeService.CanGrant(grantor.Scope, entry.Scope!))
                    throw ApiException.Forbidden(DataScopeCeiling.WiderThanActorMessage);
            }
        }
        var memberIds = await context.UserRoles.AsNoTracking().Where(member => member.RoleId == id)
            .Select(member => member.UserId).Distinct().ToArrayAsync(ct);
        var scopeSnapshot = await DataScopeCeiling.CaptureAsync(context, actor, memberIds, ct);
        var before = await context.RoleDataScopes.AsNoTracking()
            .Where(setting => setting.RoleId == id)
            .ToDictionaryAsync(setting => setting.Domain, setting => setting.Scope, ct);
        var existing = await context.RoleDataScopes.Where(setting => setting.RoleId == id).ToArrayAsync(ct);
        foreach (var entry in scopes)
        {
            var row = existing.FirstOrDefault(setting => setting.Domain == entry.Domain);
            if (entry.Scope is null)
            {
                if (row is not null) context.RoleDataScopes.Remove(row);
            }
            else if (row is null)
            {
                context.RoleDataScopes.Add(new RoleDataScope
                {
                    RoleId = id, Domain = entry.Domain, Scope = entry.Scope,
                });
            }
            else row.Scope = entry.Scope;
        }
        await context.SaveChangesAsync(ct);
        await DataScopeCeiling.EnsureAsync(context, scopeSnapshot, memberIds, [], ct);
        var after = await context.RoleDataScopes.AsNoTracking()
            .Where(setting => setting.RoleId == id)
            .ToDictionaryAsync(setting => setting.Domain, setting => setting.Scope, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "ROLE_DATA_SCOPE", "role", id, new
            {
                role.Name,
                targetName = role.Name,
                before = OrderedScopes(before),
                after = OrderedScopes(after),
                changes = AuditChange.OnlyChanged(new AuditChange(
                    "dataScopes", "数据范围", OrderedScopes(before), OrderedScopes(after))),
            }, null, ct);
    }

    private static void ValidateScopeEntries(IReadOnlyList<DataScopeEntry>? entries)
    {
        if (entries is null || entries.Count == 0) throw ApiException.BadRequest("请提供数据范围");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!DataScopeDomains.IsValid(entry.Domain))
                throw ApiException.BadRequest($"未知的数据范围: {entry.Domain}");
            if (!seen.Add(entry.Domain))
                throw ApiException.BadRequest($"数据范围重复: {entry.Domain}");
            if (entry.Scope is not null && !DataScopeValues.IsValid(entry.Scope))
                throw ApiException.BadRequest("数据范围只能为 SELF、ORG 或 ALL");
        }
    }

    private static async Task<DataScopeResponse[]> DescribeScopesAsync(
        YfDbContext context, ulong roleId, CancellationToken ct)
    {
        var local = await context.RoleDataScopes.AsNoTracking()
            .Where(setting => setting.RoleId == roleId)
            .ToDictionaryAsync(setting => setting.Domain, setting => setting.Scope, ct);
        return DataScopeDomains.All.Select(domain =>
        {
            var scope = local.GetValueOrDefault(domain) ?? DataScopeValues.Self;
            return new DataScopeResponse(domain, DataScopeDomains.Label(domain),
                local.GetValueOrDefault(domain), scope, DataScopeValues.Label(scope));
        }).ToArray();
    }

    private static Dictionary<string, string> OrderedScopes(IReadOnlyDictionary<string, string> scopes) =>
        scopes.OrderBy(setting => setting.Key, StringComparer.Ordinal)
            .ToDictionary(setting => setting.Key, setting => setting.Value);

    public async Task<PageResponse<RoleResponse>> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await RequireRoleReadAsync(context, actor, ct);
        var total = await context.Roles.LongCountAsync(ct);
        var rows = offset > int.MaxValue ? []
            : await context.Roles.AsNoTracking().OrderBy(r => r.Id).Skip((int)offset).Take((int)size).ToArrayAsync(ct);
        var manageable = await ceiling.GetManageableRoleIdsAsync(context.Database.Connection(), null, actor, rows.Select(r => r.Id).ToArray(), ct);
        var list = await ToResponsesAsync(context, rows, manageable.Contains, ct);
        return new(list, (ulong)total, page, size);
    }

    public async Task<RoleResponse> CreateAsync(CurrentUser actor, RoleUpsert request, CancellationToken ct)
    {
        Validate(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        var name = request.Name.Trim();
        await UniqueAsync(context, name, null, ct);
        var created = new Role { Name = name, Description = request.Description, IsBuiltIn = false, Status = AccountStatuses.Active };
        context.Roles.Add(created);
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_CREATE", "role", created.Id, new
        {
            name,
            status = AccountStatuses.Active,
            targetName = name,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "角色名称", null, name),
                new AuditChange("description", "角色说明", null, request.Description),
                new AuditChange("status", "状态", null, AccountStatuses.Active))
        }, null, ct);
        var result = await JsonAsync(context, created, true, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RoleResponse> UpdateAsync(CurrentUser actor, ulong id, RoleUpsert request, CancellationToken ct)
    {
        Validate(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        var newName = request.Name.Trim();
        if (role.IsBuiltIn && newName != role.Name) throw ApiException.BadRequest("内置角色名称不可修改");
        await UniqueAsync(context, newName, id, ct);
        await context.Roles.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Name, newName)
            .SetProperty(r => r.Description, request.Description), ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_UPDATE", "role", id, new
        {
            oldName = role.Name,
            newName,
            descriptionChanged = role.Description != request.Description,
            targetName = newName,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "角色名称", role.Name, newName),
                new AuditChange("description", "角色说明", role.Description, request.Description))
        }, null, ct);
        var updated = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        var result = await JsonAsync(context, updated, true, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RoleResponse> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (status == AccountStatuses.Disabled)
        {
            await EnsureProjectListRemovalSafeAsync(context, id, ct);
            var count = await context.Users.Join(context.UserRoles, u => u.Id, ur => ur.UserId, (u, ur) => new { u, ur })
                .CountAsync(x => x.ur.RoleId == id && x.u.Status == AccountStatuses.Active, ct);
            if (count > 0) throw ApiException.BadRequest($"该角色仍绑定 {count} 个启用用户，请先为这些用户更换角色或停用账号");
        }
        await context.Roles.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status), ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_STATUS", "role", id, new
        {
            role.Name,
            oldStatus = role.Status,
            newStatus = status,
            targetName = role.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", role.Status, status))
        }, null, ct);
        var updated = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        var result = await JsonAsync(context, updated, true, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task AssignPermissionsAsync(CurrentUser actor, ulong id, IReadOnlyList<ulong> requested, CancellationToken ct)
    {
        if (requested is null) throw ApiException.BadRequest("权限点不能为空");
        if (requested.Count > 500) throw ApiException.BadRequest("权限点数量超过上限");
        var ids = requested.Distinct().ToArray();
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        await ApplyPermissionAssignmentAsync(context, actor, role, id, ids, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Writes a role's action permissions; the caller owns the transaction and the gate.</summary>
    private async Task ApplyPermissionAssignmentAsync(
        YfDbContext context, CurrentUser actor, Role role, ulong id, ulong[] ids, CancellationToken ct)
    {
        var assignedToSupplier = await context.UserRoles.Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
            .AnyAsync(x => x.ur.RoleId == id && x.u.UserType == UserTypes.Supplier, ct);
        var supplierRestricted = assignedToSupplier || role.IsBuiltIn && role.Name == AdminBuiltIns.SupplierRoleName;
        await ceiling.EnsureGrantableAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ids, ct,
            supplierRestricted ? SupplierExclusivePermissionCodes : null);
        var valid = ids.Length == 0 ? [] : await context.Permissions.AsNoTracking().Where(p => Enumerable.Contains(ids, p.Id)).ToArrayAsync(ct);
        if (valid.Length != ids.Length) throw ApiException.BadRequest("权限点不存在");
        if (!IsSupplierPermissionSetAllowed(role.IsBuiltIn, role.Name, valid.Select(x => x.Code)) ||
            assignedToSupplier && !IsSupplierAccountPermissionSetAllowed(valid.Select(x => x.Code)))
            throw ApiException.BadRequest("供应商账号角色只能授予供应商自有项目所需权限");
        if (role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator)
        {
            var boundActiveInternal = await context.UserRoles.Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
                .CountAsync(x => x.ur.RoleId == id && x.u.UserType == UserTypes.Internal
                    && x.u.Status == AccountStatuses.Active, ct);
            if (boundActiveInternal > 0)
            {
                var requiredCodes = new[] { "rbac:role", "role:manage", "org:user", "user:manage" };
                var required = await context.Permissions.AsNoTracking().Where(p => Enumerable.Contains(requiredCodes, p.Code)).ToArrayAsync(ct);
                if (required.Length != 4 || required.Any(x => !ids.Contains(x.Id))) throw ApiException.BadRequest("系统管理员角色绑定启用用户时，必须保留用户管理和角色管理权限");
            }
        }
        var oldPermissions = await context.RolePermissions.Where(rp => rp.RoleId == id)
            .Join(context.Permissions, rp => rp.PermissionId, p => p.Id, (rp, p) => p)
            .OrderBy(p => p.Id).AsNoTracking().ToArrayAsync(ct);
        if (oldPermissions.Any(permission => permission.Code == "project:list")
            && valid.All(permission => permission.Code != "project:list"))
            await EnsureProjectListRemovalSafeAsync(context, id, ct);
        var old = oldPermissions.Select(x => x.Id).ToArray();
        var oldIds = old.ToHashSet();
        var newIds = ids.ToHashSet();
        var orderedNewPermissions = valid.OrderBy(x => x.Id).ToArray();
        var addedPermissions = orderedNewPermissions.Where(x => !oldIds.Contains(x.Id)).Select(PermissionAuditJson).ToArray();
        var removedPermissions = oldPermissions.Where(x => !newIds.Contains(x.Id)).Select(PermissionAuditJson).ToArray();
        var oldPermissionDetails = oldPermissions.Select(PermissionAuditJson).ToArray();
        var newPermissionDetails = orderedNewPermissions.Select(PermissionAuditJson).ToArray();
        await context.RolePermissions.Where(rp => rp.RoleId == id).ExecuteDeleteAsync(ct);
        context.RolePermissions.AddRange(ids.Select(permissionId => new RolePermission { RoleId = id, PermissionId = permissionId }));
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_ASSIGN_PERMS", "role", id, new
        {
            oldPermissionCount = old.Length,
            newPermissionCount = ids.Length,
            oldPermissionIds = old,
            newPermissionIds = ids,
            roleName = role.Name,
            targetName = role.Name,
            addedPermissions,
            removedPermissions,
            changes = AuditChange.OnlyChanged(new AuditChange("permissions", "权限", oldPermissionDetails, newPermissionDetails))
        }, null, ct);
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await GateAsync(context, actor, "role:manage", ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "role:delete", ct);
        await ceiling.EnsureManageRoleAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, id, ct);
        var role = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (role.IsBuiltIn) throw ApiException.BadRequest("内置角色不可删除");
        var assigned = await context.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
        if (assigned > 0) throw ApiException.BadRequest($"该角色仍绑定 {assigned} 个用户，请先为这些用户更换角色");
        await context.RolePermissions.Where(rp => rp.RoleId == id).ExecuteDeleteAsync(ct);
        await context.Roles.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_DELETE", "role", id, new { role.Name, targetName = role.Name, changes = Array.Empty<AuditChange>() }, null, ct);
        await tx.CommitAsync(ct);
    }

    internal static bool IsSupplierPermissionSetAllowed(bool isBuiltIn, string roleName, IEnumerable<string> codes) =>
        !isBuiltIn || roleName != AdminBuiltIns.SupplierRoleName || codes.All(SupplierPermissionCodes.Contains);
    internal static bool IsSupplierAccountPermissionSetAllowed(IEnumerable<string> codes) => codes.All(SupplierPermissionCodes.Contains);

    internal static async Task EnsureProjectListRemovalSafeAsync(YfDbContext context, ulong roleId, CancellationToken ct)
    {
        var count = await (from g in context.Projects
                           join ur in context.UserRoles on g.ResponsibleUserId equals (ulong?)ur.UserId
                           where ur.RoleId == roleId && g.Status == "IN_PROGRESS"
                           select g.Id).Distinct().CountAsync(ct);
        if (count > 0) throw ApiException.BadRequest($"该角色仍有用户负责 {count} 个未结束项目，请先结束相关项目再移除项目列表权限");
    }

    private static async Task GateAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        await ManagementAuthorization.RequireAsync(context, actor, permission, ct);
    }

    private static void Validate(RoleUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("角色名称需为 1~64 个字符"); if (r.Description?.EnumerateRunes().Count() > 255) throw ApiException.BadRequest("角色说明不能超过 255 个字符"); }

    private static async Task UniqueAsync(YfDbContext context, string name, ulong? id, CancellationToken ct)
    {
        if (await context.Roles.AnyAsync(r => r.Name == name && (id == null || r.Id != id), ct)) throw ApiException.Conflict("角色名称已存在");
    }

    private static object PermissionAuditJson(Permission permission) => new { permission.Id, permission.Code, permission.Name };
    private static Task<Role?> FindAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);

    private static async Task<RoleResponse> JsonAsync(YfDbContext context, Role r, bool canManage, CancellationToken ct) =>
        (await ToResponsesAsync(context, [r], _ => canManage, ct))[0];

    /// <summary>Builds role responses with a fixed number of queries, however many roles are listed.</summary>
    private static async Task<List<RoleResponse>> ToResponsesAsync(
        YfDbContext context, IReadOnlyList<Role> roles, Func<ulong, bool> canManage, CancellationToken ct)
    {
        var roleIds = roles.Select(role => role.Id).ToArray();
        var permissionIds = (await context.RolePermissions.Where(rp => Enumerable.Contains(roleIds, rp.RoleId))
                .Select(rp => new { rp.RoleId, rp.PermissionId }).ToListAsync(ct))
            .ToLookup(rp => rp.RoleId, rp => rp.PermissionId);
        var assignedCounts = await context.UserRoles.Where(ur => Enumerable.Contains(roleIds, ur.RoleId))
            .GroupBy(ur => ur.RoleId).Select(group => new { RoleId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(row => row.RoleId, row => row.Count, ct);
        var assignedToSupplier = (await context.UserRoles.Where(ur => Enumerable.Contains(roleIds, ur.RoleId))
                .Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur.RoleId, u.UserType })
                .Where(x => x.UserType == UserTypes.Supplier).Select(x => x.RoleId).Distinct().ToListAsync(ct))
            .ToHashSet();
        return roles.Select(r => new RoleResponse(
            r.Id, r.Name, r.Description, r.IsBuiltIn, r.Status,
            permissionIds[r.Id].ToArray(),
            (ulong)assignedCounts.GetValueOrDefault(r.Id),
            canManage(r.Id),
            assignedToSupplier.Contains(r.Id) || r.IsBuiltIn && r.Name == AdminBuiltIns.SupplierRoleName,
            r.CreatedAt)).ToList();
    }
}
