using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectDictionaryTypes
{
    internal const string RobotVendor = "ROBOT_VENDOR";
    internal const string RobotModel = "ROBOT_MODEL";
    internal const string Priority = "PRIORITY";

    internal static string Normalize(string? value)
    {
        var type = (value ?? string.Empty).Trim().ToUpperInvariant();
        return type is RobotVendor or RobotModel or Priority
            ? type
            : throw ApiException.BadRequest("type 必须为 ROBOT_VENDOR、ROBOT_MODEL 或 PRIORITY");
    }
}

internal sealed class ProjectDictionaryService(AuditService audit)
{
    internal async Task<object> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        string? requestedType,
        bool enabledOnly,
        ulong? parentId,
        CancellationToken ct)
    {
        var type = ProjectDictionaryTypes.Normalize(requestedType);
        if (type != ProjectDictionaryTypes.RobotModel && parentId is not null)
            throw ApiException.BadRequest("仅 ROBOT_MODEL 支持 parentId 筛选");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await RequireReadAsync(conn, tx, current, ct);
        var rows = await conn.QueryAsync<DictionaryRow>(new CommandDefinition(
            """
            SELECT d.id AS Id,d.type AS Type,d.code AS Code,d.name AS Name,d.parent_id AS ParentId,
                   parent.name AS ParentName,d.sort_no AS SortNo,d.status AS Status,
                   EXISTS(SELECT 1 FROM projects p WHERE p.robot_vendor_id=d.id OR p.robot_model_id=d.id OR p.priority_id=d.id) AS ProjectInUse,
                   EXISTS(SELECT 1 FROM project_dictionaries child WHERE child.parent_id=d.id) AS HasChildren
            FROM project_dictionaries d
            LEFT JOIN project_dictionaries parent ON parent.id=d.parent_id
            WHERE d.type=@Type AND (@EnabledOnly=0 OR d.status='ACTIVE')
              AND (@ParentId IS NULL OR d.parent_id=@ParentId)
            ORDER BY d.sort_no,d.id
            """,
            new { Type = type, EnabledOnly = enabledOnly, ParentId = parentId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows.Select(Json).ToArray();
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn, CurrentUser actor, ProjectDictionaryUpsertRequest request, string? ip, CancellationToken ct)
    {
        var input = Normalize(request, null);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        await ValidateParentAsync(conn, tx, input.Type, input.ParentId, null, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_dictionaries(type,code,name,parent_id,sort_no,status,created_at,updated_at)
                VALUES(@Type,@Code,@Name,@ParentId,@SortNo,@Status,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
                """, input, tx, cancellationToken: ct));
        }
        catch (MySqlException error) when (error.Number == 1062)
        {
            throw ApiException.Conflict("同类型字典编码已存在");
        }
        var id = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_CREATE", "project_dictionary", id,
            new { input.Type, input.Code, input.Name, input.ParentId, input.SortNo, input.Status }, ip, ct);
        var result = Json(await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound());
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> UpdateAsync(
        MySqlConnection conn, CurrentUser actor, ulong id, ProjectDictionaryUpsertRequest request, string? ip, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        var before = await FindAsync(conn, tx, id, ct, true) ?? throw ApiException.NotFound();
        var input = Normalize(request, before.Type);
        if (input.Type != before.Type) throw ApiException.BadRequest("字典 type 创建后不可修改");
        EnsureReferencedModelParentUnchanged(before.Type, before.ProjectInUse, before.ParentId, input.ParentId);
        await ValidateParentAsync(conn, tx, input.Type, input.ParentId, id, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE project_dictionaries
                SET code=@Code,name=@Name,parent_id=@ParentId,sort_no=@SortNo,status=@Status,updated_at=UTC_TIMESTAMP(3)
                WHERE id=@Id
                """, new { Id = id, input.Code, input.Name, input.ParentId, input.SortNo, input.Status }, tx, cancellationToken: ct));
        }
        catch (MySqlException error) when (error.Number == 1062)
        {
            throw ApiException.Conflict("同类型字典编码已存在");
        }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_UPDATE", "project_dictionary", id,
            new { changes = AuditChange.OnlyChanged(
                new("code", "编码", before.Code, input.Code),
                new("name", "名称", before.Name, input.Name),
                new("parentId", "上级厂商", before.ParentId, input.ParentId),
                new("sortNo", "排序", before.SortNo, input.SortNo),
                new("enabled", "启用", before.Status == "ACTIVE", input.Status == "ACTIVE")) }, ip, ct);
        var result = Json(await FindAsync(conn, tx, id, ct) ?? throw ApiException.NotFound());
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task DeleteAsync(MySqlConnection conn, CurrentUser actor, ulong id, string? ip, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await RequireWriteAsync(conn, tx, actor, ct);
        var row = await FindAsync(conn, tx, id, ct, true) ?? throw ApiException.NotFound();
        if (row.ProjectInUse) throw ApiException.Conflict("字典项已被项目引用，可停用但不能删除");
        if (row.HasChildren) throw ApiException.Conflict("机器人厂商仍有关联型号，可停用但不能删除");
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM project_dictionaries WHERE id=@Id", new { Id = id }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DICTIONARY_DELETE", "project_dictionary", id,
            new { row.Type, row.Code, row.Name }, ip, ct);
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
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (request.Name ?? string.Empty).Trim();
        if (code.Length is < 1 or > 64 || code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw ApiException.BadRequest("code 仅允许 1-64 位字母、数字、下划线或短横线");
        if (name.Length is < 1 or > 128) throw ApiException.BadRequest("name 长度必须为 1-128 个字符");
        if (request.SortNo is < 0 or > 100000) throw ApiException.BadRequest("sortNo 必须为 0-100000");
        if (type == ProjectDictionaryTypes.RobotModel && request.ParentId is null)
            throw ApiException.BadRequest("ROBOT_MODEL 必须指定机器人厂商 parentId");
        if (type != ProjectDictionaryTypes.RobotModel && request.ParentId is not null)
            throw ApiException.BadRequest("仅 ROBOT_MODEL 可以指定 parentId");
        return new(type, code, name, request.ParentId, request.SortNo, request.Enabled ? "ACTIVE" : "DISABLED");
    }

    private static async Task ValidateParentAsync(
        MySqlConnection conn, MySqlTransaction tx, string type, ulong? parentId, ulong? selfId, CancellationToken ct)
    {
        if (type != ProjectDictionaryTypes.RobotModel) return;
        if (parentId == selfId) throw ApiException.BadRequest("字典项不能以自身作为 parentId");
        var parentType = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT type FROM project_dictionaries WHERE id=@ParentId", new { ParentId = parentId }, tx, cancellationToken: ct));
        if (parentType is null) throw ApiException.BadRequest("机器人厂商不存在");
        if (parentType != ProjectDictionaryTypes.RobotVendor) throw ApiException.BadRequest("ROBOT_MODEL 的 parentId 必须指向 ROBOT_VENDOR");
    }

    internal static void EnsureReferencedModelParentUnchanged(
        string type, bool projectInUse, ulong? beforeParentId, ulong? afterParentId)
    {
        if (type == ProjectDictionaryTypes.RobotModel && projectInUse && beforeParentId != afterParentId)
            throw ApiException.Conflict("机器人型号已被项目引用，不能更换所属厂商");
    }

    private static async Task<DictionaryRow?> FindAsync(
        MySqlConnection conn, MySqlTransaction tx, ulong id, CancellationToken ct, bool forUpdate = false)
    {
        var sql = """
            SELECT d.id AS Id,d.type AS Type,d.code AS Code,d.name AS Name,d.parent_id AS ParentId,
                   parent.name AS ParentName,d.sort_no AS SortNo,d.status AS Status,
                   EXISTS(SELECT 1 FROM projects p WHERE p.robot_vendor_id=d.id OR p.robot_model_id=d.id OR p.priority_id=d.id) AS ProjectInUse,
                   EXISTS(SELECT 1 FROM project_dictionaries child WHERE child.parent_id=d.id) AS HasChildren
            FROM project_dictionaries d LEFT JOIN project_dictionaries parent ON parent.id=d.parent_id
            WHERE d.id=@Id
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);
        return await conn.QuerySingleOrDefaultAsync<DictionaryRow>(new CommandDefinition(sql, new { Id = id }, tx, cancellationToken: ct));
    }

    private static object Json(DictionaryRow row) => new
    {
        id = row.Id,
        type = row.Type,
        code = row.Code,
        name = row.Name,
        parentId = row.ParentId,
        parentName = row.ParentName,
        sortNo = row.SortNo,
        enabled = row.Status == "ACTIVE",
        inUse = row.ProjectInUse || row.HasChildren,
    };

    private sealed record DictionaryInput(string Type, string Code, string Name, ulong? ParentId, int SortNo, string Status);
    private sealed class DictionaryRow
    {
        public ulong Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ulong? ParentId { get; init; }
        public string? ParentName { get; init; }
        public int SortNo { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool ProjectInUse { get; init; }
        public bool HasChildren { get; init; }
    }
}
