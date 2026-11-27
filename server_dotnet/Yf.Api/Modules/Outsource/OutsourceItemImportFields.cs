using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

public sealed partial class OutsourceItemService
{
    private static bool HasOptionalValues(OutsourceImportRow[] rows) =>
        rows.Any(row => row.FieldValues?.Values.Any(v => !string.IsNullOrWhiteSpace(v)) == true);

    // Runs after IPMS enrichment, inside the authorized transaction on confirmation. Both paths
    // use the same field settings, normalization and final values.
    private static async Task CompleteImportRowsAsync(YfDbContext db, OutsourceReference reference, CurrentUser actor,
        OutsourceImportRow[] inputs, List<PlannedRow> plans, CancellationToken ct)
    {
        var confirming = db.Database.CurrentTransaction is not null;
        var existing = await ExistingImportItemsAsync(db, inputs, confirming, ct);
        var existingIds = existing.Values.Select(x => x.Id).ToArray();
        var stored = (await db.OutsourceItemValues.AsNoTracking().Where(value => existingIds.Contains(value.ItemId)).ToArrayAsync(ct))
            .ToLookup(value => value.ItemId);
        bool hasOptional = HasOptionalValues(inputs);
        var editor = hasOptional || existing.Count != 0 ? await OutsourceEditor.LoadAsync(db, actor.Id, ct) : null;
        var canImportDates = hasOptional && await AccessService.HasPermissionAsync(db.Database.Connection(),
            db.Database.Transaction(), actor.Id, OutsourcePermissions.DateImport, ct);
        for (int index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            var input = inputs[index];
            var before = existing.GetValueOrDefault(plan.Preview.WorkOrderNo);
            if (confirming && (input.ExpectedItemId.HasValue || input.ExpectedUpdatedAt.HasValue || input.ExpectedChangeHash is not null)
                && (before is null || before.Id != input.ExpectedItemId || input.ExpectedUpdatedAt is not { } expected
                    || Utc(before.UpdatedAt) != Utc(expected) || string.IsNullOrEmpty(input.ExpectedChangeHash)))
                throw ApiException.VersionConflict($"工令 {plan.Preview.WorkOrderNo} 的记录已变化，请重新预览");
            if (plan.Item is not { } item) continue;
            try
            {
                if (before?.ClosedAt is not null) throw ApiException.BadRequest("已完成的记录不能覆盖，请先重新开启");
                if (before is not null && confirming && !input.ExpectedItemId.HasValue)
                {
                    plans[index] = plan with { Status = "SKIPPED", Item = null, Preview = plan.Preview with
                        { Status = "SKIPPED", Message = "工令已存在；请重新预览并确认覆盖" } };
                    continue;
                }
                if (input.FieldValues is { Count: > 0 } fields)
                    foreach (var (key, value) in fields)
                        if (!string.IsNullOrWhiteSpace(value))
                            ApplyImportField(item, reference.Layout, editor!, canImportDates, key, value.Trim(), plan.StoredValues);
                item.LatestDueDate ??= item.OriginalDueDate;
                if (before is not null)
                {
                    PreserveUnspecified(item, before, input.FieldValues);
                    var changes = ImportChanges(reference, before, item, stored[before.Id], plan.StoredValues, editor!);
                    var status = changes.Length == 0 ? "SKIPPED" : "READY_UPDATE";
                    plans[index] = plan with { Before = before, Changes = changes, Status = status, Preview = plan.Preview with
                    {
                        Status = status, Message = changes.Length == 0 ? "与现有资料相同" : null,
                        ExpectedItemId = before.Id, ExpectedUpdatedAt = Utc(before.UpdatedAt),
                        ChangedFields = changes.Select(c => ImportColumnKey(c.Field)).Distinct().ToArray(),
                    } };
                }
            }
            catch (ApiException e) when (e.Status == 400)
            {
                plans[index] = new("INVALID", plan.Preview with { Status = "INVALID", Message = e.Message }, 0, null);
            }
        }

        var rows = plans.Where(plan => plan.Item is not null).ToArray();
        if (rows.Length == 0) return;
        var supervisorColumn = reference.Layout.Find("supervisor");
        var supervisors = supervisorColumn?.Active == true
            ? await MechanicalSupervisorDirectory.ResolveAsync(db,
                rows.Select(p => p.Item!.MechanicalOwner).Concat(rows.Select(p => p.Before?.MechanicalOwner)), ct)
            : new Dictionary<string, string>();
        string? SupervisorOf(string? owner) => owner is null ? null : supervisors.GetValueOrDefault(owner);
        var shownColumns = reference.Layout.Columns.Where(c => c.Category != OutsourceFieldCategories.Fixed
            && c.Key is not ("outsourceDate" or "vendor" or "type")).ToArray();
        for (int index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            if (plan.Item is not { } item) continue;
            var values = plan.Before is null ? new Dictionary<ulong, string>() : stored[plan.Before.Id].ToDictionary(x => x.FieldId, x => x.Value);
            foreach (var (id, value) in plan.StoredValues) values[id] = value;
            var custom = shownColumns.Where(c => c.BuiltIn is null && values.ContainsKey(c.Field.Id))
                .ToDictionary(c => c.Key, c => (string?)values[c.Field.Id]);
            var supervisor = SupervisorOf(item.MechanicalOwner);
            if (plan.Before is { } previous && supervisorColumn?.Active == true && SupervisorOf(previous.MechanicalOwner) != supervisor)
            {
                plan = plan with
                {
                    Changes = [.. plan.Changes, new("supervisor", supervisorColumn.Label, SupervisorOf(previous.MechanicalOwner), supervisor)],
                    Preview = plan.Preview with { ChangedFields = [.. plan.Preview.ChangedFields!, "supervisor"] },
                };
            }
            var response = reference.Describe(item, supervisor, custom);
            var display = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in shownColumns)
                if (column.Value(response) is { Length: > 0 } value) display.Add(column.Key, value);
            var hash = plan.Status == "READY_UPDATE" ? ChangeHash(plan, reference, display) : null;
            if (confirming && inputs[index].ExpectedChangeHash is { } expectedHash
                && !string.Equals(expectedHash, hash, StringComparison.Ordinal))
                throw ApiException.VersionConflict($"工令 {item.WorkOrderNo} 的预览内容已变化，请重新预览");
            plans[index] = plan with { Preview = plan.Preview with { Customer = response.Customer, MachineModel = item.MachineModel,
                Quantity = item.Quantity, LatestDueDate = item.LatestDueDate, FieldValues = display, ExpectedChangeHash = hash } };
        }
    }

    private static void ApplyImportField(OutsourceItem item, OutsourceFieldLayout layout, OutsourceEditor editor, bool canImportDates,
        string key, string value, Dictionary<ulong, string> stored)
    {
        if (layout.Find(key) is not { Active: true } column) throw ApiException.BadRequest($"「{key}」不是可导入的启用栏位");
        if (column.Category == OutsourceFieldCategories.Ipms && key != "supervisor")
        {
            // The first due date is frozen on the item. Other IPMS fields are refreshed by the
            // sync worker; remember any Excel value used to fill a gap for future sparse snapshots.
            if (FillMissingIpmsValue(item, key, value, column.Label) && key != "originalDueDate")
            {
                if (column.Field.Id == 0) throw ApiException.BadRequest($"「{column.Label}」缺少栏位设定，无法保存补充值");
                stored.Add(column.Field.Id, key == "quantity" ? item.Quantity!.Value.ToString(CultureInfo.InvariantCulture) : value);
            }
            return;
        }
        if (column.Category == OutsourceFieldCategories.Imported)
        {
            if (!canImportDates) throw ApiException.BadRequest($"无权导入「{column.Label}」，请留空");
            var date = ImportDate(value, column.Label);
            if (key == "bomOn") item.BomOn = date;
            else if (key == "drawingReleasedOn") item.DrawingReleasedOn = date;
            else throw ApiException.BadRequest($"「{column.Label}」不可填写");
            return;
        }
        if (!OutsourceFieldLayout.CanEdit(column, editor)) throw ApiException.BadRequest($"「{column.Label}」不可填写或当前账号没有填写权限");
        if (column.BuiltIn is null)
        {
            stored.Add(column.Field.Id, OutsourceFieldValues.Normalize(column.Field.DataType, column.Options, column.Label, value)!);
            return;
        }
        switch (key)
        {
            case "status":
                item.Status = value switch
                {
                    "正常" or OutsourceItemStatuses.Normal => OutsourceItemStatuses.Normal,
                    "暂停" or OutsourceItemStatuses.Paused => OutsourceItemStatuses.Paused,
                    "取消" or OutsourceItemStatuses.Cancelled => OutsourceItemStatuses.Cancelled,
                    _ => throw ApiException.BadRequest("状态只能是正常、暂停或取消"),
                };
                break;
            case "remark": item.Remark = ImportText(value, column.Label, 500); break;
            case "latestDueDate": item.LatestDueDate = ImportDate(value, column.Label); break;
            default: throw ApiException.BadRequest($"「{column.Label}」不能作为可选栏位填写");
        }
    }

    // Used at import and on subsequent syncs. Return true only if the Excel value was applied.
    internal static bool FillMissingIpmsValue(OutsourceItem item, string key, string value, string label)
    {
        switch (key)
        {
            case "customer" when item.Customer is null: item.Customer = ImportText(value, label, 128); break;
            case "machineModel" when item.MachineModel is null: item.MachineModel = ImportText(value, label, 128); break;
            case "mechanicalOwner" when item.MechanicalOwner is null: item.MechanicalOwner = ImportText(value, label, 64); break;
            case "electricalOwner" when item.ElectricalOwner is null: item.ElectricalOwner = ImportText(value, label, 64); break;
            case "workOrderIssuedOn" when item.WorkOrderIssuedOn is null: item.WorkOrderIssuedOn = ImportDate(value, label); break;
            case "originalDueDate" when item.OriginalDueDate is null: item.OriginalDueDate = ImportDate(value, label); break;
            case "quantity" when item.Quantity is null:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
                    throw ApiException.BadRequest($"「{label}」需为非负整数");
                item.Quantity = quantity; break;
            default: return false;
        }
        return true;
    }

    private static string ImportText(string value, string label, int maxLength) => value.EnumerateRunes().Count() <= maxLength
        ? value : throw ApiException.BadRequest($"「{label}」最多 {maxLength} 个字符");

    private static DateOnly ImportDate(string value, string label) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw ApiException.BadRequest($"「{label}」日期格式不正确");
}
