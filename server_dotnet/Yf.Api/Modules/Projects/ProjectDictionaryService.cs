using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal static class ProjectDictionaryTypes
{
    internal const string RobotVendor = "ROBOT_VENDOR";
    internal const string RobotModel = "ROBOT_MODEL";
    internal const string Priority = "PRIORITY";

    internal static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized is not (RobotVendor or RobotModel or Priority))
            throw ApiException.BadRequest("type 仅支持 ROBOT_VENDOR、ROBOT_MODEL、PRIORITY");
        return normalized;
    }
}

internal sealed class ProjectDictionaryService(AuditService audit)
{
    internal async Task<object> ListAsync(
        MySqlConnection conn, CurrentUser actor, string? rawType, bool enabledOnly, ulong? parentId, CancellationToken ct)
    {
        var type = ProjectDictionaryTypes.Normalize(rawType);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await RequireReadAsync(conn, tx, current, ct);
        await using var db = EfDb.Use(conn, tx);
        var query = db.ProjectDictionaries.Where(item => item.Type == type);
        if (enabledOnly) query = query.Where(item => item.Status == "ACTIVE");
        if (parentId is not null) query = query.Where(item => item.ParentId == parentId);
        var rows = await Rows(db, query).OrderBy(row => row.SortNo).ThenBy(row => row.Id).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(Json).ToArray();
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn, CurrentUser actor, ProjectDictionaryUpsertRequest request, string? ip, CancellationToken ct)
    {
        var input = Normalize(request, null);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        await ValidateParentAsync(db, input.Type, input.ParentId, null, ct);
        var entity = new ProjectDictionary
        {
            Type = input.Type,
            Name = input.Name,
            ParentId = input.ParentId,
            SortNo = input.SortNo,
            Status = input.Status,
        };
        db.ProjectDictionaries.Add(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("同类型字典名称已存在"); }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_CREATE", "project_dictionary", entity.Id,
            new { input.Type, input.Name, input.ParentId, input.SortNo, input.Status }, ip, ct);
        var result = Json(await FindAsync(db, entity.Id, ct) ?? throw ApiException.NotFound());
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> UpdateAsync(
        MySqlConnection conn, CurrentUser actor, ulong id, ProjectDictionaryUpsertRequest request, string? ip, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var before = await FindAsync(db, id, ct, true) ?? throw ApiException.NotFound();
        var input = Normalize(request, before.Type);
        if (input.Type != before.Type) throw ApiException.BadRequest("字典 type 创建后不可修改");
        EnsureReferencedModelParentUnchanged(before.Type, before.ProjectInUse, before.ParentId, input.ParentId);
        await ValidateParentAsync(db, input.Type, input.ParentId, id, ct);
        try
        {
            await db.ProjectDictionaries.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Name, input.Name)
                .SetProperty(item => item.ParentId, input.ParentId)
                .SetProperty(item => item.SortNo, input.SortNo)
                .SetProperty(item => item.Status, input.Status), ct);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("同类型字典名称已存在"); }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_UPDATE", "project_dictionary", id,
            new { changes = AuditChange.OnlyChanged(
                new("name", "名称", before.Name, input.Name),
                new("parentId", "上级厂商", before.ParentId, input.ParentId),
                new("sortNo", "排序", before.SortNo, input.SortNo),
                new("enabled", "启用", before.Status == "ACTIVE", input.Status == "ACTIVE")) }, ip, ct);
        var result = Json(await FindAsync(db, id, ct) ?? throw ApiException.NotFound());
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task DeleteAsync(MySqlConnection conn, CurrentUser actor, ulong id, string? ip, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var row = await FindAsync(db, id, ct, true) ?? throw ApiException.NotFound();
        if (row.ProjectInUse) throw ApiException.Conflict("字典项已被项目引用，可停用但不能删除");
        if (row.HasChildren) throw ApiException.Conflict("机器人厂商仍有关联型号，可停用但不能删除");
        var deleted = await db.ProjectDictionaries.Where(item => item.Id == id).ExecuteDeleteAsync(ct);
        if (deleted != 1) throw ApiException.NotFound();
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_DELETE", "project_dictionary", id,
            new { row.Type, row.Name }, ip, ct);
        await tx.CommitAsync(ct);
    }

    internal static async Task RequireOptionReadAsync(
        MySqlConnection conn, MySqlTransaction tx, CurrentUser current, CancellationToken ct)
    {
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var allowed = await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:create", ct)
            || await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:update", ct);
        if (!allowed) throw ApiException.Forbidden();
    }

    private static async Task RequireReadAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser current, CancellationToken ct)
    {
        if (await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "config:manage", ct)) return;
        await RequireOptionReadAsync(conn, tx, current, ct);
    }

    private static async Task<CurrentUser> RequireWriteAsync(
        MySqlConnection conn, MySqlTransaction tx, CurrentUser actor, CancellationToken ct)
    {
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "config:manage", ct);
        return current;
    }

    private static DictionaryInput Normalize(ProjectDictionaryUpsertRequest request, string? existingType)
    {
        var type = ProjectDictionaryTypes.Normalize(request.Type ?? existingType);
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 128) throw ApiException.BadRequest("name 长度必须为 1-128 个字符");
        if (request.SortNo is < 0 or > 100000) throw ApiException.BadRequest("sortNo 必须为 0-100000");
        if (type == ProjectDictionaryTypes.RobotModel && request.ParentId is null)
            throw ApiException.BadRequest("ROBOT_MODEL 必须指定机器人厂商 parentId");
        if (type != ProjectDictionaryTypes.RobotModel && request.ParentId is not null)
            throw ApiException.BadRequest("仅 ROBOT_MODEL 可以指定 parentId");
        return new(type, name, request.ParentId, request.SortNo, request.Enabled ? "ACTIVE" : "DISABLED");
    }

    private static async Task ValidateParentAsync(
        YfDbContext db, string type, ulong? parentId, ulong? selfId, CancellationToken ct)
    {
        if (type != ProjectDictionaryTypes.RobotModel) return;
        if (parentId == selfId) throw ApiException.BadRequest("字典项不能以自身作为 parentId");
        var parentType = await db.ProjectDictionaries.Where(item => item.Id == parentId)
            .Select(item => item.Type).SingleOrDefaultAsync(ct);
        if (parentType is null) throw ApiException.BadRequest("机器人厂商不存在");
        if (parentType != ProjectDictionaryTypes.RobotVendor)
            throw ApiException.BadRequest("ROBOT_MODEL 的 parentId 必须指向 ROBOT_VENDOR");
    }

    internal static void EnsureReferencedModelParentUnchanged(
        string type, bool projectInUse, ulong? beforeParentId, ulong? afterParentId)
    {
        if (type == ProjectDictionaryTypes.RobotModel && projectInUse && beforeParentId != afterParentId)
            throw ApiException.Conflict("机器人型号已被项目引用，不能更换所属厂商");
    }

    private static IQueryable<DictionaryRow> Rows(YfDbContext db, IQueryable<ProjectDictionary> query) =>
        query.Select(item => new DictionaryRow
        {
            Id = item.Id,
            Type = item.Type,
            Name = item.Name,
            ParentId = item.ParentId,
            ParentName = db.ProjectDictionaries.Where(parent => parent.Id == item.ParentId)
                .Select(parent => parent.Name).FirstOrDefault(),
            SortNo = item.SortNo,
            Status = item.Status,
            ProjectInUse = db.Projects.Any(project => project.RobotVendorId == item.Id
                    || project.RobotModelId == item.Id || project.PriorityId == item.Id)
                || db.ProjectGroups.Any(group => group.RobotVendorId == item.Id
                    || group.RobotModelId == item.Id || group.PriorityId == item.Id),
            HasChildren = db.ProjectDictionaries.Any(child => child.ParentId == item.Id),
        });

    private static async Task<DictionaryRow?> FindAsync(
        YfDbContext db, ulong id, CancellationToken ct, bool forUpdate = false)
    {
        if (forUpdate)
        {
            var locked = await db.ProjectDictionaries
                .FromSqlInterpolated($"SELECT * FROM project_dictionaries WHERE id={id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (locked is null) return null;
        }
        return await Rows(db, db.ProjectDictionaries.Where(item => item.Id == id)).SingleOrDefaultAsync(ct);
    }

    private static object Json(DictionaryRow row) => new
    {
        id = row.Id,
        type = row.Type,
        name = row.Name,
        parentId = row.ParentId,
        parentName = row.ParentName,
        sortNo = row.SortNo,
        enabled = row.Status == "ACTIVE",
        inUse = row.ProjectInUse || row.HasChildren,
    };

    private sealed record DictionaryInput(string Type, string Name, ulong? ParentId, int SortNo, string Status);
    private sealed class DictionaryRow
    {
        public ulong Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ulong? ParentId { get; init; }
        public string? ParentName { get; init; }
        public int SortNo { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool ProjectInUse { get; init; }
        public bool HasChildren { get; init; }
    }
}
