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
        "message:create", "project:submit", "project:withdraw"
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<PermissionResponse[]> PermissionsAsync(CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var owned = (await ceiling.GetCodesAsync(context.Database.Connection(), null, actor.Id, ct)).ToHashSet(StringComparer.Ordinal);
        var isAdmin = await AccessService.IsSystemAdminAsync(context.Database.Connection(), null, actor.Id, ct);
        var all = await context.Permissions.AsNoTracking().OrderBy(p => p.SortNo).ThenBy(p => p.Id).ToListAsync(ct);
        return all.Select(x => new PermissionResponse(
            x.Id, x.Code, x.Name, x.Type, x.ParentId, x.SortNo,
            isAdmin || owned.Contains(x.Code),
            SupplierPermissionCodes.Contains(x.Code))).ToArray();
    }

    public async Task<PageResponse<RoleResponse>> ListAsync(CurrentUser actor, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, "role:manage", ct);
        var total = await context.Roles.LongCountAsync(ct);
        var rows = offset > int.MaxValue ? []
            : await context.Roles.AsNoTracking().OrderBy(r => r.Id).Skip((int)offset).Take((int)size).ToArrayAsync(ct);
        var manageable = await ceiling.GetManageableRoleIdsAsync(context.Database.Connection(), null, actor, rows.Select(r => r.Id).ToArray(), ct);
        var list = new List<RoleResponse>();
        foreach (var r in rows) list.Add(await JsonAsync(context, r, manageable.Contains(r.Id), ct));
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
        var created = new Role { Name = name, Description = request.Description, IsBuiltIn = false, Status = "ACTIVE" };
        context.Roles.Add(created);
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "ROLE_CREATE", "role", created.Id, new
        {
            name,
            status = "ACTIVE",
            targetName = name,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "角色名称", null, name),
                new AuditChange("description", "角色说明", null, request.Description),
                new AuditChange("status", "状态", null, "ACTIVE"))
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
        if (status == "DISABLED")
        {
            await EnsureProjectListRemovalSafeAsync(context, id, ct);
            var count = await context.Users.Join(context.UserRoles, u => u.Id, ur => ur.UserId, (u, ur) => new { u, ur })
                .CountAsync(x => x.ur.RoleId == id && x.u.Status == "ACTIVE", ct);
            if (count > 0) throw ApiException.BadRequest($"该角色仍绑定 {count} 个启用用户，请先为这些用户更换角色或禁用账号");
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
        await ceiling.EnsureGrantableAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ids, ct);
        var valid = ids.Length == 0 ? [] : await context.Permissions.AsNoTracking().Where(p => Enumerable.Contains(ids, p.Id)).ToArrayAsync(ct);
        if (valid.Length != ids.Length) throw ApiException.BadRequest("权限点不存在");
        var assignedToSupplier = await context.UserRoles.Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
            .AnyAsync(x => x.ur.RoleId == id && x.u.UserType == "SUPPLIER", ct);
        if (!IsSupplierPermissionSetAllowed(role.IsBuiltIn, role.Name, valid.Select(x => x.Code)) ||
            assignedToSupplier && !IsSupplierAccountPermissionSetAllowed(valid.Select(x => x.Code)))
            throw ApiException.BadRequest("供应商账号角色只能授予供应商自有项目所需权限");
        if (role.IsBuiltIn && role.Name == "系统管理员")
        {
            var boundActiveInternal = await context.UserRoles.Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
                .CountAsync(x => x.ur.RoleId == id && x.u.UserType == "INTERNAL" && x.u.Status == "ACTIVE", ct);
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
        await tx.CommitAsync(ct);
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
        !isBuiltIn || roleName != "供应商人员" || codes.All(SupplierPermissionCodes.Contains);
    internal static bool IsSupplierAccountPermissionSetAllowed(IEnumerable<string> codes) => codes.All(SupplierPermissionCodes.Contains);

    internal static async Task EnsureProjectListRemovalSafeAsync(YfDbContext context, ulong roleId, CancellationToken ct)
    {
        var count = await (from g in context.ProjectGroups
                           join ur in context.UserRoles on g.ResponsibleUserId equals (ulong?)ur.UserId
                           where ur.RoleId == roleId && (g.Status == "DRAFT" || g.Status == "IN_PROGRESS")
                           select g.Id).Distinct().CountAsync(ct);
        if (count > 0) throw ApiException.BadRequest($"该角色仍有用户负责 {count} 个未结束主项目，请先转交负责人再移除项目列表权限");
    }

    private static async Task GateAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        await AccessService.LockManagementAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        actor = await AccessService.RecheckActorAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, permission, ct);
    }

    private static void Validate(RoleUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("角色名称需为 1~64 个字符"); if (r.Description?.EnumerateRunes().Count() > 255) throw ApiException.BadRequest("角色说明不能超过 255 个字符"); }

    private static async Task UniqueAsync(YfDbContext context, string name, ulong? id, CancellationToken ct)
    {
        if (await context.Roles.AnyAsync(r => r.Name == name && (id == null || r.Id != id), ct)) throw ApiException.Conflict("角色名称已存在");
    }

    private static object PermissionAuditJson(Permission permission) => new { permission.Id, permission.Code, permission.Name };
    private static Task<Role?> FindAsync(YfDbContext context, ulong id, CancellationToken ct) => context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);

    private static async Task<RoleResponse> JsonAsync(YfDbContext context, Role r, bool canManage, CancellationToken ct)
    {
        var ids = await context.RolePermissions.Where(rp => rp.RoleId == r.Id).Select(rp => rp.PermissionId).ToArrayAsync(ct);
        var assigned = await context.UserRoles.LongCountAsync(ur => ur.RoleId == r.Id, ct);
        var assignedToSupplier = await context.UserRoles.Join(context.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
            .AnyAsync(x => x.ur.RoleId == r.Id && x.u.UserType == "SUPPLIER", ct);
        return new RoleResponse(
            r.Id, r.Name, r.Description, r.IsBuiltIn, r.Status,
            ids,
            (ulong)assigned,
            canManage,
            assignedToSupplier || r.IsBuiltIn && r.Name == "供应商人员",
            r.CreatedAt);
    }
}
