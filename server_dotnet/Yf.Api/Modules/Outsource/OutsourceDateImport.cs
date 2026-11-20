using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;

namespace Yf.Api.Modules.Outsource;

/// <summary>One sheet row: an empty date keeps the stored value.</summary>
public sealed record OutsourceDateImportRow(int RowNumber, string? WorkOrderNo, string? BomOn, string? DrawingReleasedOn);

public sealed record OutsourceDateImportRequest(OutsourceDateImportRow[]? Rows);

/// <summary>Preview of one row: READY rows carry the values before and after.</summary>
public sealed record OutsourceDateImportPreviewRow(
    int RowNumber, string WorkOrderNo, string Status, string? Message,
    DateOnly? BomOnBefore, DateOnly? BomOn, DateOnly? DrawingReleasedOnBefore, DateOnly? DrawingReleasedOn);

public sealed record OutsourceDateImportPreviewResponse(int Ready, int Skipped, int Invalid, OutsourceDateImportPreviewRow[] Rows);

/// <summary>
/// BOM时间 and 发图 are not edited on the list: they arrive from an Excel sheet (工令, BOM时间, 发图) that updates the
/// existing open records with the same work order, under its own permission <c>outsource:date_import</c>
/// (外协清单契约 → BOM时间与发图导入). Preview then confirm; the confirmation re-plans against the locked rows.
/// </summary>
public sealed class OutsourceDateImportService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumRows = 500;

    public async Task<OutsourceDateImportPreviewResponse> PreviewAsync(CurrentUser actor, OutsourceDateImportRequest request, CancellationToken ct)
    {
        var rows = ValidateRowCount(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.DateImport, ct);
        var parsed = Parse(rows);
        var workOrders = parsed.Where(x => x.Valid).Select(x => x.WorkOrderNo).ToArray();
        var items = await context.OutsourceItems.AsNoTracking().Where(x => workOrders.Contains(x.WorkOrderNo)).ToArrayAsync(ct);
        var planned = Plan(parsed, items);
        return Summary(planned);
    }

    public async Task<AdminImportResponse> ImportAsync(CurrentUser actor, OutsourceDateImportRequest request, CancellationToken ct)
    {
        var rows = ValidateRowCount(request);
        var parsed = Parse(rows);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.DateImport, ct);
        var workOrders = parsed.Where(x => x.Valid).Select(x => x.WorkOrderNo).ToArray();
        var ids = await context.OutsourceItems.AsNoTracking().Where(x => workOrders.Contains(x.WorkOrderNo))
            .OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
        var locked = new List<OutsourceItem>(ids.Length);
        foreach (var id in ids)
        {
            var row = await context.OutsourceItems.FromSqlInterpolated($"SELECT * FROM outsource_items WHERE id={id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is not null) locked.Add(row);
        }
        var planned = Plan(parsed, locked);
        var ready = planned.Where(x => x.Status == "READY").ToArray();
        if (ready.Length > 0)
        {
            var vendorKeys = (await OutsourceFieldLayout.LoadAsync(context, ct)).VendorKeys;
            var modifiedAt = await DbClock.UtcNowAsync(context, ct, 3);
            foreach (var row in ready)
            {
                var before = row.Item!;
                var changes = AuditChange.OnlyChanged(
                    new AuditChange("bomOn", "BOM时间", Text(before.BomOn), Text(row.BomOn)),
                    new AuditChange("drawingReleasedOn", "发图", Text(before.DrawingReleasedOn), Text(row.DrawingReleasedOn)));
                // Like an edit: a change to a column vendors see turns a mailed item into "changed, awaiting notice".
                var noticeNeeded = changes.Any(x => vendorKeys.Contains(x.Field));
                var sendStatus = noticeNeeded && before.SendStatus == OutsourceSendStatuses.Sent ? OutsourceSendStatuses.PendingNotice : before.SendStatus;
                await context.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.BomOn, row.BomOn).SetProperty(x => x.DrawingReleasedOn, row.DrawingReleasedOn)
                    .SetProperty(x => x.SendStatus, sendStatus).SetProperty(x => x.ModifiedBy, actor.Id).SetProperty(x => x.ModifiedAt, modifiedAt), ct);
                OutsourceHistory.Add(context, before.Id, changes, OutsourceHistory.DateImport, actor.Id);
            }
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_DATE_IMPORT", "outsource_item", null, new
                {
                    targetName = "BOM时间与发图导入",
                    count = ready.Length,
                    workOrders = ready.Select(x => x.Preview.WorkOrderNo).Take(100).ToArray(),
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        var results = planned.Select(x => new AdminImportRowResult(x.Preview.RowNumber, x.Preview.WorkOrderNo,
            x.Status switch { "READY" => "CREATED", "SKIPPED" => "SKIPPED", _ => "FAILED" }, x.Preview.Message)).ToArray();
        return new(results.Count(x => x.Status == "CREATED"), results.Count(x => x.Status == "SKIPPED"),
            results.Count(x => x.Status == "FAILED"), results);
    }

    private sealed record ParsedRow(int RowNumber, string WorkOrderNo, bool Valid, string? Error,
        DateOnly? BomOn, DateOnly? DrawingReleasedOn);

    private sealed record PlannedRow(string Status, OutsourceDateImportPreviewRow Preview, OutsourceItem? Item,
        DateOnly? BomOn, DateOnly? DrawingReleasedOn);

    private static OutsourceDateImportRow[] ValidateRowCount(OutsourceDateImportRequest request)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumRows || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumRows} 行");
        return request.Rows;
    }

    private static ParsedRow[] Parse(OutsourceDateImportRow[] rows)
    {
        var parsed = rows.Select((row, index) =>
        {
            var rowNumber = row.RowNumber is >= 2 and <= MaximumRows + 1 ? row.RowNumber : index + 2;
            var workOrderNo = row.WorkOrderNo?.Trim() ?? "";
            if (workOrderNo.Length is < 1 or > 32 || !workOrderNo.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return new ParsedRow(rowNumber, workOrderNo, false, "工令需为 1~32 位字母、数字、连字符或下划线", null, null);
            var bomText = row.BomOn?.Trim() ?? "";
            var drawingText = row.DrawingReleasedOn?.Trim() ?? "";
            if (bomText.Length == 0 && drawingText.Length == 0)
                return new ParsedRow(rowNumber, workOrderNo, false, "BOM时间和发图都未填写", null, null);
            if (!TryDate(bomText, out var bom)) return new ParsedRow(rowNumber, workOrderNo, false, "BOM时间格式不正确", null, null);
            if (!TryDate(drawingText, out var drawing)) return new ParsedRow(rowNumber, workOrderNo, false, "发图格式不正确", null, null);
            return new ParsedRow(rowNumber, workOrderNo, true, null, bom, drawing);
        }).ToArray();
        var repeated = parsed.Where(x => x.Valid).GroupBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parsed.Select(x => x.Valid && repeated.Contains(x.WorkOrderNo)
            ? x with { Valid = false, Error = "文件内工令重复，本重复组未导入" } : x).ToArray();
    }

    private static bool TryDate(string text, out DateOnly? value)
    {
        value = null;
        if (text.Length == 0) return true;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        value = date;
        return true;
    }

    private static List<PlannedRow> Plan(ParsedRow[] parsed, IReadOnlyCollection<OutsourceItem> items)
    {
        var byWorkOrder = items.ToDictionary(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase);
        return parsed.Select(row =>
        {
            OutsourceDateImportPreviewRow Preview(string status, string? message, OutsourceItem? item = null, DateOnly? bom = null, DateOnly? drawing = null) =>
                new(row.RowNumber, item?.WorkOrderNo ?? row.WorkOrderNo, status, message, item?.BomOn, bom, item?.DrawingReleasedOn, drawing);
            if (!row.Valid) return new PlannedRow("INVALID", Preview("INVALID", row.Error), null, null, null);
            if (!byWorkOrder.TryGetValue(row.WorkOrderNo, out var item))
                return new PlannedRow("SKIPPED", Preview("SKIPPED", "外协清单中没有该工令"), null, null, null);
            // An empty cell keeps what is stored.
            var bom = row.BomOn ?? item.BomOn;
            var drawing = row.DrawingReleasedOn ?? item.DrawingReleasedOn;
            if (item.ClosedAt is not null)
                return new PlannedRow("SKIPPED", Preview("SKIPPED", "已完成的记录不更新", item, bom, drawing), null, null, null);
            if (bom == item.BomOn && drawing == item.DrawingReleasedOn)
                return new PlannedRow("SKIPPED", Preview("SKIPPED", "与现有资料相同", item, bom, drawing), null, null, null);
            return new PlannedRow("READY", Preview("READY", null, item, bom, drawing), item, bom, drawing);
        }).ToList();
    }

    private static OutsourceDateImportPreviewResponse Summary(List<PlannedRow> planned) =>
        new(planned.Count(x => x.Status == "READY"), planned.Count(x => x.Status == "SKIPPED"),
            planned.Count(x => x.Status == "INVALID"), planned.Select(x => x.Preview).ToArray());

    private static string? Text(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
