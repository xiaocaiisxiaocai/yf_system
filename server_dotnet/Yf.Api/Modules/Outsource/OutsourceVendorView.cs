using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
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
/// current records whose vendor is its own company, with the columns of the vendor Excel attachment. Internal staff use
/// the outsourcing module itself. Fields vendors fill in will be added here once they are specified.
/// </summary>
public sealed class OutsourceVendorViewService(IDbContextFactory<YfDbContext> dbFactory)
{
    internal const string SerialKey = "serialNo";

    public static OutsourceVendorColumn[] Columns { get; } =
        [new(SerialKey, "序号"), .. OutsourceWorkbook.Fields.Select(field => new OutsourceVendorColumn(field.Key, field.Header))];

    public async Task<OutsourceVendorPageResponse> ListAsync(OemActor actor, HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        var keyword = request.Query["keyword"].ToString().Trim();
        var includeClosed = string.Equals(request.Query["includeClosed"], "true", StringComparison.OrdinalIgnoreCase);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
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
        var total = (ulong)await items.LongCountAsync(ct);
        var rows = await items.OrderBy(x => x.SerialNo).ThenBy(x => x.Id).Skip((int)offset).Take((int)size).ToArrayAsync(ct);
        var list = rows.Select(item =>
        {
            var response = reference.Json(item);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
                { [SerialKey] = item.SerialNo.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            foreach (var field in OutsourceWorkbook.Fields) values[field.Key] = field.Value(response);
            return new OutsourceVendorRow(item.Id, item.Status, item.ClosedAt is not null, values);
        }).ToArray();
        return new(Columns, list, total, page, size);
    }
}
