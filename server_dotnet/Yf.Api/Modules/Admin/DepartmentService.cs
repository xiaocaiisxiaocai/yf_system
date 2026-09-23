using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Admin;

public sealed class DepartmentService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    public async Task<object> ListAsync(CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var all = await context.Departments.AsNoTracking().OrderBy(d => d.SortNo).ThenBy(d => d.Id).ToListAsync(ct);
        List<object> Build(ulong? parent) => all.Where(x => x.ParentId == parent)
            .Select(x => (object)new
            {
                x.Id, x.Name, x.ParentId, x.Kind, x.SortNo, x.Status,
                children = Build(x.Id)
            }).ToList();
        return Build(null);
    }

    public Task<object> CreateAsync(CurrentUser actor, DepartmentUpsert request, CancellationToken ct) => WriteAsync(actor, null, request, ct);
    public Task<object> UpdateAsync(CurrentUser actor, ulong id, DepartmentUpsert request, CancellationToken ct) => WriteAsync(actor, id, request, ct);

    private async Task<object> WriteAsync(CurrentUser actor, ulong? id, DepartmentUpsert request, CancellationToken ct)
    {
        Validate(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockManagementAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        actor = await AccessService.RecheckActorAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "dept:manage", ct);
        Department? existing = null;
        if (id.HasValue) existing = await FindAsync(context, id.Value, ct) ?? throw ApiException.NotFound();
        if (id.HasValue && await WouldCycleAsync(context, id.Value, request.ParentId, ct)) throw ApiException.BadRequest("不能将组织移动到自身或其下级下");
        var newParent = request.ParentId is ulong parentId ? await FindAsync(context, parentId, ct) ?? throw ApiException.BadRequest("上级组织不存在") : null;
        var oldParent = existing?.ParentId is ulong oldParentId ? await FindAsync(context, oldParentId, ct) : null;
        var parentChanged = existing is not null && existing.ParentId != request.ParentId;
        var protectedProjectGroupIds = parentChanged
            ? await ActiveProjectGroupsWithValidOwnerInSubtreeAsync(context, id!.Value, ct)
            : [];
        var kind = existing is not null && !parentChanged
            ? existing.Kind
            : newParent?.Kind switch
            {
                null => "DIVISION",
                "DIVISION" => "DEPARTMENT",
                "DEPARTMENT" => "SECTION",
                _ => throw ApiException.BadRequest("课别下不能再新增下级，组织层级为 事业部 > 部门 > 课别")
            };
        if (parentChanged && await MaxDepthAsync(context, id!.Value, ct) > (kind switch { "DIVISION" => 2, "DEPARTMENT" => 1, _ => 0 })) throw ApiException.BadRequest("超出 事业部 > 部门 > 课别 三级");
        var name = request.Name.Trim();
        var duplicate = await context.Departments.AnyAsync(d =>
            d.Name == name && d.Kind == kind && d.ParentId == request.ParentId && (id == null || d.Id != id), ct);
        if (duplicate) throw ApiException.Conflict("同一上级和层级下组织名称已存在");
        // Snapshot pre-mutation values now: `existing` gets attached and mutated below,
        // so reading it after that point for the audit trail would show the new values.
        var oldName = existing?.Name;
        var oldKind = existing?.Kind;
        var oldSortNo = existing?.SortNo;
        var oldStatus = existing?.Status;
        var oldParentIdForAudit = existing?.ParentId;
        ulong resultId;
        if (id is null)
        {
            var created = new Department { Name = name, ParentId = request.ParentId, Kind = kind, SortNo = request.SortNo ?? 0, Status = "ACTIVE" };
            context.Departments.Add(created);
            await context.SaveChangesAsync(ct);
            resultId = created.Id;
        }
        else
        {
            resultId = id.Value;
            var target = existing!;
            // Re-attach for update: `existing` was loaded no-tracking above (FindAsync).
            context.Attach(target);
            target.Name = name;
            target.ParentId = request.ParentId;
            target.Kind = kind;
            if (request.SortNo is int sortNo) target.SortNo = sortNo;
            await context.SaveChangesAsync(ct);
            if (parentChanged)
            {
                await RecomputeAsync(context, resultId, kind, ct);
                await EnsureProjectOwnerSectionsRemainActiveAsync(context, protectedProjectGroupIds, ct);
            }
        }
        var row = await FindAsync(context, resultId, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, id is null ? "DEPT_CREATE" : "DEPT_UPDATE", "department", resultId, new
        {
            name = row.Name,
            row.Kind,
            parentId = row.ParentId,
            row.SortNo,
            targetName = row.Name,
            oldParentId = oldParentIdForAudit,
            newParentId = row.ParentId,
            oldParentName = oldParent?.Name,
            newParentName = newParent?.Name,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "组织名称", oldName, row.Name),
                new AuditChange("parent", "上级组织", DepartmentAuditJson(oldParent), DepartmentAuditJson(newParent)),
                new AuditChange("kind", "组织层级", oldKind, row.Kind),
                new AuditChange("sortNo", "排序号", oldSortNo, row.SortNo),
                new AuditChange("status", "状态", oldStatus, row.Status))
        }, null, ct);
        var result = new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, row.Status };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockManagementAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        actor = await AccessService.RecheckActorAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "dept:manage", ct);
        var row = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (status == "DISABLED" && row.Status != "DISABLED")
        {
            var affected = await ActiveProjectGroupsWithValidOwnerInSubtreeAsync(context, id, ct);
            if (affected.Length > 0)
                throw ApiException.BadRequest($"该组织范围内仍有 {affected.Length} 个未结束主项目负责人，请先转交负责人");
        }
        var oldStatus = row.Status;
        context.Attach(row);
        row.Status = status;
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "DEPT_STATUS", "department", id, new
        {
            row.Name,
            row.Kind,
            oldStatus,
            newStatus = status,
            targetName = row.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", oldStatus, status))
        }, null, ct);
        await tx.CommitAsync(ct);
        return new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, Status = status };
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockManagementAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        actor = await AccessService.RecheckActorAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "dept:manage", ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor, "dept:delete", ct);
        var row = await FindAsync(context, id, ct) ?? throw ApiException.NotFound();
        if (await context.Departments.AnyAsync(d => d.ParentId == id, ct)) throw ApiException.BadRequest("请先删除下级组织节点");
        if (await context.Users.AnyAsync(u => u.DepartmentId == id, ct)) throw ApiException.BadRequest("该组织仍有用户，请先调整用户归属或禁用组织");
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), actor.Id, "DEPT_DELETE", "department", id, new { row.Name, row.Kind, targetName = row.Name, changes = Array.Empty<AuditChange>() }, null, ct);
        await context.Departments.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static async Task EnsureActiveAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        ulong? current = id;
        var visited = new HashSet<ulong>();
        while (current is ulong currentId)
        {
            if (!visited.Add(currentId) || visited.Count > 32) throw ApiException.BadRequest("组织层级无效");
            var row = await context.Departments.AsNoTracking().Where(d => d.Id == currentId)
                .Select(d => new { d.ParentId, d.Status }).SingleOrDefaultAsync(ct);
            if (row is null) throw ApiException.BadRequest(currentId == id ? "组织不存在" : "组织层级无效");
            if (row.Status != "ACTIVE") throw ApiException.BadRequest("组织已被禁用");
            current = row.ParentId;
        }
    }

    private static void Validate(DepartmentUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("组织名称需为 1~64 个字符"); if (r.SortNo < 0) throw ApiException.BadRequest("排序号不能为负数"); }
    private static object? DepartmentAuditJson(Department? department) => department is null ? null : new { department.Id, department.Name };

    private static Task<Department?> FindAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        context.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);

    private static async Task<bool> WouldCycleAsync(YfDbContext context, ulong id, ulong? parent, CancellationToken ct)
    {
        for (var n = 0; parent.HasValue && n <= 32; n++)
        {
            if (parent == id) return true;
            parent = await context.Departments.Where(d => d.Id == parent).Select(d => (ulong?)d.ParentId!).SingleOrDefaultAsync(ct);
        }
        return parent.HasValue;
    }

    private static async Task<int> MaxDepthAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var children = await context.Departments.Where(d => d.ParentId == id).Select(d => d.Id).ToArrayAsync(ct);
        var max = 0;
        foreach (var child in children) max = Math.Max(max, 1 + await MaxDepthAsync(context, child, ct));
        return max;
    }

    private static async Task RecomputeAsync(YfDbContext context, ulong id, string kind, CancellationToken ct)
    {
        var childKind = kind switch { "DIVISION" => "DEPARTMENT", "DEPARTMENT" => "SECTION", _ => null };
        var children = await context.Departments.Where(d => d.ParentId == id).Select(d => d.Id).ToArrayAsync(ct);
        if (children.Length > 0 && childKind is null) throw ApiException.BadRequest("课别下不能再有下级，超出 事业部 > 部门 > 课别");
        foreach (var child in children)
        {
            await context.Departments.Where(d => d.Id == child).ExecuteUpdateAsync(s => s.SetProperty(d => d.Kind, childKind!), ct);
            await RecomputeAsync(context, child, childKind!, ct);
        }
    }

    private static async Task<ulong[]> SubtreeIdsAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var result = new List<ulong> { id };
        var visited = new HashSet<ulong> { id };
        var frontier = new[] { id };
        while (frontier.Length > 0)
        {
            var children = await context.Departments.Where(d => d.ParentId != null && Enumerable.Contains(frontier, d.ParentId!.Value))
                .OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync(ct);
            frontier = children.Where(visited.Add).ToArray();
            result.AddRange(frontier);
            if (result.Count > 100_000) throw ApiException.BadRequest("组织层级无效");
        }
        return result.ToArray();
    }

    private static IQueryable<ulong> ValidSectionIds(YfDbContext context) =>
        context.Departments.Where(section => section.Kind == "SECTION" && section.Status == "ACTIVE"
            && (section.ParentId == null || context.Departments.Any(parent => parent.Id == section.ParentId
                && parent.Kind == "DEPARTMENT" && parent.Status == "ACTIVE"
                && (parent.ParentId == null || context.Departments.Any(root => root.Id == parent.ParentId
                    && root.Kind == "DIVISION" && root.Status == "ACTIVE" && root.ParentId == null)))))
            .Select(section => section.Id);

    private static async Task<ulong[]> ActiveProjectGroupsWithValidOwnerInSubtreeAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var departmentIds = await SubtreeIdsAsync(context, id, ct);
        var validSections = ValidSectionIds(context);
        return await (from g in context.ProjectGroups
                      join u in context.Users on g.ResponsibleUserId equals (ulong?)u.Id
                      where (g.Status == "DRAFT" || g.Status == "IN_PROGRESS")
                          && u.DepartmentId != null
                          && Enumerable.Contains(departmentIds, u.DepartmentId.Value)
                          && validSections.Contains(u.DepartmentId.Value)
                      select g.Id).Distinct().OrderBy(id => id).ToArrayAsync(ct);
    }

    private static async Task EnsureProjectOwnerSectionsRemainActiveAsync(YfDbContext context, ulong[] projectGroupIds, CancellationToken ct)
    {
        if (projectGroupIds.Length == 0) return;
        var validSections = ValidSectionIds(context);
        var count = await (from g in context.ProjectGroups
                           join u in context.Users on g.ResponsibleUserId equals (ulong?)u.Id
                           join section in context.Departments on u.DepartmentId equals (ulong?)section.Id
                           where Enumerable.Contains(projectGroupIds, g.Id) && !validSections.Contains(section.Id)
                           select g.Id).Distinct().CountAsync(ct);
        if (count > 0)
            throw ApiException.BadRequest($"组织调整会使 {count} 个未结束主项目的负责人失去有效课别，请先转交负责人");
    }
}
