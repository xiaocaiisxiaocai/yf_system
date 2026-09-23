using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Text;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class RobotPartService(AuditService audit)
{
    internal async Task<RobotPartSupplierOption[]> SupplierOptionsAsync(
        MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "config:manage", ct);
        await using var db = EfDb.Use(conn, tx);
        var result = await db.Suppliers.AsNoTracking().OrderBy(supplier => supplier.Id)
            .Select(supplier => new RobotPartSupplierOption(supplier.Id, supplier.Name, supplier.Status))
            .ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<RobotPartResponse[]> ListAsync(
        MySqlConnection conn, CurrentUser actor, ulong? supplierId, bool enabledOnly, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectDictionaryService.RequireReadAsync(conn, tx, current, ct);
        await using var db = EfDb.Use(conn, tx);
        var query = db.RobotParts.AsNoTracking();
        if (supplierId is not null) query = query.Where(part => part.SupplierId == supplierId.Value);
        if (enabledOnly) query = query.Where(part => part.Status == AccountStatuses.Active
            && db.Suppliers.Any(supplier => supplier.Id == part.SupplierId && supplier.Status == AccountStatuses.Active));
        var result = await Rows(db, query).OrderBy(row => row.SortNo).ThenBy(row => row.Id)
            .Select(row => new RobotPartResponse(row.Id, row.SupplierId, row.SupplierName, row.PartNumber,
                row.Model, row.SortNo, row.Status == AccountStatuses.Active, row.InUse))
            .ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<RobotPartResponse> CreateAsync(
        MySqlConnection conn, CurrentUser actor, RobotPartUpsertRequest request, string? ip, CancellationToken ct)
    {
        var input = Normalize(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await ProjectDictionaryService.RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        await RequireSupplierAsync(db, input.SupplierId, ct);
        var entity = new RobotPart
        {
            SupplierId = input.SupplierId,
            PartNumber = input.PartNumber,
            Model = input.Model,
            SortNo = input.SortNo,
            Status = input.Status,
        };
        db.RobotParts.Add(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("该供应商下的 Robot 料号已存在"); }
        await audit.WriteAsync(conn, tx, current.Id, "ROBOT_PART_CREATE", "robot_part", entity.Id,
            new { input.SupplierId, input.PartNumber, input.Model, input.SortNo, input.Status }, ip, ct);
        var result = await FindResponseAsync(db, entity.Id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<RobotPartResponse> UpdateAsync(
        MySqlConnection conn, CurrentUser actor, ulong id, RobotPartUpsertRequest request, string? ip, CancellationToken ct)
    {
        var input = Normalize(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await ProjectDictionaryService.RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var before = await LockAsync(db, id, ct) ?? throw ApiException.NotFound();
        await RequireSupplierAsync(db, input.SupplierId, ct);
        var inUse = await InUseAsync(db, id, ct);
        if (inUse && (before.SupplierId != input.SupplierId || before.PartNumber != input.PartNumber
                || before.Model != input.Model))
            throw ApiException.Conflict("Robot 料号已被项目引用，不能更换供应商、料号或型号");
        try
        {
            await db.RobotParts.Where(part => part.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(part => part.SupplierId, input.SupplierId)
                .SetProperty(part => part.PartNumber, input.PartNumber)
                .SetProperty(part => part.Model, input.Model)
                .SetProperty(part => part.SortNo, input.SortNo)
                .SetProperty(part => part.Status, input.Status), ct);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("该供应商下的 Robot 料号已存在"); }
        await audit.WriteAsync(conn, tx, current.Id, "ROBOT_PART_UPDATE", "robot_part", id,
            new { changes = AuditChange.OnlyChanged(
                new("supplierId", "供应商", before.SupplierId, input.SupplierId),
                new("partNumber", "料号", before.PartNumber, input.PartNumber),
                new("model", "型号", before.Model, input.Model),
                new("sortNo", "排序", before.SortNo, input.SortNo),
                new("enabled", "启用", before.Status == AccountStatuses.Active, input.Status == AccountStatuses.Active)) }, ip, ct);
        var result = await FindResponseAsync(db, id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task DeleteAsync(
        MySqlConnection conn, CurrentUser actor, ulong id, string? ip, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await ProjectDictionaryService.RequireWriteAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var before = await LockAsync(db, id, ct) ?? throw ApiException.NotFound();
        if (await InUseAsync(db, id, ct))
            throw ApiException.Conflict("Robot 料号已被项目引用，可停用但不能删除");
        if (await db.RobotParts.Where(part => part.Id == id).ExecuteDeleteAsync(ct) != 1)
            throw ApiException.NotFound();
        await audit.WriteAsync(conn, tx, current.Id, "ROBOT_PART_DELETE", "robot_part", id,
            new { before.SupplierId, before.PartNumber, before.Model, targetName = before.PartNumber }, ip, ct);
        await tx.CommitAsync(ct);
    }

    private static IQueryable<RobotPartRow> Rows(YfDbContext db, IQueryable<RobotPart> query) =>
        query.Select(part => new RobotPartRow
        {
            Id = part.Id,
            SupplierId = part.SupplierId,
            SupplierName = db.Suppliers.Where(supplier => supplier.Id == part.SupplierId)
                .Select(supplier => supplier.Name).FirstOrDefault() ?? "",
            PartNumber = part.PartNumber,
            Model = part.Model,
            SortNo = part.SortNo,
            Status = part.Status,
            InUse = db.Projects.Any(project => project.RobotPartId == part.Id)
                || db.ProjectGroups.Any(group => group.RobotPartId == part.Id),
        });

    private static async Task<RobotPartResponse> FindResponseAsync(YfDbContext db, ulong id, CancellationToken ct) =>
        await Rows(db, db.RobotParts.Where(part => part.Id == id))
            .Select(row => new RobotPartResponse(row.Id, row.SupplierId, row.SupplierName, row.PartNumber,
                row.Model, row.SortNo, row.Status == AccountStatuses.Active, row.InUse))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static async Task<RobotPart?> LockAsync(YfDbContext db, ulong id, CancellationToken ct) =>
        await db.RobotParts.FromSqlInterpolated($"SELECT * FROM robot_parts WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);

    private static async Task<bool> InUseAsync(YfDbContext db, ulong id, CancellationToken ct) =>
        await db.Projects.AnyAsync(project => project.RobotPartId == id, ct)
            || await db.ProjectGroups.AnyAsync(group => group.RobotPartId == id, ct);

    private static async Task RequireSupplierAsync(YfDbContext db, ulong supplierId, CancellationToken ct)
    {
        if (supplierId == 0 || !await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId, ct))
            throw ApiException.BadRequest("供应商不存在");
    }

    private static RobotPartInput Normalize(RobotPartUpsertRequest request)
    {
        var partNumber = (request.PartNumber ?? string.Empty).Trim();
        var model = (request.Model ?? string.Empty).Trim();
        if (request.SupplierId == 0) throw ApiException.BadRequest("请选择供应商");
        if (partNumber.EnumerateRunes().Count() is < 1 or > 128)
            throw ApiException.BadRequest("partNumber 长度必须为 1-128 个字符");
        if (model.EnumerateRunes().Count() is < 1 or > 512)
            throw ApiException.BadRequest("model 长度必须为 1-512 个字符");
        if (request.SortNo is < 0 or > 100000) throw ApiException.BadRequest("sortNo 必须为 0-100000");
        return new(request.SupplierId, partNumber, model, request.SortNo,
            request.Enabled ? AccountStatuses.Active : AccountStatuses.Disabled);
    }

    private sealed record RobotPartInput(ulong SupplierId, string PartNumber, string Model, int SortNo, string Status);

    private sealed class RobotPartRow
    {
        public ulong Id { get; init; }
        public ulong SupplierId { get; init; }
        public string SupplierName { get; init; } = string.Empty;
        public string PartNumber { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public int SortNo { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool InUse { get; init; }
    }
}
