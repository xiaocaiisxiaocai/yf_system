using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Outsource;

/// <summary>One column of the vendor view, in the order of the vendor Excel attachment.</summary>
public sealed record OutsourceVendorColumn(string Key, string Title);

/// <summary>One outsourcing record as its vendor sees it: the attachment's cells by column key, plus the status for row colour.</summary>
public sealed record OutsourceVendorRow(ulong Id, string Status, bool Closed, IReadOnlyDictionary<string, string?> Values);

public sealed record OutsourceVendorPageResponse(
    OutsourceVendorColumn[] Columns, IReadOnlyList<OutsourceVendorRow> List, ulong Total, ulong Page, uint PageSize);

/// <summary>
/// The outsourcing list in the ODM/OEMA vendor portal (外协清单契约 → 厂商查看): an ODM/OEMA account reads, never writes, the
/// current records whose vendor is its own company, with the columns the field settings show to vendors (the same as the
/// vendor Excel attachment). Internal staff use the outsourcing module itself. Vendors filling columns in is reserved.
/// </summary>
public sealed class OutsourceVendorViewService(IDbContextFactory<YfDbContext> dbFactory)
{
    internal const string SerialKey = "serialNo";

    public async Task<OutsourceVendorPageResponse> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var (db, reference, items) = await FilteredAsync(uow, actor, request, ct);
        var total = (ulong)await items.LongCountAsync(ct);
        var rows = await items.OrderBy(x => x.SerialNo).ThenBy(x => x.Id).Skip((int)offset).Take((int)size).ToArrayAsync(ct);
        await reference.LoadValuesAsync(db, rows.Select(x => x.Id).ToArray(), ct);
        OutsourceVendorColumn[] columns =
            [new(SerialKey, "序号"), .. reference.Layout.VendorColumns.Select(column => new OutsourceVendorColumn(column.Key, column.Label))];
        return new(columns, rows.Select(item => Row(reference, item)).ToArray(), total, page, size);
    }

    /// <summary>
    /// The records the list shows with the same keyword and completed filter, all pages, as the vendor Excel attachment
    /// (vendor columns, rows coloured by status, no yellow change marks).
    /// </summary>
    public async Task<OutsourceExport> ExportAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var (db, reference, items) = await FilteredAsync(uow, actor, request, ct);
        if (await items.CountAsync(ct) > OutsourceItemService.MaximumExportRows)
            throw ApiException.BadRequest($"一次最多导出 {OutsourceItemService.MaximumExportRows} 条，请缩小筛选范围");
        var rows = await items.OrderBy(x => x.SerialNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        await reference.LoadValuesAsync(db, rows.Select(x => x.Id).ToArray(), ct);
        var content = OutsourceWorkbook.Build(rows.Select(reference.Json).ToArray(), null,
            reference.Layout.WorkbookFields(vendor: true), forVendor: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new(content, $"外协清单_{today}.xlsx");
    }

    private static async Task<(YfDbContext Db, OutsourceReference Reference, IQueryable<OutsourceItem> Items)> FilteredAsync(
        OemUnitOfWork uow, OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var keyword = request.Query["keyword"].ToString().Trim();
        var includeClosed = string.Equals(request.Query["includeClosed"], "true", StringComparison.OrdinalIgnoreCase);
        // Rechecks the account, its company and its session; only vendor accounts have a list here.
        if (await OemAuthorizer.RecheckAsync(uow, actor, ct) is not OemAccountActor account)
            throw ApiException.Forbidden("外协清单请在外协模块中查看");
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
        return (db, reference, items);
    }

    private static OutsourceVendorRow Row(OutsourceReference reference, OutsourceItem item)
    {
        var response = reference.Json(item);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            { [SerialKey] = item.SerialNo.ToString(CultureInfo.InvariantCulture) };
        foreach (var column in reference.Layout.VendorColumns) values[column.Key] = column.Value(response);
        return new(item.Id, item.Status, item.ClosedAt is not null, values);
    }
}
