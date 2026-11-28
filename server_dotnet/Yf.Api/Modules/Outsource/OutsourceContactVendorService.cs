using System.Data;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Outsource;

/// <summary>
/// Read-only unified contact matrix for the ODM/OEM vendor portal (/api/v1/oem/contacts).
/// Strictly filtered by the authenticated vendor company; strips internal columns, disabled people,
/// internal user IDs, employee numbers, and other vendors' IDs.
/// </summary>
public sealed class OutsourceContactVendorService(IDbContextFactory<YfDbContext> dbFactory)
{
    public async Task<OemContactMatrixResponse> GetAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var account = await RequireVendorAsync(uow, actor, ct);
        return await LoadVendorMatrixAsync(uow.Db, account.CompanyId, ct);
    }

    public async Task<OutsourceExport> ExportAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var account = await RequireVendorAsync(uow, actor, ct);
        var matrix = await LoadVendorMatrixAsync(uow.Db, account.CompanyId, ct);
        if (matrix.Columns.Length == 0)
            throw ApiException.BadRequest("当前没有可导出的对接联络列");
        var bytes = OutsourceContactGrid.BuildVendorWorkbook(matrix.Columns, matrix.Rows);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd");
        return new(bytes, $"对接联络表_{today}.xlsx");
    }

    private static async Task<OemAccountActor> RequireVendorAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct) =>
        await OemAuthorizer.RecheckAsync(uow, actor, ct) as OemAccountActor
            ?? throw ApiException.Forbidden("对接联络表请在外协模块中查看");

    private static async Task<OemContactMatrixResponse> LoadVendorMatrixAsync(
        YfDbContext db, ulong companyId, CancellationToken ct)
    {
        // The audience, columns and contents below must all come from the same read view.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var columns = await db.OutsourceContactColumns.AsNoTracking()
            .Where(c => c.Status == AccountStatuses.Active && c.VendorVisible)
            .OrderBy(c => c.SortNo).ThenBy(c => c.Id)
            .Select(c => new OemContactColumnResponse(
                c.Id, c.ColKey, c.Title, c.DataType, c.PersonDisplay, c.PhoneTitle,
                c.AutoMerge, c.IsGroupColumn, c.SortNo, c.WidthHint))
            .ToArrayAsync(ct);

        if (columns.Length == 0) return new([], []);

        var allowedRowIds = await db.OutsourceContactRowVendors.AsNoTracking()
            .Where(rv => rv.VendorId == companyId)
            .Select(rv => rv.RowId)
            .Distinct()
            .ToArrayAsync(ct);

        var rows = await db.OutsourceContactRows.AsNoTracking()
            .Where(r => r.Visibility == ContactRowVisibilities.AllVendors
                || (r.Visibility == ContactRowVisibilities.SpecificVendors && allowedRowIds.Contains(r.Id)))
            .OrderBy(r => r.SortNo).ThenBy(r => r.Id)
            .ToArrayAsync(ct);

        if (rows.Length == 0) return new(columns, []);

        var rowIds = rows.Select(r => r.Id).ToArray();
        var colKeyById = columns.ToDictionary(c => c.Id, c => c.ColKey);
        var visibleColIds = colKeyById.Keys.ToArray();

        var rowValues = await db.OutsourceContactRowValues.AsNoTracking()
            .Where(rv => rowIds.Contains(rv.RowId) && visibleColIds.Contains(rv.ColumnId))
            .ToArrayAsync(ct);

        var rowPeople = await db.OutsourceContactRowPeople.AsNoTracking()
            .Where(rp => rowIds.Contains(rp.RowId) && visibleColIds.Contains(rp.ColumnId))
            .Join(
                db.OutsourceContactPeople.AsNoTracking().Where(p => p.Status == AccountStatuses.Active
                    && (p.ScopeType == ContactPersonScopes.Internal
                        || (p.ScopeType == ContactPersonScopes.Vendor && p.VendorId == companyId))),
                rp => rp.PersonId,
                p => p.Id,
                (rp, p) => new { rp, p })
            .OrderBy(x => x.rp.SortNo).ThenBy(x => x.rp.Id)
            .ToArrayAsync(ct);

        var valueLookup = rowValues.GroupBy(x => x.RowId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(x => colKeyById[x.ColumnId], x => x.TextValue, StringComparer.Ordinal));

        var peopleLookup = rowPeople.GroupBy(x => x.rp.RowId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, OemContactCellPersonResponse[]>)g
                .GroupBy(x => colKeyById[x.rp.ColumnId], StringComparer.Ordinal)
                .ToDictionary(
                    cg => cg.Key,
                    cg => cg.Select(x => new OemContactCellPersonResponse(
                        x.p.Id,
                        x.rp.PrefixLabel,
                        x.p.Name,
                        x.p.Phone,
                        x.p.Email)).ToArray(),
                    StringComparer.Ordinal));

        var emptyValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var emptyPeople = new Dictionary<string, OemContactCellPersonResponse[]>(StringComparer.Ordinal);

        var rowResponses = rows.Select(r => new OemContactRowResponse(
            r.Id,
            r.SortNo,
            valueLookup.GetValueOrDefault(r.Id) ?? emptyValues,
            peopleLookup.GetValueOrDefault(r.Id) ?? emptyPeople)).ToArray();

        return new(columns, rowResponses);
    }
}
