using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Admin;

public sealed class DepartmentService(AppDb db, AuditService audit)
{
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
        var parentKind = request.ParentId is ulong parentId ? (await FindAsync(conn, tx, parentId, ct) ?? throw ApiException.BadRequest("上级组织不存在")).Kind : null;
        var kind = parentKind switch { null => "DIVISION", "DIVISION" => "DEPARTMENT", "DEPARTMENT" => "SECTION", _ => throw ApiException.BadRequest("课别下不能再新增下级，组织层级为 事业部 > 部门 > 课别") };
        if (id.HasValue && await MaxDepthAsync(conn, tx, id.Value, ct) > (kind switch { "DIVISION" => 2, "DEPARTMENT" => 1, _ => 0 })) throw ApiException.BadRequest("超出 事业部 > 部门 > 课别 三级");
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
            await RecomputeAsync(conn, tx, resultId, kind, ct);
        }
        await audit.WriteAsync(conn, tx, actor.Id, id is null ? "DEPT_CREATE" : "DEPT_UPDATE", "department", resultId, new { name = request.Name.Trim(), kind, parentId = request.ParentId, sortNo = request.SortNo ?? existing?.SortNo ?? 0 }, null, ct);
        var row = await FindAsync(conn, tx, resultId, ct) ?? throw ApiException.NotFound();
        var result = new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, row.Status };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<object> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status); await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct); actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:manage", ct);
        var row = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE departments SET status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { status, id }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, actor.Id, "DEPT_STATUS", "department", id, new { row.Name, row.Kind, oldStatus = row.Status, newStatus = status }, null, ct); await tx.CommitAsync(ct);
        return new { row.Id, row.Name, row.ParentId, row.Kind, row.SortNo, Status = status };
    }

    public async Task DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct); await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct); actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct); AccessService.RequireInternal(actor); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:manage", ct); await AccessService.RequirePermissionAsync(conn, tx, actor, "dept:delete", ct);
        var row = await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound();
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM departments WHERE parent_id=@id)", new { id }, tx, cancellationToken: ct)) == 1) throw ApiException.BadRequest("请先删除下级组织节点");
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM users WHERE department_id=@id)", new { id }, tx, cancellationToken: ct)) == 1) throw ApiException.BadRequest("该组织仍有用户，请先调整用户归属或禁用组织");
        await audit.WriteAsync(conn, tx, actor.Id, "DEPT_DELETE", "department", id, new { row.Name, row.Kind }, null, ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM departments WHERE id=@id", new { id }, tx, cancellationToken: ct)); await tx.CommitAsync(ct);
    }

    internal static async Task EnsureActiveAsync(MySqlConnection conn, MySqlTransaction tx, ulong id, CancellationToken ct)
    {
        var status = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT status FROM departments WHERE id=@id", new { id }, tx, cancellationToken: ct));
        if (status is null) throw ApiException.BadRequest("组织不存在"); if (status != "ACTIVE") throw ApiException.BadRequest("组织已被禁用");
    }
    private static void Validate(DepartmentUpsert r) { if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().EnumerateRunes().Count() > 64) throw ApiException.BadRequest("组织名称需为 1~64 个字符"); if (r.SortNo < 0) throw ApiException.BadRequest("排序号不能为负数"); }
    private static Task<DeptRow?> FindAsync(MySqlConnection c, MySqlTransaction? t, ulong id, CancellationToken ct) => c.QuerySingleOrDefaultAsync<DeptRow>(new CommandDefinition("SELECT id Id,name Name,parent_id ParentId,kind Kind,sort_no SortNo,status Status FROM departments WHERE id=@id", new { id }, t, cancellationToken: ct));
    private static async Task<bool> WouldCycleAsync(MySqlConnection c, MySqlTransaction t, ulong id, ulong? parent, CancellationToken ct) { for (var n = 0; parent.HasValue && n <= 32; n++) { if (parent == id) return true; parent = await c.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition("SELECT parent_id FROM departments WHERE id=@parent", new { parent }, t, cancellationToken: ct)); } return parent.HasValue; }
    private static async Task<int> MaxDepthAsync(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken ct) { var children = (await c.QueryAsync<ulong>(new CommandDefinition("SELECT id FROM departments WHERE parent_id=@id", new { id }, t, cancellationToken: ct))).ToArray(); var max = 0; foreach (var child in children) max = Math.Max(max, 1 + await MaxDepthAsync(c, t, child, ct)); return max; }
    private static async Task RecomputeAsync(MySqlConnection c, MySqlTransaction t, ulong id, string kind, CancellationToken ct) { var childKind = kind switch { "DIVISION" => "DEPARTMENT", "DEPARTMENT" => "SECTION", _ => null }; var children = (await c.QueryAsync<ulong>(new CommandDefinition("SELECT id FROM departments WHERE parent_id=@id", new { id }, t, cancellationToken: ct))).ToArray(); if (children.Length > 0 && childKind is null) throw ApiException.BadRequest("课别下不能再有下级，超出 事业部 > 部门 > 课别"); foreach (var child in children) { await c.ExecuteAsync(new CommandDefinition("UPDATE departments SET kind=@childKind WHERE id=@child", new { childKind, child }, t, cancellationToken: ct)); await RecomputeAsync(c, t, child, childKind!, ct); } }
}
