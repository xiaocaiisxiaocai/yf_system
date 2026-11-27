using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Oem.Common;
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
    /// <summary>
    /// The sender decided an unsent item needs no mail (2026-11-19). Later edits keep it so: only an item that was
    /// mailed (SENT) turns PENDING_NOTICE again. It can still be mailed at any time.
    /// </summary>
    internal const string NotNeeded = "NOT_NEEDED";
    internal static bool IsValid(string? value) => value is Unsent or Sent or PendingNotice or NotNeeded;
    internal static string Label(string value) => value switch
    {
        Sent => "已发送", PendingNotice => "有变更待通知", NotNeeded => "无需发送", _ => "未发送",
    };
    /// <summary>What waits for a sender to decide: mail it or mark it as needing no notice.</summary>
    internal static bool AwaitsSender(string value) => value is Unsent or PendingNotice;
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
    string? Keyword, bool IncludeClosed, bool SendQueue = false, IReadOnlyList<string>? WorkOrders = null)
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
        var sendQueue = request.Query["sendQueue"].ToString() switch
        {
            "" or "false" => false,
            "true" => true,
            _ => throw ApiException.BadRequest("请求参数错误: sendQueue"),
        };
        var keyword = request.Query["keyword"].ToString().Trim();
        var workOrders = OutsourceFilterTerms.Split(request.Query["workOrders"].ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(101).ToArray();
        if (workOrders.Length > 100 || workOrders.Any(term => term.Length > 64))
            throw ApiException.BadRequest("请求参数错误: workOrders");
        return new(QueryValues.OptionalUInt64(request, "vendorId"), QueryValues.OptionalUInt64(request, "typeId"),
            status.Length == 0 ? null : status, sendStatus.Length == 0 ? null : sendStatus,
            Date(request, "dateFrom"), Date(request, "dateTo"), keyword.Length == 0 ? null : keyword[..Math.Min(keyword.Length, 64)],
            includeClosed, sendQueue, workOrders);
    }
}

/// <summary>Exported workbook bytes and its download name.</summary>
public sealed record OutsourceExport(byte[] Content, string FileName);

/// <summary>
/// The outsourcing list itself: import (preview then confirm, both against IPMS), the overall table and per-vendor
/// tabs, export, purchasing edits and deletion. See 外协清单契约 → 汇入 / 采购编辑与删除 / 页面.
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
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Import);
        var planned = await PlanImportAsync(context, rows, ct);
        return new(planned.Count(x => x.Status == "READY"), planned.Count(x => x.Status == "SKIPPED"),
            planned.Count(x => x.Status == "INVALID"), planned.Select(x => x.Preview).ToArray());
    }

    /// <summary>Re-plans against IPMS and the database, then stores only READY rows in one transaction.</summary>
    public async Task<AdminImportResponse> ImportAsync(CurrentUser actor, OutsourceImportRequest request, CancellationToken ct)
    {
        var rows = ValidateRowCount(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Import);
        var planned = await PlanImportAsync(context, rows, ct);
        await context.Database.CloseConnectionAsync();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Import);
        var ready = planned.Where(x => x.Status == "READY").ToArray();
        var vendorIds = ready.Select(x => x.VendorId).Distinct().ToArray();
        var activeVendors = await context.OemCompanies.Where(x => vendorIds.Contains(x.Id) && x.Status == AccountStatuses.Active)
            .Select(x => x.Id).ToArrayAsync(ct);
        if (activeVendors.Length != vendorIds.Length) throw ApiException.Conflict("外协厂商状态已变化，请重新预览");
        var items = ready.Select(x => x.Item!).ToArray();
        var serial = await context.OutsourceItems.MaxAsync(x => (uint?)x.SerialNo, ct) ?? 0;
        foreach (var item in items) { item.CreatedBy = actor.Id; item.SerialNo = ++serial; }
        context.OutsourceItems.AddRange(items);
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 } duplicate)
        {
            throw ApiException.Conflict(duplicate.Message.Contains("uk_outsource_items_serial", StringComparison.Ordinal)
                ? "另一批汇入刚刚完成，请重新预览后再汇入" : "部分工令已被他人汇入，请重新预览");
        }
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
        var vendorsByName = await context.OemCompanies.AsNoTracking()
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
                Customer = reference.CustomerShown(order.Customer), MachineModel = order.MachineModel, TypeLabel = lookup.TypeLabel,
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
            // IPMS only supplies the original due date; the latest one starts out the same and is then maintained by purchasing.
            item.OriginalDueDate = item.LatestDueDate = order.DueDate;
            result[lookup.Index] = new("READY", withIpms with { Status = "READY" }, lookup.VendorId, item);
        }
        return result;
    }

    /// <summary>Copies the IPMS snapshot onto an item, truncated to the column sizes.</summary>
    internal static void ApplyIpms(OutsourceItem item, IpmsWorkOrder order, DateTime syncedAt)
    {
        item.Customer = Cut(order.Customer, 128);
        item.MachineModel = Cut(order.MachineModel, 128);
        item.MechanicalOwner = MechanicalOwnerOf(order);
        item.ElectricalOwner = Cut(order.ElectricalOwner, 64);
        item.WorkOrderIssuedOn = order.WorkOrderIssuedOn;
        item.Quantity = order.Quantity;
        item.IpmsSyncedAt = syncedAt;
    }

    internal static string? MechanicalOwnerOf(IpmsWorkOrder order) => Cut(order.MechanicalOwner, 64);

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

    // ---- list, export ----

    public async Task<PageResponse<OutsourceItemResponse>> ListAsync(
        CurrentUser actor, OutsourceItemQuery query, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View);
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var filtered = Filter(context.OutsourceItems.AsNoTracking(), query, reference);
        var total = (ulong)await filtered.LongCountAsync(ct);
        var rows = await ListOrder(context, filtered)
            .Skip((int)Math.Min(offset, int.MaxValue)).Take((int)size).ToArrayAsync(ct);
        return new(await reference.JsonMarkedAsync(context, rows, ct), total, page, size);
    }

    public async Task<OutsourceItemFilesResponse> FilesAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View);
        if (!await context.OutsourceItems.AnyAsync(x => x.Id == id, ct))
            throw ApiException.NotFound("外协记录不存在或已被删除");
        var canOpen = await AccessService.HasPermissionAsync(context.Database.Connection(), null, actor.Id, OemPermissions.OutsourceFileView, ct);
        return new(canOpen, await OutsourceFiles.ListAsync(context, id, null, ct));
    }

    public async Task<OutsourceExport> ExportAsync(CurrentUser actor, OutsourceItemQuery query, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Export);
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var filtered = Filter(context.OutsourceItems.AsNoTracking(), query, reference);
        if (await filtered.CountAsync(ct) > MaximumExportRows)
            throw ApiException.BadRequest($"一次最多导出 {MaximumExportRows} 条，请缩小筛选范围");
        var rows = await ListOrder(context, filtered).ToArrayAsync(ct);
        // Cells that differ from the last mail are yellow, as in the list and the vendor attachment.
        var responses = await reference.JsonMarkedAsync(context, rows, ct);
        var changed = responses.Where(x => x.ChangedFields is { Length: > 0 })
            .ToDictionary(x => x.Id, x => (IReadOnlySet<string>)x.ChangedFields!.ToHashSet(StringComparer.Ordinal));
        var content = OutsourceWorkbook.Build(responses, changed, reference.Layout.WorkbookFields(vendor: false));
        var vendorPart = query.VendorId is { } vendorId && reference.VendorNames.TryGetValue(vendorId, out var name) ? "_" + name : "";
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new(content, $"外协清单{vendorPart}_{today}.xlsx");
    }

    /// <summary>List and export order (外协清单契约 → 清单排序): vendor name, then newest outsourcing date, then work order.</summary>
    private static IOrderedQueryable<OutsourceItem> ListOrder(YfDbContext context, IQueryable<OutsourceItem> items) =>
        items.OrderBy(x => context.OemCompanies.Where(c => c.Id == x.VendorId).Select(c => c.Name).FirstOrDefault())
            .ThenBy(x => x.VendorId).ThenByDescending(x => x.OutsourceDate).ThenBy(x => x.WorkOrderNo).ThenBy(x => x.Id);

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
        // What waits for a sender (外协清单契约 → 待发送提醒): open, never mailed or changed since the last mail.
        if (query.SendQueue)
            items = items.Where(x => x.ClosedAt == null
                && (x.SendStatus == OutsourceSendStatuses.Unsent || x.SendStatus == OutsourceSendStatuses.PendingNotice));
        if (query.DateFrom is { } from) items = items.Where(x => x.OutsourceDate >= from);
        if (query.DateTo is { } to) items = items.Where(x => x.OutsourceDate <= to);
        if (query.WorkOrders is { Count: > 0 } workOrders)
            items = items.Where(MatchWorkOrders(workOrders));
        if (query.Keyword is { } keyword)
        {
            var pattern = QueryValues.ContainsPattern(keyword);
            // The list shows short names, so a keyword also finds the customers whose short name contains it.
            var byShortName = reference.CustomersWithShortNameContaining(keyword);
            items = items.Where(x => EF.Functions.Like(x.WorkOrderNo, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(x.Customer!, pattern, QueryValues.LikeEscape)
                || byShortName.Contains(x.Customer!)
                || EF.Functions.Like(x.MachineModel!, pattern, QueryValues.LikeEscape));
        }
        return items;
    }

    internal static Expression<Func<OutsourceItem, bool>> MatchWorkOrders(IReadOnlyList<string> terms)
    {
        // Compose SQL OR clauses so filtering still happens before counting and paging.
        Expression<Func<OutsourceItem, bool>> matches = x => false;
        foreach (var term in terms)
        {
            var pattern = QueryValues.ContainsPattern(term);
            Expression<Func<OutsourceItem, bool>> like = x => EF.Functions.Like(x.WorkOrderNo, pattern, QueryValues.LikeEscape);
            matches = Expression.Lambda<Func<OutsourceItem, bool>>(
                Expression.OrElse(matches.Body, Expression.Invoke(like, matches.Parameters[0])), matches.Parameters);
        }
        return matches;
    }

    // ---- change history ----

    /// <summary>All recorded changes of one item, newest first.</summary>
    public async Task<OutsourceItemChangeResponse[]> ChangesAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View);
        if (!await context.OutsourceItems.AnyAsync(x => x.Id == id, ct)) throw ApiException.NotFound("外协记录不存在或已被删除");
        var layout = await OutsourceFieldLayout.LoadAsync(context, ct);
        var rows = await context.OutsourceItemChanges.AsNoTracking().Where(x => x.ItemId == id)
            .OrderByDescending(x => x.Id)
            .GroupJoin(context.Users, change => change.ActorUserId, user => (ulong?)user.Id, (change, users) => new { change, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, user) => new { x.change, Name = user == null ? null : user.RealName })
            .ToArrayAsync(ct);
        return rows.Select(x => new OutsourceItemChangeResponse(x.change.Id, x.change.Field,
            // Only historical IPMS syncs wrote a department name under this key.
            x.change.Field == "mechanicalSection" ? "机构课别" : layout.Label(ColumnKey(x.change.Field)),
            x.change.OldValue, x.change.NewValue, x.change.Source, x.Name, DateTime.SpecifyKind(x.change.ChangedAt, DateTimeKind.Utc),
            x.change.SignerName)).ToArray();
    }

    // ---- edits and deletion ----

    public async Task<OutsourceItemResponse> UpdateAsync(CurrentUser actor, ulong id, OutsourceItemUpdate request, CancellationToken ct)
    {
        if (request.ExpectedUpdatedAt is not { } expected) throw ApiException.BadRequest("expectedUpdatedAt 必须为当前记录的 updatedAt");
        var change = ParseChange(request.OutsourceDate, request.VendorId, request.TypeId, request.Remark, request.Status, request.LatestDueDate, requireAll: true)
            with { Custom = request.Values };
        var result = await ApplyAsync(actor, [new OutsourceItemVersion(id, expected)], change, ct);
        return result.Items.Single();
    }

    public Task<OutsourceBatchResponse> BatchUpdateAsync(CurrentUser actor, OutsourceBatchUpdate request, CancellationToken ct)
    {
        var items = ValidateBatch(request.Items);
        var change = ParseChange(request.OutsourceDate, request.VendorId, request.TypeId, request.Remark, request.Status, request.LatestDueDate, requireAll: false)
            with { Custom = request.Values };
        if (change.Date is null && change.VendorId is null && change.TypeId is null && change.Remark is null && change.Status is null && change.LatestDue is null
            && change.Custom is not { Count: > 0 })
            throw ApiException.BadRequest("请至少选择一项要修改的内容");
        return ApplyAsync(actor, items, change, ct);
    }

    public async Task<EmptyResponse> BatchDeleteAsync(CurrentUser actor, OutsourceBatchDelete request, CancellationToken ct)
    {
        var versions = ValidateBatch(request.Items);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Delete);
        var locked = await LockAsync(context, versions, ct);
        var ids = locked.Select(x => x.Id).ToArray();
        var boundItemId = await context.OemTransfers.AsNoTracking()
            .Where(x => x.OutsourceItemId.HasValue && ids.Contains(x.OutsourceItemId.Value))
            .Select(x => x.OutsourceItemId).OrderBy(x => x).FirstOrDefaultAsync(ct);
        if (boundItemId is { } bound)
            throw ApiException.Conflict($"工令 {locked.Single(x => x.Id == bound).WorkOrderNo} 已有往来文件，不能删除");

        // Deleting a sent row does not notify the vendor, so the audit
        // record says whether the row had been mailed. Its mail history keeps the work order number (no FK to items).
        await context.OutsourceItems.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var vendors = await VendorNamesAsync(context, ct);
        foreach (var item in locked)
        {
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_DELETE", "outsource_item", item.Id, new
                {
                    targetName = item.WorkOrderNo, workOrderNo = item.WorkOrderNo,
                    vendor = vendors.GetValueOrDefault(item.VendorId),
                    sendStatus = item.SendStatus,
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    /// <summary>
    /// Purchasing marks items 已完成 (or reopens them). IPMS never reports closure, so this is the only way an item closes:
    /// completed items are hidden by default, skipped by the IPMS sync, cannot be edited or mailed, and no vendor is told.
    /// </summary>
    public async Task<OutsourceBatchResponse> SetCompletionAsync(CurrentUser actor, OutsourceCompletionRequest request, CancellationToken ct)
    {
        var versions = ValidateBatch(request.Items);
        if (request.Completed is not { } completed) throw ApiException.BadRequest("请指定标记为已完成或重新开启");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Manage);
        var locked = await LockAsync(context, versions, ct);
        var now = DateTime.UtcNow;
        foreach (var before in locked)
        {
            if ((before.ClosedAt is not null) == completed) continue;
            await context.OutsourceItems.Where(x => x.Id == before.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedAt, completed ? now : null)
                    .SetProperty(x => x.ModifiedBy, actor.Id).SetProperty(x => x.ModifiedAt, now), ct);
            AuditChange[] changes = [new("completed", "完成状态", CompletionLabel(!completed), CompletionLabel(completed))];
            OutsourceHistory.Add(context, before.Id, changes, OutsourceHistory.Edit, actor.Id);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                completed ? "OUTSOURCE_ITEM_COMPLETE" : "OUTSOURCE_ITEM_REOPEN", "outsource_item", before.Id, new
                {
                    targetName = before.WorkOrderNo, changes,
                    batch = versions.Length > 1 ? versions.Length : (int?)null,
                }, null, ct);
        }
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var ids = locked.Select(x => x.Id).ToArray();
        var updated = await context.OutsourceItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        await transaction.CommitAsync(ct);
        var order = versions.Select((x, index) => (x.Id, index)).ToDictionary(x => x.Id, x => x.index);
        return new(await reference.JsonMarkedAsync(context, updated.OrderBy(x => order[x.Id]).ToArray(), ct));
    }

    internal static string CompletionLabel(bool completed) => completed ? "已完成" : "未完成";

    /// <summary>How many open items wait for a sender; shown on the outsourcing entry for accounts that may send.</summary>
    public async Task<OutsourceSendQueueResponse> SendQueueAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Send);
        var count = await context.OutsourceItems.AsNoTracking().CountAsync(x => x.ClosedAt == null
            && (x.SendStatus == OutsourceSendStatuses.Unsent || x.SendStatus == OutsourceSendStatuses.PendingNotice), ct);
        return new(count);
    }

    /// <summary>The history value that records a sender's decision not to mail; edits before it are no longer marked.</summary>
    internal const string SkipNoticeLabel = "无需通知";

    /// <summary>
    /// A sender decides not to mail these items: a changed, already sent item goes back to SENT, an unsent one becomes
    /// NOT_NEEDED. Either way the decision is the new baseline, so the cells return to their normal colours: a sent item
    /// takes its current vendor columns as "as last sent", an unsent one only marks edits made after the decision.
    /// Nothing is mailed; the decision is kept in the history and the audit log.
    /// </summary>
    public async Task<OutsourceBatchResponse> SkipNoticeAsync(CurrentUser actor, OutsourceSkipNoticeRequest request, CancellationToken ct)
    {
        var versions = ValidateBatch(request.Items);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.View, OutsourcePermissions.Send);
        var locked = await LockAsync(context, versions, ct);
        if (locked.Any(x => x.ClosedAt is not null)) throw ApiException.Conflict("已完成的记录不需要通知");
        if (locked.FirstOrDefault(x => !OutsourceSendStatuses.AwaitsSender(x.SendStatus)) is { } other)
            throw ApiException.Conflict($"工令 {other.WorkOrderNo} 不是未发送或有变更待通知的记录");
        var baseline = await OutsourceReference.LoadAsync(context, ct);
        await baseline.LoadValuesAsync(context, locked, ct);
        var vendorFields = baseline.Layout.MailWorkbookFields();
        foreach (var before in locked)
        {
            var after = before.SendStatus == OutsourceSendStatuses.PendingNotice ? OutsourceSendStatuses.Sent : OutsourceSendStatuses.NotNeeded;
            if (after == OutsourceSendStatuses.Sent)
            {
                // What the vendor columns show now becomes the comparison for the next mail.
                var snapshot = System.Text.Json.JsonSerializer.Serialize(OutsourceWorkbook.Snapshot(baseline.Json(before), vendorFields));
                await context.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.SendStatus, after)
                    .SetProperty(x => x.NotifiedVendorId, before.VendorId)
                    .SetProperty(x => x.LastSentSnapshot, snapshot), ct);
            }
            else await context.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SendStatus, after), ct);
            AuditChange[] changes = [new("sendStatus", "发送状态", OutsourceSendStatuses.Label(before.SendStatus), SkipNoticeLabel)];
            OutsourceHistory.Add(context, before.Id, changes, OutsourceHistory.Edit, actor.Id);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_ITEM_SKIP_NOTICE", "outsource_item", before.Id, new
                {
                    targetName = before.WorkOrderNo, changes,
                    sendStatus = after,
                    batch = versions.Length > 1 ? versions.Length : (int?)null,
                }, null, ct);
        }
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var ids = locked.Select(x => x.Id).ToArray();
        var updated = await context.OutsourceItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        await transaction.CommitAsync(ct);
        var order = versions.Select((x, index) => (x.Id, index)).ToDictionary(x => x.Id, x => x.index);
        return new(await reference.JsonMarkedAsync(context, updated.OrderBy(x => order[x.Id]).ToArray(), ct));
    }

    private sealed record Change(DateOnly? Date, ulong? VendorId, ulong? TypeId, string? Remark, string? Status, DateOnly? LatestDue)
    {
        /// <summary>Custom field values by key: a key that is absent keeps its value, an empty text clears it.</summary>
        public IReadOnlyDictionary<string, string?>? Custom { get; init; }
    }

    private static Change ParseChange(string? date, ulong? vendorId, ulong? typeId, string? remark, string? status, string? latestDue, bool requireAll)
    {
        DateOnly? parsedDate = null;
        // Optional even on a single edit: absent keeps the current date.
        if (date is not null)
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
        // Optional even on a single edit: absent keeps the current latest due date (an empty text never clears it).
        DateOnly? parsedLatest = null;
        if (!string.IsNullOrWhiteSpace(latestDue))
        {
            if (!DateOnly.TryParseExact(latestDue.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                throw ApiException.BadRequest("最新交期格式不正确");
            parsedLatest = due;
        }
        return new(parsedDate, vendorId, typeId, normalizedRemark, status, parsedLatest);
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
        // The account must see the list; field settings decide which changed columns it may write.
        actor = await AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.View);
        var editor = await OutsourceEditor.LoadAsync(context, actor.Id, ct);
        if (!(await OutsourceFieldLayout.LoadAsync(context, ct)).Columns.Any(column => OutsourceFieldLayout.CanEdit(column, editor)))
            throw ApiException.Forbidden();
        var locked = await LockAsync(context, versions, ct);
        if (locked.Any(x => x.ClosedAt is not null)) throw ApiException.Conflict("已完成的记录不能修改，请先重新开启");
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
        var layout = reference.Layout;
        var custom = CustomChanges(layout, change.Custom);
        await reference.LoadValuesAsync(context, locked, ct);
        var vendorKeys = layout.NoticeKeys;
        var modifiedAt = await DbClock.UtcNowAsync(context, ct, 3);
        foreach (var before in locked)
        {
            var date = change.Date ?? before.OutsourceDate;
            var vendor = change.VendorId ?? before.VendorId;
            var remark = change.Remark is null ? before.Remark : change.Remark.Length == 0 ? null : change.Remark;
            var status = change.Status ?? before.Status;
            var latestDue = change.LatestDue ?? before.LatestDueDate;
            var typeCode = TypeChanged(before, newTypeCode) ? newTypeCode : before.TypeCode;
            var changes = AuditChange.OnlyChanged(
                new AuditChange("outsourceDate", "外发日期", Text(before.OutsourceDate), Text(date)),
                new AuditChange("vendor", "外发厂商", reference.VendorNames.GetValueOrDefault(before.VendorId), reference.VendorNames.GetValueOrDefault(vendor)),
                new AuditChange("typeCode", "外发类型", reference.Label(before.TypeCode), reference.Label(typeCode)),
                new AuditChange("remark", "备注", before.Remark, remark),
                new AuditChange("status", "状态", OutsourceItemStatuses.Label(before.Status), OutsourceItemStatuses.Label(status)),
                new AuditChange("latestDueDate", "最新交期", before.LatestDueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latestDue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            var current = reference.ValuesOf(before.Id);
            var customChanges = custom.Where(x => current.GetValueOrDefault(x.Column.Key) != x.Value)
                .Select(x => new AuditChange(x.Column.Key, x.Column.Label,
                    OutsourceFieldValues.Display(x.Column.Field.DataType, current.GetValueOrDefault(x.Column.Key)),
                    OutsourceFieldValues.Display(x.Column.Field.DataType, x.Value))).ToArray();
            changes = [.. changes, .. customChanges];
            if (changes.Length == 0) continue;
            // Each changed column must be one this account may fill in (外协清单契约 → 栏位设定); an unchanged one is never checked.
            foreach (var key in changes.Select(x => ColumnKey(x.Field)))
            {
                if (layout.Find(key) is not { } column || !OutsourceFieldLayout.CanEdit(column, editor))
                    throw ApiException.Forbidden($"无权修改栏位「{layout.Label(key)}」");
            }
            // Only what vendors see needs a new notice; a change to an internal-only column keeps the send status.
            var noticeNeeded = changes.Any(x => vendorKeys.Contains(ColumnKey(x.Field)));
            var sendStatus = noticeNeeded && before.SendStatus == OutsourceSendStatuses.Sent ? OutsourceSendStatuses.PendingNotice : before.SendStatus;
            await context.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OutsourceDate, date).SetProperty(x => x.VendorId, vendor).SetProperty(x => x.TypeCode, typeCode)
                .SetProperty(x => x.Remark, remark).SetProperty(x => x.Status, status).SetProperty(x => x.LatestDueDate, latestDue)
                .SetProperty(x => x.SendStatus, sendStatus).SetProperty(x => x.ModifiedBy, actor.Id).SetProperty(x => x.ModifiedAt, modifiedAt), ct);
            await OutsourceItemValues.WriteAsync(context, before.Id,
                custom.Where(x => customChanges.Any(c => c.Field == x.Column.Key)).Select(x => (x.Column.Field.Id, x.Value)), ct);
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
        reference.ForgetValues(ids);
        await transaction.CommitAsync(ct);
        var order = versions.Select((x, index) => (x.Id, index)).ToDictionary(x => x.Id, x => x.index);
        return new(await reference.JsonMarkedAsync(context, updated.OrderBy(x => order[x.Id]).ToArray(), ct));
    }

    /// <summary>The column key an audit change belongs to (the type is recorded as <c>typeCode</c>).</summary>
    internal static string ColumnKey(string field) => field == "typeCode" ? "type" : field;

    private sealed record CustomChange(OutsourceFieldLayout.Column Column, string? Value);

    /// <summary>Validates requested custom values: only active custom columns, each value valid for its type.</summary>
    private static CustomChange[] CustomChanges(OutsourceFieldLayout layout, IReadOnlyDictionary<string, string?>? values)
    {
        if (values is null || values.Count == 0) return [];
        return values.Select(entry =>
        {
            var column = layout.Find(entry.Key);
            if (column is null || column.BuiltIn is not null || !column.Active) throw ApiException.BadRequest($"栏位「{entry.Key}」不存在或已停用");
            return new CustomChange(column, OutsourceFieldValues.Normalize(column.Field.DataType, column.Options, column.Label, entry.Value));
        }).ToArray();
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
        context.OemCompanies.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

    // ---- authorization ----

    /// <summary>Rechecks the internal account and requires every supplied action permission.</summary>
    internal static async Task AuthorizeReadAsync(YfDbContext context, CurrentUser actor, CancellationToken ct, params string[] permissions)
    {
        AccessService.RequireInternal(actor);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        await RequirePermissionsAsync(connection, null, actor, permissions, ct);
    }

    internal static async Task<CurrentUser> AuthorizeWriteAsync(YfDbContext context, CurrentUser actor, CancellationToken ct, params string[] permissions)
    {
        AccessService.RequireInternal(actor);
        var connection = context.Database.Connection();
        var transaction = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor);
        await RequirePermissionsAsync(connection, transaction, actor, permissions, ct);
        return actor;
    }

    private static async Task RequirePermissionsAsync(MySqlConnection connection, MySqlTransaction? transaction,
        CurrentUser actor, string[] required, CancellationToken ct)
    {
        if (required.Length == 0) return;
        var granted = await AccessService.PermissionCodesAsync(connection, transaction, actor.Id, ct);
        if (!required.All(permission => granted.Contains(permission, StringComparer.Ordinal))) throw ApiException.Forbidden();
    }

    internal static IReadOnlyList<string> TemplateHeaders => ImportHeaders;
}

/// <summary>Type dictionary, vendor mapping and vendor names, loaded once per request to resolve items.</summary>
internal sealed class OutsourceReference
{
    private readonly Dictionary<string, (ulong Id, string Code, string Name, bool AllowElectricalHandoff)> types;
    private readonly HashSet<(ulong TypeId, ulong VendorId)> mapping;
    public IReadOnlyDictionary<ulong, string> VendorNames { get; }
    public IReadOnlySet<ulong> ActiveVendorIds { get; }
    /// <summary>客户全称 → 简称, keyed by <see cref="OutsourceSettingsService.CustomerKey"/>.</summary>
    private readonly Dictionary<string, string> customerShortNames;
    /// <summary>The same key → the full name as stored in the table.</summary>
    private readonly Dictionary<string, string> customerFullNames;
    /// <summary>The configured columns (外协清单契约 → 栏位设定).</summary>
    public OutsourceFieldLayout Layout { get; }
    /// <summary>Directory and custom values for the items loaded in this request.</summary>
    private readonly Dictionary<ulong, (string? Supervisor, Dictionary<string, string?> Custom)> values = [];

    private OutsourceReference(Dictionary<string, (ulong, string, string, bool)> types, HashSet<(ulong, ulong)> mapping,
        Dictionary<ulong, string> vendorNames, HashSet<ulong> activeVendorIds,
        Dictionary<string, (string FullName, string ShortName)> customerShortNames, OutsourceFieldLayout layout)
    {
        this.types = types; this.mapping = mapping; VendorNames = vendorNames; ActiveVendorIds = activeVendorIds; Layout = layout;
        this.customerShortNames = customerShortNames.ToDictionary(x => x.Key, x => x.Value.ShortName, StringComparer.Ordinal);
        customerFullNames = customerShortNames.ToDictionary(x => x.Key, x => x.Value.FullName, StringComparer.Ordinal);
    }

    public string[] CustomersWithShortNameContaining(string keyword) =>
        customerFullNames.Where(x => customerShortNames[x.Key].Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value).ToArray();

    /// <summary>The short name the list shows for a customer; the full name when the table has none.</summary>
    public string? CustomerShown(string? fullName) =>
        !string.IsNullOrWhiteSpace(fullName) && customerShortNames.TryGetValue(OutsourceSettingsService.CustomerKey(fullName), out var shortName)
            ? shortName : fullName;

    public static async Task<OutsourceReference> LoadAsync(YfDbContext context, CancellationToken ct)
    {
        // Only active types match; a disabled type behaves like an unknown one until it is re-enabled.
        var types = await context.OutsourceTypes.AsNoTracking().Where(x => x.Status == AccountStatuses.Active)
            .Select(x => new { x.Id, x.Code, x.Name, x.AllowElectricalHandoff }).ToArrayAsync(ct);
        var mapping = await context.OutsourceTypeVendors.AsNoTracking().Select(x => new { x.TypeId, x.VendorId }).ToArrayAsync(ct);
        var vendors = await context.OemCompanies.AsNoTracking().Select(x => new { x.Id, x.Name, x.Status }).ToArrayAsync(ct);
        var aliases = await context.OutsourceCustomerAliases.AsNoTracking().Select(x => new { x.FullName, x.ShortName }).ToArrayAsync(ct);
        var layout = await OutsourceFieldLayout.LoadAsync(context, ct);
        return new(types.ToDictionary(x => Normalize(x.Code)!, x => (x.Id, x.Code, x.Name, x.AllowElectricalHandoff)),
            mapping.Select(x => (x.TypeId, x.VendorId)).ToHashSet(),
            vendors.ToDictionary(x => x.Id, x => x.Name),
            vendors.Where(x => x.Status == AccountStatuses.Active).Select(x => x.Id).ToHashSet(),
            aliases.GroupBy(x => OutsourceSettingsService.CustomerKey(x.FullName))
                .ToDictionary(x => x.Key, x => (x.First().FullName, x.First().ShortName), StringComparer.Ordinal), layout);
    }

    /// <summary>Batch-loads the current supervisor and stored custom fields for a set of items.</summary>
    public async Task LoadValuesAsync(YfDbContext context, IReadOnlyCollection<OutsourceItem> items, CancellationToken ct)
    {
        var missing = items.Where(item => !values.ContainsKey(item.Id)).ToArray();
        if (missing.Length == 0) return;
        // The directory lookup is only needed while the column is on; a disabled column is never shown, mailed or compared.
        var showsSupervisor = Layout.Everything.Any(column => column.Key == "supervisor" && column.Active);
        var supervisors = showsSupervisor
            ? await MechanicalSupervisorDirectory.ResolveAsync(context, missing.Select(item => item.MechanicalOwner), ct)
            : new Dictionary<string, string>();
        foreach (var item in missing)
        {
            var supervisor = item.MechanicalOwner is { } owner ? supervisors.GetValueOrDefault(owner) : null;
            values[item.Id] = (supervisor, new Dictionary<string, string?>(StringComparer.Ordinal));
        }
        var keys = Layout.Everything.Where(x => x.BuiltIn is null).ToDictionary(x => x.Field.Id, x => x.Key);
        if (keys.Count == 0) return;
        var ids = missing.Select(item => item.Id).ToArray();
        var rows = await context.OutsourceItemValues.AsNoTracking().Where(x => ids.Contains(x.ItemId)).ToArrayAsync(ct);
        foreach (var row in rows)
        {
            if (keys.TryGetValue(row.FieldId, out var key)) values[row.ItemId].Custom[key] = row.Value;
        }
    }

    /// <summary>Drops loaded values after a write, so the next <see cref="LoadValuesAsync"/> reads them again.</summary>
    public void ForgetValues(IEnumerable<ulong> itemIds)
    {
        foreach (var id in itemIds) values.Remove(id);
    }

    /// <summary>The stored custom values of a loaded item by field key.</summary>
    public IReadOnlyDictionary<string, string?> ValuesOf(ulong itemId) =>
        values.TryGetValue(itemId, out var found) ? found.Custom : new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The active custom values the list shows, by field key.</summary>
    private Dictionary<string, string?> ActiveValuesOf(ulong itemId)
    {
        var stored = ValuesOf(itemId);
        return Layout.Columns.Where(x => x.BuiltIn is null)
            .ToDictionary(x => x.Key, x => stored.GetValueOrDefault(x.Key), StringComparer.Ordinal);
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

    public (ulong Id, string Code, string Name, bool AllowElectricalHandoff)? Find(string? raw) =>
        Normalize(raw) is { } key && types.TryGetValue(key, out var type) ? type : null;

    public bool AllowsElectricalHandoff(string? typeCode) => Find(typeCode)?.AllowElectricalHandoff ?? true;

    public string Match(string? rawType, ulong vendorId) => Find(rawType) is not { } type
        ? OutsourceMatchStates.UnknownType
        : mapping.Contains((type.Id, vendorId)) ? OutsourceMatchStates.Ok : OutsourceMatchStates.VendorMismatch;

    public string? Label(string? rawType) => Find(rawType) is { } type
        ? OutsourceSettingsService.Label(type.Code, type.Name) : rawType?.Trim();

    public string? CodeOf(ulong typeId) => types.Values.Where(x => x.Id == typeId).Select(x => x.Code).FirstOrDefault();

    /// <summary>
    /// Responses for the list screens. An item never mailed to its current vendor has nothing to compare with, so the
    /// fields purchasing edited by hand are marked instead; once it is mailed, the comparison with that mail takes over.
    /// </summary>
    public async Task<OutsourceItemResponse[]> JsonMarkedAsync(YfDbContext context, IReadOnlyList<OutsourceItem> items, CancellationToken ct)
    {
        await LoadValuesAsync(context, items, ct);
        var responses = items.Select(Json).ToArray();
        // Who imported and who last edited each row, by name.
        var userIds = items.SelectMany(x => new[] { x.CreatedBy, x.ModifiedBy }).OfType<ulong>().Distinct().ToArray();
        var names = userIds.Length == 0 ? [] : await context.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.RealName, ct);
        var byId = items.ToDictionary(x => x.Id);
        var counts = await OutsourceFiles.CountsAsync(context, byId.Keys, null, ct);
        responses = responses.Select(response => response with
        {
            ImportedBy = byId[response.Id].CreatedBy is { } importer ? names.GetValueOrDefault(importer) : null,
            ModifiedBy = byId[response.Id].ModifiedBy is { } editor ? names.GetValueOrDefault(editor) : null,
            FileCount = counts.GetValueOrDefault(response.Id).FileCount,
            PurgedFileCount = counts.GetValueOrDefault(response.Id).PurgedFileCount,
        }).ToArray();
        var unsent = items.Where(x => x.NotifiedVendorId != x.VendorId || x.LastSentSnapshot is null).Select(x => x.Id).ToArray();
        if (unsent.Length == 0) return responses;
        var edits = await context.OutsourceItemChanges.AsNoTracking()
            // Changes made by hand mark the cell: list edits and the BOM时间/发图 import (not IPMS syncs).
            .Where(x => unsent.Contains(x.ItemId) && (x.Source == OutsourceHistory.Edit || x.Source == OutsourceHistory.DateImport))
            .Select(x => new { x.Id, x.ItemId, x.Field, x.NewValue }).ToArrayAsync(ct);
        // A sender's "no notice needed" is a new baseline: only edits made after it are marked.
        var skipped = edits.Where(x => x.Field == "sendStatus" && x.NewValue == OutsourceItemService.SkipNoticeLabel)
            .GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Max(change => change.Id));
        var edited = edits.Where(x => x.Field is not ("sendStatus" or "completed") && x.Id > skipped.GetValueOrDefault(x.ItemId))
            .ToLookup(x => x.ItemId, x => OutsourceItemService.ColumnKey(x.Field));
        return responses.Select(response => edited[response.Id].Any()
            ? response with { ChangedFields = [.. edited[response.Id].Distinct()] } : response).ToArray();
    }

    public OutsourceItemResponse Json(OutsourceItem item) => new OutsourceItemResponse(
        item.Id, item.SerialNo, item.WorkOrderNo, item.OutsourceDate, item.VendorId, VendorNames.GetValueOrDefault(item.VendorId) ?? "",
        CustomerShown(item.Customer), item.MachineModel, values[item.Id].Supervisor, item.MechanicalOwner, item.ElectricalOwner,
        item.WorkOrderIssuedOn, item.BomOn, item.DrawingReleasedOn,
        item.TypeCode, Find(item.TypeCode)?.Id, Label(item.TypeCode), item.Quantity, item.OriginalDueDate, item.LatestDueDate,
        item.Remark, item.Status, item.SendStatus, Match(item.TypeCode, item.VendorId), item.ClosedAt is not null,
        item.IpmsSyncedAt, item.UpdatedAt, item.ElectricalHandoff,
        item.ElectricalHandoffAt is { } handoffAt ? DateTime.SpecifyKind(handoffAt, DateTimeKind.Utc) : null, item.ElectricalHandoffSigner,
        null, null, CreatedAtOf(item), null, item.ModifiedAt, item.Customer,
        ActiveValuesOf(item.Id)).Marked(item, Layout);

    private static DateTime? CreatedAtOf(OutsourceItem item) => item.CreatedBy is null ? null : item.CreatedAt;
}

/// <summary>
/// The outsourcing-list columns, shared by the export, vendor mail bodies and attachments, and the "as last sent"
/// snapshot, so the same field is compared and highlighted everywhere.
/// </summary>
internal static class OutsourceChangeMarks
{
    /// <summary>
    /// Marks the columns that differ from the last mail to the item's current vendor (none before it was ever sent).
    /// Only the columns included in the notice are compared.
    /// </summary>
    internal static OutsourceItemResponse Marked(this OutsourceItemResponse response, OutsourceItem item, OutsourceFieldLayout layout)
    {
        var snapshot = LastNoticeValues(item);
        return snapshot is null ? response
            : response with { ChangedFields = [.. OutsourceWorkbook.Changed(response, snapshot, layout.MailWorkbookFields())] };
    }

    internal static Dictionary<string, string?>? LastNoticeValues(OutsourceItem item)
    {
        if (item.NotifiedVendorId != item.VendorId || item.LastSentSnapshot is not { } json) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

internal static class OutsourceWorkbook
{
    internal sealed record Field(string Key, string Header, double Width, Func<OutsourceItemResponse, string?> Value);

    private static readonly Field SendStatus = new("sendStatus", "发送状态", 14, x => OutsourceSendStatuses.Label(x.SendStatus));

    /// <summary>Columns of the vendor mail body table.</summary>
    internal static readonly string[] MailBodyKeys = ["workOrderNo", "customer", "machineModel", "type", "quantity", "latestDueDate", "status", "remark"];

    internal static XlsxFill RowFill(string status) => status switch
    {
        OutsourceItemStatuses.Paused => XlsxFill.Red,
        OutsourceItemStatuses.Cancelled => XlsxFill.PaleYellow,
        _ => XlsxFill.Green,
    };

    /// <summary>Values of the vendor columns as mailed; stored per item and compared at the next send.</summary>
    internal static Dictionary<string, string?> Snapshot(OutsourceItemResponse item, IReadOnlyList<Field> fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value(item), StringComparer.Ordinal);

    /// <summary>
    /// Stored by migration in place of the 机构课别 name that snapshots held before the column became 上级主管: no real baseline,
    /// so it never counts as a change and the next IPMS sync replaces it with the current supervisor.
    /// </summary>
    internal const string LegacyBaseline = "~legacy-section~";

    /// <summary>Keys whose value differs from the snapshot; empty when there is no snapshot.</summary>
    internal static IReadOnlySet<string> Changed(OutsourceItemResponse item, IReadOnlyDictionary<string, string?>? snapshot, IReadOnlyList<Field> fields)
    {
        if (snapshot is null) return new HashSet<string>(StringComparer.Ordinal);
        return fields.Where(field => !snapshot.TryGetValue(field.Key, out var before)
                || (before != field.Value(item) && before != LegacyBaseline))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <param name="changed">Per item id, the changed field keys (yellow cells); null for none.</param>
    /// <param name="columns">The configured columns: every active one for the internal export, the vendor's for an attachment.</param>
    /// <param name="forVendor">Vendor attachments leave out the internal send status.</param>
    internal static byte[] Build(IReadOnlyList<OutsourceItemResponse> items, IReadOnlyDictionary<ulong, IReadOnlySet<string>>? changed,
        IReadOnlyList<Field> columns, bool forVendor = false)
    {
        var fields = forVendor ? columns.ToArray() : [.. columns, SendStatus];
        var headers = new[] { "序号" }.Concat(fields.Select(field => field.Header)).ToArray();
        var widths = new[] { 6d }.Concat(fields.Select(field => field.Width)).ToArray();
        var rows = items.Select(item =>
        {
            var keys = changed?.GetValueOrDefault(item.Id);
            var highlighted = keys is null ? null : fields.Select((field, column) => (field, column))
                .Where(x => keys.Contains(x.field.Key)).Select(x => x.column + 1).ToHashSet();
            return new XlsxRow(
                [item.SerialNo.ToString(CultureInfo.InvariantCulture), .. fields.Select(field => field.Value(item))],
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
    /// <summary>BOM时间 / 发图 written by their Excel import.</summary>
    internal const string DateImport = "DATE_IMPORT";
    /// <summary>Filled in by the vendor in the portal; the change records the account and its signature.</summary>
    internal const string Vendor = "VENDOR";
    private const int MaximumValueLength = 500;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["outsourceDate"] = "外发日期", ["vendor"] = "外发厂商", ["remark"] = "备注", ["status"] = "状态",
        ["customer"] = "客户", ["machineModel"] = "机型", ["supervisor"] = "机构课别",
        ["mechanicalOwner"] = "机构执行人", ["electricalOwner"] = "电控执行人", ["workOrderIssuedOn"] = "工令下发",
        ["bomOn"] = "BOM时间", ["drawingReleasedOn"] = "发图", ["typeCode"] = "外发类型", ["quantity"] = "数量",
        ["latestDueDate"] = "最新交期", ["completed"] = "完成状态", ["sendStatus"] = "发送状态",
        [OutsourceBuiltInFields.ElectricalHandoffKey] = OutsourceBuiltInFields.ElectricalHandoffLabel,
        [OutsourceBuiltInFields.FilesKey] = OutsourceBuiltInFields.FilesLabel,
    };

    internal static string Label(string field) => Labels.GetValueOrDefault(field, field);

    internal static void Add(YfDbContext context, ulong itemId, IEnumerable<AuditChange> changes, string source, ulong? actorUserId,
        ulong? oemAccountId = null, string? signerName = null)
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
            Source = source, ActorUserId = actorUserId, ActorOemAccountId = oemAccountId, SignerName = signerName,
        }));
    }
}

/// <summary>Writes custom field values: a null value deletes the row (empty), anything else replaces it.</summary>
internal static class OutsourceItemValues
{
    internal static async Task WriteAsync(YfDbContext context, ulong itemId, IEnumerable<(ulong FieldId, string? Value)> values, CancellationToken ct)
    {
        foreach (var (fieldId, value) in values)
        {
            await context.OutsourceItemValues.Where(x => x.ItemId == itemId && x.FieldId == fieldId).ExecuteDeleteAsync(ct);
            if (value is not null) context.OutsourceItemValues.Add(new OutsourceItemValue { ItemId = itemId, FieldId = fieldId, Value = value });
        }
        await context.SaveChangesAsync(ct);
    }
}
