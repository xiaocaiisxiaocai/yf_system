using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Admin;

public sealed class DepartmentService(AppDb db, AuditService audit)
{
    private const string ActiveSectionCondition = """
        section.kind='SECTION' AND section.status='ACTIVE'
        AND (section.parent_id IS NULL OR (
          parent_department.id IS NOT NULL
          AND parent_department.kind='DEPARTMENT'
          AND parent_department.status='ACTIVE'
          AND (parent_department.parent_id IS NULL OR (
            root_department.id IS NOT NULL
            AND root_department.kind='DIVISION'
            AND root_department.status='ACTIVE'
            AND root_department.parent_id IS NULL))))
        """;

    public async Task<object> ListAsync(CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var conn = await db.OpenAsync(ct);
        var all = (await conn.QueryAsync<DeptRow>(new CommandDefinition("SELECT id Id,name Name,parent_id ParentId,kind Kind,sort_no SortNo,status Status FROM departments ORDER BY sort_no,id", cancellationToken: ct))).AsList();
        List<object> Build(ulong? parent) => all.Where(x => x.ParentId == parent).Select(x => (object)new { x.Id, x.Name, x.ParentId, x.Kind, x.SortNo, x.Status, children = Build(x.Id) }).ToList();
        return Build(null);
    }

    public Task<object> CreateAsync(CurrentUser actor, DepartmentUpsert request, CancellationToken ct) => WriteAsync(actor, null, request, ct);
    public Task<object> UpdateAsync(CurrentUser actor, ulong id, DepartmentUpsert request, CancellationToken ct) => WriteAsync(actor, id, request, ct);

    private async Task<object> WriteAsync(CurrentUser actor, ulong? id, DepartmentUpsert request, CancellationToken ct)
    {
        Validate(request);
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct); actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:manage", ct);
        DeptRow? existing = null;
        if (id.HasValue) existing = await FindAsync(conn, tx, id.Value, ct) ?? throw ApiException.NotFound();
        if (id.HasValue && await WouldCycleAsync(conn, tx, id.Value, request.ParentId, ct)) throw ApiException.BadRequest("不能将组织移动到自身或其下级下");
        var newParent = request.ParentId is ulong parentId ? await FindAsync(conn, tx, parentId, ct) ?? throw ApiException.BadRequest("上级组织不存在") : null;
        var oldParent = existing?.ParentId is ulong oldParentId ? await FindAsync(conn, tx, oldParentId, ct) : null;
        var parentChanged = existing is not null && existing.ParentId != request.ParentId;
        var protectedProjectGroupIds = parentChanged
            ? await ActiveProjectGroupsWithValidOwnerInSubtreeAsync(conn, tx, id!.Value, ct)
            : Array.Empty<ulong>();
        var kind = existing is not null && !parentChanged
            ? existing.Kind
            : newParent?.Kind switch
            {
                null => "DIVISION",
                "DIVISION" => "DEPARTMENT",
                "DEPARTMENT" => "SECTION",
                _ => throw ApiException.BadRequest("课别下不能再新增下级，组织层级为 事业部 > 部门 > 课别")
            };
        if (parentChanged && await MaxDepthAsync(conn, tx, id!.Value, ct) > (kind switch { "DIVISION" => 2, "DEPARTMENT" => 1, _ => 0 })) throw ApiException.BadRequest("超出 事业部 > 部门 > 课别 三级");
        var duplicate = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM departments WHERE name=@name AND kind=@kind AND parent_id<=>@parentId AND (@id IS NULL OR id<>@id))", new { name = request.Name.Trim(), kind, parentId = request.ParentId, id }, tx, cancellationToken: ct));
        if (duplicate == 1) throw ApiException.Conflict("同一上级和层级下组织名称已存在");
        ulong resultId;
        if (id is null)
        {
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO departments(name,parent_id,kind,sort_no,status,created_at,updated_at) VALUES(@name,@parentId,@kind,@sortNo,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))", new { name = request.Name.Trim(), parentId = request.ParentId, kind, sortNo = request.SortNo ?? 0 }, tx, cancellationToken: ct));
            resultId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        }
        else
        {
            resultId = id.Value;
            await conn.ExecuteAsync(new CommandDefinition("UPDATE departments SET name=@name,parent_id=@parentId,kind=@kind,sort_no=COALESCE(@sortNo,sort_no),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { name = request.Name.Trim(), parentId = request.ParentId, kind, sortNo = request.SortNo, id }, tx, cancellationToken: ct));
            if (parentChanged)
            {
                await RecomputeAsync(conn, tx, resultId, kind, ct);
                await EnsureProjectOwnerSectionsRemainActiveAsync(conn, tx, protectedProjectGroupIds, ct);
            }
        }
        var row = await FindAsync(conn, tx, resultId, ct) ?? throw ApiException.NotFound();
        await audit.WriteAsync(conn, tx, actor.Id, id is null ? "DEPT_CREATE" : "DEPT_UPDATE", "department", resultId, new
        {
            name = row.Name,
            row.Kind,
            parentId = row.ParentId,
            row.SortNo,
            targetName = row.Name,
            oldParentId = existing?.ParentId,
            newParentId = row.ParentId,
            oldParentName = oldParent?.Name,
            newParentName = newParent?.Name,
            changes = AuditChange.OnlyChanged(
                new AuditChange("name", "组织名称", existing?.Name, row.Name),
                new AuditChange("parent", "上级组织", DepartmentAuditJson(oldParent), DepartmentAuditJson(newParent)),
                new AuditChange("kind", "组织层级", existing?.Kind, row.Kind),
                new AuditChange("sortNo", "排序号", existing?.SortNo, row.SortNo),
                new AuditChange("status", "状态", existing?.Status, row.Status))
        }, null, ct);
        var result = new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, row.Status };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status); await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct); actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:manage", ct);
        var row = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        if (status == "DISABLED" && row.Status != "DISABLED")
        {
            var affected = await ActiveProjectGroupsWithValidOwnerInSubtreeAsync(conn, tx, id, ct);
            if (affected.Length > 0)
                throw ApiException.BadRequest($"该组织范围内仍有 {affected.Length} 个未结束主项目负责人，请先转交负责人");
        }
        await conn.ExecuteAsync(new CommandDefinition("UPDATE departments SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, actor.Id, "DEPT_STATUS", "department", id, new
        {
            row.Name,
            row.Kind,
            oldStatus = row.Status,
            newStatus = status,
            targetName = row.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", row.Status, status))
        }, null, ct); await tx.CommitAsync(ct);
        return new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, Status = status };
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct); actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:manage", ct); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:delete", ct);
        var row = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM departments WHERE parent_id=@id)", new { id }, tx, cancellationToken: ct)) == 1) throw ApiException.BadRequest("请先删除下级组织节点");
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM users WHERE department_id=@id)", new { id }, tx, cancellationToken: ct)) == 1) throw ApiException.BadRequest("该组织仍有用户，请先调整用户归属或禁用组织");
        await audit.WriteAsync(conn, tx, actor.Id, "DEPT_DELETE", "department", id, new { row.Name, row.Kind, targetName = row.Name, changes = Array.Empty<AuditChange>() }, null, ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM departments WHERE id=@id", new { id }, tx, cancellationToken: ct)); await tx.CommitAsync(ct);
    }

    internal static async Task EnsureActiveAsync(MySqlConnection conn, MySqlTransaction tx, ulong id, CancellationToken ct)
    {
        ulong? current = id;
        var visited = new HashSet<ulong>();
        while (current is ulong currentId)
        {
            if (!visited.Add(currentId) || visited.Count > 32) throw ApiException.BadRequest("组织层级无效");
            var row = await FindAsync(conn, tx, currentId, ct);
            if (row is null) throw ApiException.BadRequest(currentId == id ? "组织不存在" : "组织层级无效");
            if (row.Status != "ACTIVE") throw ApiException.BadRequest("组织已被禁用");
            current = row.ParentId;
        }
    }
    private static void Validate(DepartmentUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("组织名称需为 1~64 个字符"); if (r.SortNo < 0) throw ApiException.BadRequest("排序号不能为负数"); }
    private static object? DepartmentAuditJson(DeptRow? department) => department is null ? null : new { department.Id, department.Name };
    private static Task<DeptRow?> FindAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<DeptRow>(new CommandDefinition("SELECT id Id,name Name,parent_id ParentId,kind Kind,sort_no SortNo,status Status FROM departments WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static async Task<bool> WouldCycleAsync(MySqlConnection c, MySqlTransaction t, ulong id, ulong? parent, CancellationToken ct) { for (var n = 0; parent.HasValue && n <= 32; n++) { if (parent == id) return true; parent = await c.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition("SELECT parent_id FROM departments WHERE id=@parent", new { parent }, t, cancellationToken: ct)); } return parent.HasValue; }
    private static async Task<int> MaxDepthAsync(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct) { var children = (await c.QueryAsync<ulong>(new CommandDefinition("SELECT id FROM departments WHERE parent_id=@id", new { id }, t, cancellationToken: ct))).ToArray(); var max = 0; foreach (var child in children) max = Math.Max(max, 1 + await MaxDepthAsync(c, t, child, ct)); return max; }
    private static async Task RecomputeAsync(MySqlConnection c, MySqlTransaction t, ulong id, string kind, CancellationToken ct) { var childKind = kind switch { "DIVISION" => "DEPARTMENT", "DEPARTMENT" => "SECTION", _ => null }; var children = (await c.QueryAsync<ulong>(new CommandDefinition("SELECT id FROM departments WHERE parent_id=@id", new { id }, t, cancellationToken: ct))).ToArray(); if (children.Length > 0 && childKind is null) throw ApiException.BadRequest("课别下不能再有下级，超出 事业部 > 部门 > 课别"); foreach (var child in children) { await c.ExecuteAsync(new CommandDefinition("UPDATE departments SET kind=@childKind WHERE id=@child", new { childKind, child }, t, cancellationToken: ct)); await RecomputeAsync(c, t, child, childKind!, ct); } }

    private static async Task<ulong[]> SubtreeIdsAsync(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct)
    {
        var result = new List<ulong> { id };
        var visited = new HashSet<ulong> { id };
        var frontier = new[] { id };
        while (frontier.Length > 0)
        {
            var children = await c.QueryAsync<ulong>(new CommandDefinition(
                "SELECT id FROM departments WHERE parent_id IN @frontier ORDER BY id",
                new { frontier }, t, cancellationToken: ct));
            frontier = children.Where(visited.Add).ToArray();
            result.AddRange(frontier);
            if (result.Count > 100_000) throw ApiException.BadRequest("组织层级无效");
        }
        return result.ToArray();
    }

    private static async Task<ulong[]> ActiveProjectGroupsWithValidOwnerInSubtreeAsync(
        MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct)
    {
        var departmentIds = await SubtreeIdsAsync(c, t, id, ct);
        return (await c.QueryAsync<ulong>(new CommandDefinition(
            $"""
            SELECT DISTINCT g.id
            FROM project_groups g
            JOIN users u ON u.id=g.responsible_user_id
            JOIN departments section ON section.id=u.department_id
            LEFT JOIN departments parent_department ON parent_department.id=section.parent_id
            LEFT JOIN departments root_department ON root_department.id=parent_department.parent_id
            WHERE g.status IN ('DRAFT','IN_PROGRESS')
              AND u.department_id IN @departmentIds
              AND {ActiveSectionCondition}
            ORDER BY g.id
            """, new { departmentIds }, t, cancellationToken: ct))).ToArray();
    }

    private static async Task EnsureProjectOwnerSectionsRemainActiveAsync(
        MySqlConnection c, MySqlTransaction t, ulong[] projectGroupIds, CancellationToken ct)
    {
        if (projectGroupIds.Length == 0) return;
        var count = await c.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"""
            SELECT COUNT(DISTINCT g.id)
            FROM project_groups g
            JOIN users u ON u.id=g.responsible_user_id
            JOIN departments section ON section.id=u.department_id
            LEFT JOIN departments parent_department ON parent_department.id=section.parent_id
            LEFT JOIN departments root_department ON root_department.id=parent_department.parent_id
            WHERE g.id IN @projectGroupIds
              AND NOT(COALESCE(({ActiveSectionCondition}),0))
            """, new { projectGroupIds }, t, cancellationToken: ct));
        if (count > 0)
            throw ApiException.BadRequest($"组织调整会使 {count} 个未结束主项目的负责人失去有效课别，请先转交负责人");
    }
}
