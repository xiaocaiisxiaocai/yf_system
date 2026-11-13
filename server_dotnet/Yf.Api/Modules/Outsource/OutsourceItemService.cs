using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceItemStatuses
{
    internal const string Normal = "NORMAL";
    internal const string Paused = "PAUSED";
    internal const string Cancelled = "CANCELLED";
    internal static bool IsValid(string? value) => value is Normal or Paused or Cancelled;
    internal static string Label(string value) => value switch { Paused => "暂停", Cancelled => "取消", _ => "正常" };
}

internal static class OutsourceSendStatuses
{
    internal const string Unsent = "UNSENT";
    internal const string Sent = "SENT";
    internal const string PendingNotice = "PENDING_NOTICE";
    internal static bool IsValid(string? value) => value is Unsent or Sent or PendingNotice;
    internal static string Label(string value) => value switch { Sent => "已发送", PendingNotice => "有变更待通知", _ => "未发送" };
}

/// <summary>Whether an item's IPMS type and vendor pass the type/vendor mapping; anything but OK blocks sending.</summary>
internal static class OutsourceMatchStates
{
    internal const string Ok = "OK";
    internal const string UnknownType = "UNKNOWN_TYPE";
    internal const string VendorMismatch = "VENDOR_MISMATCH";
}

/// <summary>List filters shared by the paged list and the export.</summary>
public sealed record OutsourceItemQuery(
    ulong? VendorId, ulong? TypeId, string? Status, string? SendStatus, DateOnly? DateFrom, DateOnly? DateTo,
    string? Keyword, bool IncludeClosed)
{
    public static OutsourceItemQuery From(HttpRequest request)
    {
        static DateOnly? Date(HttpRequest request, string name)
        {
            var value = request.Query[name].ToString();
            if (value.Length == 0) return null;
            return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : throw ApiException.BadRequest("请求参数错误: " + name);
        }
        var status = request.Query["status"].ToString();
        var sendStatus = request.Query["sendStatus"].ToString();
        if (status.Length > 0 && !OutsourceItemStatuses.IsValid(status)) throw ApiException.BadRequest("请求参数错误: status");
        if (sendStatus.Length > 0 && !OutsourceSendStatuses.IsValid(sendStatus)) throw ApiException.BadRequest("请求参数错误: sendStatus");
        var includeClosed = request.Query["includeClosed"].ToString() switch
        {
            "" or "false" => false,
            "true" => true,
            _ => throw ApiException.BadRequest("请求参数错误: includeClosed"),
        };
        var keyword = request.Query["keyword"].ToString().Trim();
        return new(QueryValues.OptionalUInt64(request, "vendorId"), QueryValues.OptionalUInt64(request, "typeId"),
            status.Length == 0 ? null : status, sendStatus.Length == 0 ? null : sendStatus,
            Date(request, "dateFrom"), Date(request, "dateTo"), keyword.Length == 0 ? null : keyword[..Math.Min(keyword.Length, 64)],
            includeClosed);
    }
}

/// <summary>Exported workbook bytes and its download name.</summary>
public sealed record OutsourceExport(byte[] Content, string FileName);

/// <summary>
/// The outsourcing list itself: import (preview then confirm, both against IPMS), the overall table and per-vendor
/// tabs, export, purchasing edits and deletion of unsent rows. See 外协清单契约 → 汇入 / 采购编辑与删除 / 页面.
/// </summary>
public sealed class OutsourceItemService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit, IIpmsClient ipms)
{
    internal const int MaximumImportRows = 500;
    internal const int MaximumBatchItems = 500;
    internal const int MaximumExportRows = 10_000;
    private const string ChangedMessage = "外协记录已被他人修改，请刷新后重试";
    private static readonly string[] ImportHeaders = ["外发日期", "工令", "外发厂商", "外发类型"];

    // ---- import ----

    public async Task<OutsourceImportPreviewResponse> PreviewImportAsync(CurrentUser actor, OutsourceImportRequest request, CancellationToken ct)
    {
        var rows = ValidateRowCount(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.Import, ct);
        var planned = await PlanImportAsync(context, rows, ct);
        return new(planned.Count(x => x.Status == "READY"), planned.Count(x => x.Status == "SKIPPED"),
            planned.Count(x => x.Status == "INVALID"), planned.Select(x => x.Preview).ToArray());
    }

    /// <summary>Re-plans against IPMS and the database, then stores only READY rows in one transaction.</summary>
    public async Task<AdminImportResponse> ImportAsync(CurrentUser actor, OutsourceImportRequest request, CancellationToken ct)
    {
        var rows = ValidateRowCount(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.Import, ct);
        var planned = await PlanImportAsync(context, rows, ct);
        await context.Database.CloseConnectionAsync();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, OutsourcePermissions.Import, ct);
        var ready = planned.Where(x => x.Status == "READY").ToArray();
        var vendorIds = ready.Select(x => x.VendorId).Distinct().ToArray();
        var activeVendors = await context.OutsourceVendors.Where(x => vendorIds.Contains(x.Id) && x.Status == AccountStatuses.Active)
            .Select(x => x.Id).ToArrayAsync(ct);
        if (activeVendors.Length != vendorIds.Length) throw ApiException.Conflict("外协厂商状态已变化，请重新预览");
        var items = ready.Select(x => x.Item!).ToArray();
        foreach (var item in items) item.CreatedBy = actor.Id;
        context.OutsourceItems.AddRange(items);
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("部分工令已被他人汇入，请重新预览"); }
        if (items.Length > 0)
        {
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_IMPORT", "outsource_item", null, new
                {
                    targetName = "外协清单汇入",
                    count = items.Length,
                    workOrders = items.Select(x => x.WorkOrderNo).Take(100).ToArray(),
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        var results = planned.Select(x => new AdminImportRowResult(x.Preview.RowNumber, x.Preview.WorkOrderNo,
            x.Status switch { "READY" => "CREATED", "SKIPPED" => "SKIPPED", _ => "FAILED" }, x.Preview.Message)).ToArray();
        return new(results.Count(x => x.Status == "CREATED"), results.Count(x => x.Status == "SKIPPED"),
            results.Count(x => x.Status == "FAILED"), results);
    }

    private sealed record PlannedRow(string Status, OutsourceImportPreviewRow Preview, ulong VendorId, OutsourceItem? Item);

    private async Task<List<PlannedRow>> PlanImportAsync(YfDbContext context, OutsourceImportRow[] rows, CancellationToken ct)
    {
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var vendorsByName = await context.OutsourceVendors.AsNoTracking()
            .Select(x => new { x.Id, x.Name, x.Status }).ToListAsync(ct);
        var vendorLookup = vendorsByName.ToDictionary(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var parsed = rows.Select((row, index) => new
        {
            RowNumber = row.RowNumber is >= 2 and <= MaximumImportRows + 1 ? row.RowNumber : index + 2,
            WorkOrderNo = row.WorkOrderNo?.Trim() ?? "",
            DateText = row.OutsourceDate?.Trim() ?? "",
            VendorName = row.VendorName?.Trim() ?? "",
            TypeText = row.TypeName?.Trim() ?? "",
        }).ToArray();
        var repeated = parsed.Where(x => x.WorkOrderNo.Length > 0).GroupBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = parsed.Select(x => x.WorkOrderNo).Where(ValidWorkOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = (await context.OutsourceItems.AsNoTracking().Where(x => candidates.Contains(x.WorkOrderNo))
            .Select(x => x.WorkOrderNo).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<PlannedRow>(parsed.Length);
        var lookups = new List<(int Index, string WorkOrderNo, DateOnly Date, ulong VendorId, string VendorName, string TypeCode, string TypeLabel)>();
        foreach (var row in parsed)
        {
            OutsourceImportPreviewRow Preview(string status, string? message) =>
                new(row.RowNumber, row.WorkOrderNo, status, message, row.VendorName.Length == 0 ? null : row.VendorName,
                    row.DateText.Length == 0 ? null : row.DateText, null, null, null, null, null);
            if (!ValidWorkOrder(row.WorkOrderNo))
            { result.Add(new("INVALID", Preview("INVALID", "工令需为 1~32 位字母、数字、连字符或下划线"), 0, null)); continue; }
            if (repeated.Contains(row.WorkOrderNo))
            { result.Add(new("SKIPPED", Preview("SKIPPED", "文件内工令重复，本重复组未汇入"), 0, null)); continue; }
            if (existing.Contains(row.WorkOrderNo))
            { result.Add(new("SKIPPED", Preview("SKIPPED", "工令已存在，未覆盖现有资料"), 0, null)); continue; }
            if (!DateOnly.TryParseExact(row.DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { result.Add(new("INVALID", Preview("INVALID", "外发日期格式不正确"), 0, null)); continue; }
            if (!vendorLookup.TryGetValue(row.VendorName, out var vendor))
            { result.Add(new("INVALID", Preview("INVALID", "外发厂商不在外协厂商目录中"), 0, null)); continue; }
            if (vendor.Status != AccountStatuses.Active)
            { result.Add(new("INVALID", Preview("INVALID", "外发厂商已停用"), 0, null)); continue; }
            // The type is entered with the vendor; it must be an enabled dictionary type the vendor may take.
            if (row.TypeText.Length == 0)
            { result.Add(new("INVALID", Preview("INVALID", "请填写外发类型"), 0, null)); continue; }
            if (reference.Find(row.TypeText) is not { } type)
            { result.Add(new("INVALID", Preview("INVALID", $"外发类型「{row.TypeText}」不在字典中"), 0, null)); continue; }
            var typeLabel = OutsourceSettingsService.Label(type.Code, type.Name);
            if (reference.Match(row.TypeText, vendor.Id) != OutsourceMatchStates.Ok)
            { result.Add(new("INVALID", Preview("INVALID", $"{typeLabel} 不可配{vendor.Name}") with { TypeLabel = typeLabel }, 0, null)); continue; }
            result.Add(new("PENDING", Preview("PENDING", null) with { TypeLabel = typeLabel }, vendor.Id, null));
            lookups.Add((result.Count - 1, row.WorkOrderNo, date, vendor.Id, vendor.Name, type.Code, typeLabel));
        }
        if (lookups.Count == 0) return result;

        IReadOnlyDictionary<string, IpmsWorkOrder> found;
        try { found = await ipms.LookupAsync(lookups.Select(x => x.WorkOrderNo).ToArray(), ct); }
        catch (IpmsUnavailableException error) { throw ApiException.Unavailable(error.Message); }
        var foundIgnoringCase = found.Values.ToDictionary(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase);
        foreach (var lookup in lookups)
        {
            var preview = result[lookup.Index].Preview;
            if (!foundIgnoringCase.TryGetValue(lookup.WorkOrderNo, out var order))
            { result[lookup.Index] = new("INVALID", preview with { Status = "INVALID", Message = "IPMS 查无此工令" }, 0, null); continue; }
            var withIpms = preview with
            {
                VendorName = lookup.VendorName, OutsourceDate = lookup.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Customer = order.Customer, MachineModel = order.MachineModel, TypeLabel = lookup.TypeLabel,
                Quantity = order.Quantity, LatestDueDate = order.DueDate,
            };
            if (order.Closed)
            { result[lookup.Index] = new("INVALID", withIpms with { Status = "INVALID", Message = "IPMS 显示该工令已结案" }, 0, null); continue; }
            var item = new OutsourceItem
            {
                WorkOrderNo = order.WorkOrderNo, OutsourceDate = lookup.Date, VendorId = lookup.VendorId,
                Status = OutsourceItemStatuses.Normal, SendStatus = OutsourceSendStatuses.Unsent,
            };
            ApplyIpms(item, order, DateTime.UtcNow);
            item.TypeCode = lookup.TypeCode; // entered by staff, not read from IPMS
            item.OriginalDueDate = order.DueDate;
            result[lookup.Index] = new("READY", withIpms with { Status = "READY" }, lookup.VendorId, item);
        }
        return result;
    }

    /// <summary>Copies the IPMS snapshot onto an item, truncated to the column sizes.</summary>
    internal static void ApplyIpms(OutsourceItem item, IpmsWorkOrder order, DateTime syncedAt)
    {
        item.Customer = Cut(order.Customer, 128);
        item.MachineModel = Cut(order.MachineModel, 128);
        item.MechanicalSection = Cut(order.MechanicalSection, 64);
        item.MechanicalOwner = Cut(order.MechanicalOwner, 64);
        item.ElectricalOwner = Cut(order.ElectricalOwner, 64);
        item.WorkOrderIssuedOn = order.WorkOrderIssuedOn;
        item.BomOn = order.BomOn;
        item.DrawingReleasedOn = order.DrawingReleasedOn;
        item.Quantity = order.Quantity;
        item.LatestDueDate = order.DueDate;
        item.IpmsSyncedAt = syncedAt;
    }

    private static string? Cut(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static bool ValidWorkOrder(string value) =>
        value.Length is >= 1 and <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static OutsourceImportRow[] ValidateRowCount(OutsourceImportRequest request)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumImportRows || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"汇入行数需为 1–{MaximumImportRows} 行");
        return request.Rows;
    }

    // ---- list, summary, export ----

    public async Task<PageResponse<OutsourceItemResponse>> ListAsync(
        CurrentUser actor, OutsourceItemQuery query, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var filtered = Filter(context.OutsourceItems.AsNoTracking(), query, reference);
        var total = (ulong)await filtered.LongCountAsync(ct);
        var rows = await filtered.OrderByDescending(x => x.OutsourceDate).ThenByDescending(x => x.Id)
            .Skip((int)Math.Min(offset, int.MaxValue)).Take((int)size).ToArrayAsync(ct);
        return new(rows.Select(reference.Json).ToArray(), total, page, size);
    }

    /// <summary>Counts for the overall tab and each vendor tab (closed rows only when asked for).</summary>
    public async Task<OutsourceItemSummaryResponse> SummaryAsync(CurrentUser actor, bool includeClosed, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var items = context.OutsourceItems.AsNoTracking();
        if (!includeClosed) items = items.Where(x => x.ClosedAt == null);
        var counts = await items.GroupBy(x => x.VendorId).Select(g => new { VendorId = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        return new(counts.Sum(x => x.Count), counts.Select(x => new OutsourceVendorCount(x.VendorId, x.Count)).ToArray());
    }

    public async Task<OutsourceExport> ExportAsync(CurrentUser actor, OutsourceItemQuery query, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var filtered = Filter(context.OutsourceItems.AsNoTracking(), query, reference);
        if (await filtered.CountAsync(ct) > MaximumExportRows)
            throw ApiException.BadRequest($"一次最多导出 {MaximumExportRows} 条，请缩小筛选范围");
        var rows = await filtered.OrderByDescending(x => x.OutsourceDate).ThenByDescending(x => x.Id).ToArrayAsync(ct);
        var content = OutsourceWorkbook.Build(rows.Select(reference.Json).ToArray(), null);
        var vendorPart = query.VendorId is { } vendorId && reference.VendorNames.TryGetValue(vendorId, out var name) ? "_" + name : "";
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new(content, $"外协清单{vendorPart}_{today}.xlsx");
    }

    private static IQueryable<OutsourceItem> Filter(IQueryable<OutsourceItem> items, OutsourceItemQuery query, OutsourceReference reference)
    {
        if (!query.IncludeClosed) items = items.Where(x => x.ClosedAt == null);
        if (query.VendorId is { } vendorId) items = items.Where(x => x.VendorId == vendorId);
        if (query.TypeId is { } typeId)
        {
            var code = reference.CodeOf(typeId);
            if (code is null) return items.Where(_ => false);
            var labelled = code + "【";
            items = items.Where(x => x.TypeCode == code || x.TypeCode!.StartsWith(labelled));
        }
        if (query.Status is { } status) items = items.Where(x => x.Status == status);
        if (query.SendStatus is { } sendStatus) items = items.Where(x => x.SendStatus == sendStatus);
        if (query.DateFrom is { } from) items = items.Where(x => x.OutsourceDate >= from);
        if (query.DateTo is { } to) items = items.Where(x => x.OutsourceDate <= to);
        if (query.Keyword is { } keyword)
        {
            var pattern = QueryValues.ContainsPattern(keyword);
            items = items.Where(x => EF.Functions.Like(x.WorkOrderNo, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(x.Customer!, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(x.MachineModel!, pattern, QueryValues.LikeEscape));
        }
        return items;
    }

    // ---- change history ----

    /// <summary>All recorded changes of one item, newest first.</summary>
    public async Task<OutsourceItemChangeResponse[]> ChangesAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        if (!await context.OutsourceItems.AnyAsync(x => x.Id == id, ct)) throw ApiException.NotFound("外协记录不存在或已被删除");
        var rows = await context.OutsourceItemChanges.AsNoTracking().Where(x => x.ItemId == id)
            .OrderByDescending(x => x.Id)
            .GroupJoin(context.Users, change => change.ActorUserId, user => (ulong?)user.Id, (change, users) => new { change, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, user) => new { x.change, Name = user == null ? null : user.RealName })
            .ToArrayAsync(ct);
        return rows.Select(x => new OutsourceItemChangeResponse(x.change.Id, x.change.Field, OutsourceHistory.Label(x.change.Field),
            x.change.OldValue, x.change.NewValue, x.change.Source, x.Name, DateTime.SpecifyKind(x.change.ChangedAt, DateTimeKind.Utc))).ToArray();
    }

    // ---- edits and deletion ----

    public async Task<OutsourceItemResponse> UpdateAsync(CurrentUser actor, ulong id, OutsourceItemUpdate request, CancellationToken ct)
    {
        if (request.ExpectedUpdatedAt is not { } expected) throw ApiException.BadRequest("expectedUpdatedAt 必须为当前记录的 updatedAt");
        var change = ParseChange(request.OutsourceDate, request.VendorId, request.TypeId, request.Remark, request.Status, requireAll: true);
        var result = await ApplyAsync(actor, [new OutsourceItemVersion(id, expected)], change, ct);
        return result.Items.Single();
    }

    public Task<OutsourceBatchResponse> BatchUpdateAsync(CurrentUser actor, OutsourceBatchUpdate request, CancellationToken ct)
    {
        var items = ValidateBatch(request.Items);
        var change = ParseChange(request.OutsourceDate, request.VendorId, request.TypeId, request.Remark, request.Status, requireAll: false);
        if (change.Date is null && change.VendorId is null && change.TypeId is null && change.Remark is null && change.Status is null)
            throw ApiException.BadRequest("请至少选择一项要修改的内容");
        return ApplyAsync(actor, items, change, ct);
    }

    public async Task<EmptyResponse> BatchDeleteAsync(CurrentUser actor, OutsourceBatchDelete request, CancellationToken ct)
    {
        var versions = ValidateBatch(request.Items);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, OutsourcePermissions.Manage, ct);
        var locked = await LockAsync(context, versions, ct);
        if (locked.Any(x => x.SendStatus != OutsourceSendStatuses.Unsent))
            throw ApiException.Conflict("已发送给厂商的记录不能删除，请改为「取消」");
        var ids = locked.Select(x => x.Id).ToArray();
        await context.OutsourceItems.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var vendors = await VendorNamesAsync(context, ct);
        foreach (var item in locked)
        {
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_DELETE", "outsource_item", item.Id, new
                {
                    targetName = item.WorkOrderNo, workOrderNo = item.WorkOrderNo,
                    vendor = vendors.GetValueOrDefault(item.VendorId),
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    private sealed record Change(DateOnly? Date, ulong? VendorId, ulong? TypeId, string? Remark, string? Status);

    private static Change ParseChange(string? date, ulong? vendorId, ulong? typeId, string? remark, string? status, bool requireAll)
    {
        DateOnly? parsedDate = null;
        if (date is not null || requireAll)
        {
            if (!DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
                throw ApiException.BadRequest("外发日期格式不正确");
            parsedDate = value;
        }
        if (requireAll && vendorId is null) throw ApiException.BadRequest("请选择外发厂商");
        if (status is not null || requireAll)
        {
            if (!OutsourceItemStatuses.IsValid(status)) throw ApiException.BadRequest("状态只能是正常、暂停或取消");
        }
        string? normalizedRemark = null;
        if (remark is not null || requireAll)
        {
            normalizedRemark = remark?.Trim() ?? "";
            if (normalizedRemark.EnumerateRunes().Count() > 500) throw ApiException.BadRequest("备注最多 500 个字符");
        }
        return new(parsedDate, vendorId, typeId, normalizedRemark, status);
    }

    private static OutsourceItemVersion[] ValidateBatch(OutsourceItemVersion[]? items)
    {
        if (items is null || items.Length is < 1 or > MaximumBatchItems || items.Any(x => x is null))
            throw ApiException.BadRequest($"请选择 1–{MaximumBatchItems} 条记录");
        if (items.Select(x => x.Id).Distinct().Count() != items.Length) throw ApiException.BadRequest("记录不能重复");
        return items;
    }

    private async Task<OutsourceBatchResponse> ApplyAsync(CurrentUser actor, OutsourceItemVersion[] versions, Change change, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, OutsourcePermissions.Manage, ct);
        var locked = await LockAsync(context, versions, ct);
        if (locked.Any(x => x.ClosedAt is not null)) throw ApiException.Conflict("已结案的记录不能修改");
        var reference = await OutsourceReference.LoadAsync(context, ct);
        if (change.VendorId is { } vendorId && !reference.ActiveVendorIds.Contains(vendorId)) throw ApiException.BadRequest("外发厂商不存在或已停用");
        var newTypeCode = change.TypeId is { } typeId
            ? reference.CodeOf(typeId) ?? throw ApiException.BadRequest("外发类型不存在或已停用")
            : null;
        // Whenever the type or the vendor changes, the resulting pair must be allowed; an edit that touches neither is never blocked.
        foreach (var item in locked)
        {
            var typeChanged = TypeChanged(item, newTypeCode);
            var vendorChanged = change.VendorId is { } moved && moved != item.VendorId;
            if (!typeChanged && !vendorChanged) continue;
            var typeCode = typeChanged ? newTypeCode : item.TypeCode;
            var vendor = change.VendorId ?? item.VendorId;
            var match = reference.Match(typeCode, vendor);
            if (match == OutsourceMatchStates.UnknownType)
                throw ApiException.BadRequest($"工令 {item.WorkOrderNo} 的外发类型不在字典中，请先选择外发类型");
            if (match == OutsourceMatchStates.VendorMismatch)
                throw ApiException.BadRequest($"工令 {item.WorkOrderNo}：{reference.Label(typeCode)} 不可配{reference.VendorNames[vendor]}");
        }
        foreach (var before in locked)
        {
            var date = change.Date ?? before.OutsourceDate;
            var vendor = change.VendorId ?? before.VendorId;
            var remark = change.Remark is null ? before.Remark : change.Remark.Length == 0 ? null : change.Remark;
            var status = change.Status ?? before.Status;
            var typeCode = TypeChanged(before, newTypeCode) ? newTypeCode : before.TypeCode;
            var changes = AuditChange.OnlyChanged(
                new AuditChange("outsourceDate", "外发日期", Text(before.OutsourceDate), Text(date)),
                new AuditChange("vendor", "外发厂商", reference.VendorNames.GetValueOrDefault(before.VendorId), reference.VendorNames.GetValueOrDefault(vendor)),
                new AuditChange("typeCode", "外发类型", reference.Label(before.TypeCode), reference.Label(typeCode)),
                new AuditChange("remark", "备注", before.Remark, remark),
                new AuditChange("status", "状态", OutsourceItemStatuses.Label(before.Status), OutsourceItemStatuses.Label(status)));
            if (changes.Length == 0) continue;
            var sendStatus = before.SendStatus == OutsourceSendStatuses.Sent ? OutsourceSendStatuses.PendingNotice : before.SendStatus;
            await context.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OutsourceDate, date).SetProperty(x => x.VendorId, vendor).SetProperty(x => x.TypeCode, typeCode)
                .SetProperty(x => x.Remark, remark).SetProperty(x => x.Status, status)
                .SetProperty(x => x.SendStatus, sendStatus), ct);
            OutsourceHistory.Add(context, before.Id, changes, OutsourceHistory.Edit, actor.Id);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_UPDATE", "outsource_item", before.Id, new
                {
                    targetName = before.WorkOrderNo, changes,
                    batch = versions.Length > 1 ? versions.Length : (int?)null,
                }, null, ct);
        }
        var ids = locked.Select(x => x.Id).ToArray();
        var updated = await context.OutsourceItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        await transaction.CommitAsync(ct);
        var order = versions.Select((x, index) => (x.Id, index)).ToDictionary(x => x.Id, x => x.index);
        return new(updated.OrderBy(x => order[x.Id]).Select(reference.Json).ToArray());
    }

    /// <summary>Whether choosing <paramref name="newTypeCode"/> changes the item's type (the same dictionary type spelled differently is not a change).</summary>
    private static bool TypeChanged(OutsourceItem item, string? newTypeCode) =>
        newTypeCode is not null && OutsourceReference.Normalize(newTypeCode) != OutsourceReference.Normalize(item.TypeCode);

    /// <summary>Locks rows in id order (no deadlock between overlapping batches) and checks each client version.</summary>
    private static async Task<List<OutsourceItem>> LockAsync(YfDbContext context, OutsourceItemVersion[] versions, CancellationToken ct)
    {
        var locked = new List<OutsourceItem>(versions.Length);
        foreach (var version in versions.OrderBy(x => x.Id))
        {
            var id = version.Id;
            var row = await context.OutsourceItems.FromSqlInterpolated($"SELECT * FROM outsource_items WHERE id={id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("外协记录不存在或已被删除");
            if (Utc(row.UpdatedAt) != Utc(version.ExpectedUpdatedAt)) throw ApiException.VersionConflict(ChangedMessage);
            locked.Add(row);
        }
        return locked;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
        .AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));

    private static string Text(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<Dictionary<ulong, string>> VendorNamesAsync(YfDbContext context, CancellationToken ct) =>
        context.OutsourceVendors.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

    // ---- authorization ----

    private static async Task AuthorizeReadAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, null, actor, permission, ct);
    }

    private static async Task<CurrentUser> AuthorizeWriteAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var connection = context.Database.Connection();
        var transaction = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, transaction, actor, permission, ct);
        return actor;
    }

    internal static IReadOnlyList<string> TemplateHeaders => ImportHeaders;
}

/// <summary>Type dictionary, vendor mapping and vendor names, loaded once per request to resolve items.</summary>
internal sealed class OutsourceReference
{
    private readonly Dictionary<string, (ulong Id, string Code, string Name)> types;
    private readonly HashSet<(ulong TypeId, ulong VendorId)> mapping;
    public IReadOnlyDictionary<ulong, string> VendorNames { get; }
    public IReadOnlySet<ulong> ActiveVendorIds { get; }

    private OutsourceReference(Dictionary<string, (ulong, string, string)> types, HashSet<(ulong, ulong)> mapping,
        Dictionary<ulong, string> vendorNames, HashSet<ulong> activeVendorIds)
    {
        this.types = types; this.mapping = mapping; VendorNames = vendorNames; ActiveVendorIds = activeVendorIds;
    }

    public static async Task<OutsourceReference> LoadAsync(YfDbContext context, CancellationToken ct)
    {
        // Only active types match; a disabled type behaves like an unknown one until it is re-enabled.
        var types = await context.OutsourceTypes.AsNoTracking().Where(x => x.Status == AccountStatuses.Active)
            .Select(x => new { x.Id, x.Code, x.Name }).ToArrayAsync(ct);
        var mapping = await context.OutsourceTypeVendors.AsNoTracking().Select(x => new { x.TypeId, x.VendorId }).ToArrayAsync(ct);
        var vendors = await context.OutsourceVendors.AsNoTracking().Select(x => new { x.Id, x.Name, x.Status }).ToArrayAsync(ct);
        return new(types.ToDictionary(x => Normalize(x.Code)!, x => (x.Id, x.Code, x.Name)),
            mapping.Select(x => (x.TypeId, x.VendorId)).ToHashSet(),
            vendors.ToDictionary(x => x.Id, x => x.Name),
            vendors.Where(x => x.Status == AccountStatuses.Active).Select(x => x.Id).ToHashSet());
    }

    /// <summary>IPMS may report "OEMA" or "OEMA【进料+制造】"; both resolve to the dictionary code.</summary>
    internal static string? Normalize(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        var bracket = value.IndexOf('【', StringComparison.Ordinal);
        if (bracket > 0) value = value[..bracket].Trim();
        return value.ToUpperInvariant();
    }

    public (ulong Id, string Code, string Name)? Find(string? raw) =>
        Normalize(raw) is { } key && types.TryGetValue(key, out var type) ? type : null;

    public string Match(string? rawType, ulong vendorId) => Find(rawType) is not { } type
        ? OutsourceMatchStates.UnknownType
        : mapping.Contains((type.Id, vendorId)) ? OutsourceMatchStates.Ok : OutsourceMatchStates.VendorMismatch;

    public string? Label(string? rawType) => Find(rawType) is { } type
        ? OutsourceSettingsService.Label(type.Code, type.Name) : rawType?.Trim();

    public string? CodeOf(ulong typeId) => types.Values.Where(x => x.Id == typeId).Select(x => x.Code).FirstOrDefault();

    public OutsourceItemResponse Json(OutsourceItem item) => new(
        item.Id, item.WorkOrderNo, item.OutsourceDate, item.VendorId, VendorNames.GetValueOrDefault(item.VendorId) ?? "",
        item.Customer, item.MachineModel, item.MechanicalSection, item.MechanicalOwner, item.ElectricalOwner,
        item.WorkOrderIssuedOn, item.BomOn, item.DrawingReleasedOn,
        item.TypeCode, Find(item.TypeCode)?.Id, Label(item.TypeCode), item.Quantity, item.OriginalDueDate, item.LatestDueDate,
        item.Remark, item.Status, item.SendStatus, Match(item.TypeCode, item.VendorId), item.ClosedAt is not null,
        item.IpmsSyncedAt, item.UpdatedAt);
}

/// <summary>
/// The outsourcing-list columns, shared by the export, vendor mail bodies and attachments, and the "as last sent"
/// snapshot, so the same field is compared and highlighted everywhere.
/// </summary>
internal static class OutsourceWorkbook
{
    internal sealed record Field(string Key, string Header, double Width, Func<OutsourceItemResponse, string?> Value);

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Every business column in template order (without the row number).</summary>
    internal static readonly Field[] Fields =
    [
        new("outsourceDate", "外发日期", 12, x => Date(x.OutsourceDate)),
        new("workOrderNo", "工令", 12, x => x.WorkOrderNo),
        new("customer", "客户", 18, x => x.Customer),
        new("machineModel", "机型", 14, x => x.MachineModel),
        new("mechanicalSection", "机构课别", 14, x => x.MechanicalSection),
        new("mechanicalOwner", "机构执行人", 12, x => x.MechanicalOwner),
        new("electricalOwner", "电控执行人", 12, x => x.ElectricalOwner),
        new("workOrderIssuedOn", "工令下发", 12, x => Date(x.WorkOrderIssuedOn)),
        new("bomOn", "BOM时间", 12, x => Date(x.BomOn)),
        new("drawingReleasedOn", "发图", 12, x => Date(x.DrawingReleasedOn)),
        new("type", "外发类型", 24, x => x.TypeLabel ?? x.TypeCode),
        new("originalDueDate", "原始交期", 12, x => Date(x.OriginalDueDate)),
        new("latestDueDate", "最新交期", 12, x => Date(x.LatestDueDate)),
        new("quantity", "数量", 8, x => x.Quantity?.ToString(CultureInfo.InvariantCulture)),
        new("vendor", "外发厂商", 14, x => x.VendorName),
        new("remark", "备注", 24, x => x.Remark),
        new("status", "状态", 8, x => OutsourceItemStatuses.Label(x.Status)),
    ];

    private static readonly Field SendStatus = new("sendStatus", "发送状态", 14, x => OutsourceSendStatuses.Label(x.SendStatus));

    /// <summary>Columns of the vendor mail body table.</summary>
    internal static readonly string[] MailBodyKeys = ["workOrderNo", "customer", "machineModel", "type", "quantity", "latestDueDate", "status", "remark"];

    internal static XlsxFill RowFill(string status) => status switch
    {
        OutsourceItemStatuses.Paused => XlsxFill.Red,
        OutsourceItemStatuses.Cancelled => XlsxFill.Gray,
        _ => XlsxFill.None,
    };

    /// <summary>Field values as mailed; stored per item and compared at the next send.</summary>
    internal static Dictionary<string, string?> Snapshot(OutsourceItemResponse item) =>
        Fields.ToDictionary(field => field.Key, field => field.Value(item), StringComparer.Ordinal);

    /// <summary>Keys whose value differs from the snapshot; empty when there is no snapshot.</summary>
    internal static IReadOnlySet<string> Changed(OutsourceItemResponse item, IReadOnlyDictionary<string, string?>? snapshot)
    {
        if (snapshot is null) return new HashSet<string>(StringComparer.Ordinal);
        return Fields.Where(field => !snapshot.TryGetValue(field.Key, out var before) || before != field.Value(item))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <param name="changed">Per item id, the changed field keys (yellow cells); null for none.</param>
    /// <param name="forVendor">Vendor attachments leave out the internal send status.</param>
    internal static byte[] Build(IReadOnlyList<OutsourceItemResponse> items, IReadOnlyDictionary<ulong, IReadOnlySet<string>>? changed,
        bool forVendor = false)
    {
        var fields = forVendor ? Fields : [.. Fields, SendStatus];
        var headers = new[] { "序号" }.Concat(fields.Select(field => field.Header)).ToArray();
        var widths = new[] { 6d }.Concat(fields.Select(field => field.Width)).ToArray();
        var rows = items.Select((item, index) =>
        {
            var keys = changed?.GetValueOrDefault(item.Id);
            var highlighted = keys is null ? null : fields.Select((field, column) => (field, column))
                .Where(x => keys.Contains(x.field.Key)).Select(x => x.column + 1).ToHashSet();
            return new XlsxRow(
                [(index + 1).ToString(CultureInfo.InvariantCulture), .. fields.Select(field => field.Value(item))],
                RowFill(item.Status), highlighted);
        }).ToArray();
        return SimpleXlsx.Write("外协清单", headers, rows, widths);
    }
}

/// <summary>Field labels and writers for <c>outsource_item_changes</c>.</summary>
internal static class OutsourceHistory
{
    internal const string Edit = "EDIT";
    internal const string IpmsScheduled = "IPMS_SCHEDULED";
    internal const string IpmsManual = "IPMS_MANUAL";
    private const int MaximumValueLength = 500;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["outsourceDate"] = "外发日期", ["vendor"] = "外发厂商", ["remark"] = "备注", ["status"] = "状态",
        ["customer"] = "客户", ["machineModel"] = "机型", ["mechanicalSection"] = "机构课别",
        ["mechanicalOwner"] = "机构执行人", ["electricalOwner"] = "电控执行人", ["workOrderIssuedOn"] = "工令下发",
        ["bomOn"] = "BOM时间", ["drawingReleasedOn"] = "发图", ["typeCode"] = "外发类型", ["quantity"] = "数量",
        ["latestDueDate"] = "最新交期",
    };

    internal static string Label(string field) => Labels.GetValueOrDefault(field, field);

    internal static void Add(YfDbContext context, ulong itemId, IEnumerable<AuditChange> changes, string source, ulong? actorUserId)
    {
        static string? Value(object? value) => value switch
        {
            null => null,
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() is { Length: > MaximumValueLength } text ? text[..MaximumValueLength] : value.ToString(),
        };
        context.OutsourceItemChanges.AddRange(changes.Select(change => new OutsourceItemChange
        {
            ItemId = itemId, Field = change.Field, OldValue = Value(change.Before), NewValue = Value(change.After),
            Source = source, ActorUserId = actorUserId,
        }));
    }
}
