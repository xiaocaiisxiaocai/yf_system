using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Outsource;

/// <summary>One column of the vendor list and export.</summary>
public sealed record OutsourceVendorColumn(string Key, string Title);

/// <summary>One outsourcing record as its vendor sees it, with display values and the handoff state.</summary>
public sealed record OutsourceVendorRow(ulong Id, string Status, bool Closed, bool ElectricalHandoff, bool CanElectricalHandoff,
    IReadOnlyDictionary<string, string?> Values, int FileCount = 0, int PurgedFileCount = 0);

public sealed record OutsourceVendorPageResponse(
    OutsourceVendorColumn[] Columns, IReadOnlyList<OutsourceVendorRow> List, ulong Total, ulong Page, uint PageSize);

public sealed record OutsourceElectricalHandoffRequest(
    [property: JsonRequired] bool Handoff, [property: JsonRequired] bool Expected, string? SignerName);

/// <summary>
/// The outsourcing list and electrical handoff action in the ODM/OEMA portal. Vendors access records assigned to their company.
/// </summary>
public sealed class OutsourceVendorViewService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit, OutsourceMailSignal mailSignal)
{
    internal const string SerialKey = "serialNo";

    public async Task<OutsourceVendorPageResponse> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var (db, reference, items, companyId) = await FilteredAsync(uow, actor, request, ct);
        var total = (ulong)await items.LongCountAsync(ct);
        var rows = await items.OrderBy(x => x.SerialNo).ThenBy(x => x.Id).Skip((int)offset).Take((int)size).ToArrayAsync(ct);
        await reference.LoadValuesAsync(db, rows, ct);
        var counts = await OutsourceFiles.CountsAsync(db, rows.Select(x => x.Id).ToArray(), companyId, ct);
        OutsourceVendorColumn[] columns =
            [new(SerialKey, "序号"), .. reference.Layout.VendorColumns.Select(column => new OutsourceVendorColumn(column.Key, column.Label))];
        return new(columns, rows.Select(item => Row(reference, reference.Json(item), counts.GetValueOrDefault(item.Id))).ToArray(), total, page, size);
    }

    public async Task<OutsourceItemFilesResponse> FilesAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var account = await RequireVendorAsync(uow, actor, ct);
        var db = uow.Db;
        if (!await db.OutsourceItems.AnyAsync(x => x.Id == id && x.VendorId == account.CompanyId, ct))
            throw ApiException.NotFound("外协记录不存在或已被删除");
        return new(true, await OutsourceFiles.ListAsync(db, id, account.CompanyId, ct));
    }

    /// <summary>
    /// The records the list shows with the same keyword and completed filter, all pages
    /// (vendor columns, rows coloured by status, no yellow change marks).
    /// </summary>
    public async Task<OutsourceExport> ExportAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var (db, reference, items, _) = await FilteredAsync(uow, actor, request, ct);
        if (await items.CountAsync(ct) > OutsourceItemService.MaximumExportRows)
            throw ApiException.BadRequest($"一次最多导出 {OutsourceItemService.MaximumExportRows} 条，请缩小筛选范围");
        var rows = await items.OrderBy(x => x.SerialNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        await reference.LoadValuesAsync(db, rows, ct);
        var content = OutsourceWorkbook.Build(rows.Select(reference.Json).ToArray(), null,
            reference.Layout.WorkbookFields(vendor: true).Select(field => field.Key == OutsourceBuiltInFields.ElectricalHandoffKey
                ? field with { Value = item => HandoffAvailable(reference, item) ? field.Value(item) : null }
                : field).ToArray(), forVendor: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new(content, $"外协清单_{today}.xlsx");
    }

    public async Task<OutsourceVendorRow> SetElectricalHandoffAsync(
        OemActor actor, ulong id, OutsourceElectricalHandoffRequest request, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        if (await OemAuthorizer.RecheckAsync(uow, actor, ct) is not OemAccountActor account)
            throw ApiException.Forbidden("仅外发厂商可以操作");

        var db = uow.Db;
        var item = await db.OutsourceItems
            .FromSqlInterpolated($"SELECT * FROM outsource_items WHERE id={id} AND vendor_id={account.CompanyId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("外协记录不存在或已被删除");
        if (item.ClosedAt is not null) throw ApiException.BadRequest("已完成的记录不能操作");
        if (item.ElectricalHandoff != request.Expected)
            throw ApiException.VersionConflict("数据已被他人修改，请刷新后重试");
        if (request.Handoff == request.Expected) throw ApiException.BadRequest("请选择不同的转交状态");

        var reference = await OutsourceReference.LoadAsync(db, ct);
        if (request.Handoff && !reference.AllowsElectricalHandoff(item.TypeCode))
            throw ApiException.BadRequest("该外发类型不支持转迅得电控");

        var signer = request.SignerName?.Trim();
        if (string.IsNullOrEmpty(signer)) throw ApiException.BadRequest("请填写签名（操作人姓名）");
        if (signer.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("签名不能超过 32 个字符");

        var now = await uow.RefreshNowAsync(ct);
        var change = new AuditChange(OutsourceBuiltInFields.ElectricalHandoffKey, OutsourceBuiltInFields.ElectricalHandoffLabel,
            OutsourceBuiltInFields.YesNo(item.ElectricalHandoff), OutsourceBuiltInFields.YesNo(request.Handoff));
        item.ElectricalHandoff = request.Handoff;
        item.ElectricalHandoffAt = now;
        item.ElectricalHandoffSigner = signer;
        OutsourceHistory.Add(db, item.Id, [change], OutsourceHistory.Vendor, null, account.AccountId, signer);
        await db.SaveChangesAsync(ct);

        await reference.LoadValuesAsync(db, [item], ct);
        var updated = reference.Json(item);
        var counts = await OutsourceFiles.CountsAsync(db, [item.Id], account.CompanyId, ct);
        var response = Row(reference, updated, counts.GetValueOrDefault(item.Id));
        var mailQueued = await OutsourceMailService.QueueElectricalHandoffAsync(db, updated, account.AccountId, signer, now, ct);
        await audit.WriteAsync(uow, account,
            request.Handoff ? "OUTSOURCE_ELECTRICAL_HANDOFF" : "OUTSOURCE_ELECTRICAL_HANDOFF_WITHDRAW",
            "outsource_item", item.Id, new { targetName = item.WorkOrderNo, signerName = signer, mailQueued, changes = new[] { change } }, ct);
        await uow.CommitAsync(ct);
        if (mailQueued) mailSignal.Wake();
        return response;
    }

    private static async Task<OemAccountActor> RequireVendorAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct) =>
        await OemAuthorizer.RecheckAsync(uow, actor, ct) as OemAccountActor
            ?? throw ApiException.Forbidden("外协清单请在外协模块中查看");

    private static async Task<(YfDbContext Db, OutsourceReference Reference, IQueryable<OutsourceItem> Items, ulong CompanyId)> FilteredAsync(
        OemUnitOfWork uow, OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var keyword = request.Query["keyword"].ToString().Trim();
        var includeClosed = string.Equals(request.Query["includeClosed"], "true", StringComparison.OrdinalIgnoreCase);
        // Rechecks the account, its company and its session; only vendor accounts have a list here.
        var account = await RequireVendorAsync(uow, actor, ct);
        var db = uow.Db;
        var reference = await OutsourceReference.LoadAsync(db, ct);
        var items = db.OutsourceItems.AsNoTracking().Where(x => x.VendorId == account.CompanyId);
        if (!includeClosed) items = items.Where(x => x.ClosedAt == null);
        if (keyword.Length > 0)
        {
            var pattern = QueryValues.ContainsPattern(keyword);
            var byShortName = reference.CustomersWithShortNameContaining(keyword);
            items = items.Where(x => EF.Functions.Like(x.WorkOrderNo, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(x.Customer!, pattern, QueryValues.LikeEscape)
                || byShortName.Contains(x.Customer!)
                || EF.Functions.Like(x.MachineModel!, pattern, QueryValues.LikeEscape));
        }
        return (db, reference, items, account.CompanyId);
    }

    /// <summary>Handing off is offered (and its state shown) unless the type forbids it and the item is not already handed off.</summary>
    private static bool HandoffAvailable(OutsourceReference reference, OutsourceItemResponse item) =>
        item.ElectricalHandoff || reference.AllowsElectricalHandoff(item.TypeCode);

    private static OutsourceVendorRow Row(OutsourceReference reference, OutsourceItemResponse item, (int FileCount, int PurgedFileCount) counts)
    {
        var handoffAvailable = HandoffAvailable(reference, item);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            { [SerialKey] = item.SerialNo.ToString(CultureInfo.InvariantCulture) };
        foreach (var column in reference.Layout.VendorColumns)
            values[column.Key] = column.Key == OutsourceBuiltInFields.ElectricalHandoffKey && !handoffAvailable ? null : column.Value(item);
        return new(item.Id, item.Status, item.Closed, item.ElectricalHandoff, !item.Closed && handoffAvailable,
            values, counts.FileCount, counts.PurgedFileCount);
    }
}
