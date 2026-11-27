using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Outsource;

public sealed partial class OutsourceItemService
{
    private static string SqlPlaceholders(int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(i => "{" + i.ToString(CultureInfo.InvariantCulture) + "}"));

    private static async Task LockImportRoutingAsync(YfDbContext db, OutsourceReference reference, List<PlannedRow> plans, CancellationToken ct)
    {
        var candidates = plans.Where(x => x.Item is not null).ToArray();
        if (candidates.Length == 0) return;
        // Mapping writers lock the parent type; the OEM directory locks the company when enabling/disabling it.
        var typeIds = candidates.Select(row => reference.Find(row.Item!.TypeCode)!.Value.Id).Distinct().Order().ToArray();
        var vendorIds = candidates.Select(row => row.VendorId).Distinct().Order().ToArray();
        await db.Database.SqlQueryRaw<ulong>("SELECT id AS Value FROM outsource_types FORCE INDEX (PRIMARY) WHERE id IN ("
            + SqlPlaceholders(typeIds.Length) + ") ORDER BY id LOCK IN SHARE MODE", typeIds.Cast<object>().ToArray()).ToArrayAsync(ct);
        await db.Database.SqlQueryRaw<ulong>("SELECT id AS Value FROM oem_companies FORCE INDEX (PRIMARY) WHERE id IN ("
            + SqlPlaceholders(vendorIds.Length) + ") ORDER BY id LOCK IN SHARE MODE", vendorIds.Cast<object>().ToArray()).ToArrayAsync(ct);
    }

    private static async Task<Dictionary<string, OutsourceItem>> ExistingImportItemsAsync(YfDbContext db,
        OutsourceImportRow[] inputs, bool lockRows, CancellationToken ct)
    {
        var numbers = inputs.Select(r => r.WorkOrderNo?.Trim()).OfType<string>().Where(ValidWorkOrder)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matches = db.OutsourceItems.AsNoTracking().Where(x => numbers.Contains(x.WorkOrderNo));
        if (!lockRows) return await matches.ToDictionaryAsync(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase, ct);
        var ids = await matches.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
        if (ids.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
        // Lock the existing rows once in primary-key order. A concurrent insert for an absent key is
        // handled by the work-order uniqueness constraint, never converted into an update.
        var query = "SELECT * FROM outsource_items FORCE INDEX (PRIMARY) WHERE id IN (" + SqlPlaceholders(ids.Length) + ") ORDER BY id FOR UPDATE";
        return await db.OutsourceItems.FromSqlRaw(query, ids.Cast<object>().ToArray()).AsNoTracking()
            .ToDictionaryAsync(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase, ct);
    }

    private static void PreserveUnspecified(OutsourceItem next, OutsourceItem before, IReadOnlyDictionary<string, string?>? values)
    {
        bool Filled(string key) => !string.IsNullOrWhiteSpace(values?.GetValueOrDefault(key));
        next.Id = before.Id;
        next.WorkOrderNo = before.WorkOrderNo;
        if (!TypeChanged(before, next.TypeCode)) next.TypeCode = before.TypeCode;
        if (!Filled("customer")) next.Customer = before.Customer;
        if (!Filled("machineModel")) next.MachineModel = before.MachineModel;
        if (!Filled("mechanicalOwner")) next.MechanicalOwner = before.MechanicalOwner;
        if (!Filled("electricalOwner")) next.ElectricalOwner = before.ElectricalOwner;
        if (!Filled("workOrderIssuedOn")) next.WorkOrderIssuedOn = before.WorkOrderIssuedOn;
        if (!Filled("quantity")) next.Quantity = before.Quantity;
        next.OriginalDueDate = before.OriginalDueDate ?? (Filled("originalDueDate") ? next.OriginalDueDate : null);
        if (!Filled("bomOn")) next.BomOn = before.BomOn;
        if (!Filled("drawingReleasedOn")) next.DrawingReleasedOn = before.DrawingReleasedOn;
        if (!Filled("latestDueDate")) next.LatestDueDate = before.LatestDueDate;
        if (!Filled("status")) next.Status = before.Status;
        if (!Filled("remark")) next.Remark = before.Remark;
    }

    private const string FallbackPrefix = "fallback:";
    private static string ImportColumnKey(string key) =>
        key.StartsWith(FallbackPrefix, StringComparison.Ordinal) ? key[FallbackPrefix.Length..] : ColumnKey(key);

    private static string ChangeHash(PlannedRow plan, OutsourceReference reference, IReadOnlyDictionary<string, string?> display)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            itemId = plan.Before!.Id, vendorId = plan.Item!.VendorId, typeId = reference.Find(plan.Item.TypeCode)?.Id,
            changes = plan.Changes.OrderBy(c => c.Field, StringComparer.Ordinal).Select(c => new
            {
                c.Field, c.Before, c.After, shown = display.GetValueOrDefault(ImportColumnKey(c.Field)),
            }),
            stored = plan.StoredValues.OrderBy(value => value.Key),
        }, JsonDefaults.Web);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static AuditChange[] ImportChanges(OutsourceReference reference, OutsourceItem before, OutsourceItem next,
        IEnumerable<OutsourceItemValue> existing, Dictionary<ulong, string> proposed, OutsourceEditor editor)
    {
        var layout = reference.Layout;
        var changes = new List<AuditChange>();
        void Add(string key, object? oldValue, object? newValue)
        {
            if (!Equals(oldValue, newValue))
                changes.Add(new(key, layout.Label(ColumnKey(key)), oldValue, newValue));
        }
        Add("outsourceDate", before.OutsourceDate, next.OutsourceDate);
        Add("vendor", reference.VendorNames.GetValueOrDefault(before.VendorId), reference.VendorNames.GetValueOrDefault(next.VendorId));
        Add("typeCode", reference.Label(before.TypeCode), reference.Label(next.TypeCode));
        foreach (var change in changes)
        {
            if (layout.Find(ColumnKey(change.Field)) is not { } column || !OutsourceFieldLayout.CanEdit(column, editor))
                throw ApiException.BadRequest($"无权修改栏位「{change.Label}」");
        }
        Add("status", OutsourceItemStatuses.Label(before.Status), OutsourceItemStatuses.Label(next.Status));
        Add("remark", before.Remark, next.Remark);
        Add("customer", before.Customer, next.Customer);
        Add("machineModel", before.MachineModel, next.MachineModel);
        Add("mechanicalOwner", before.MechanicalOwner, next.MechanicalOwner);
        Add("electricalOwner", before.ElectricalOwner, next.ElectricalOwner);
        Add("quantity", before.Quantity, next.Quantity);
        Add("workOrderIssuedOn", before.WorkOrderIssuedOn, next.WorkOrderIssuedOn);
        Add("originalDueDate", before.OriginalDueDate, next.OriginalDueDate);
        Add("latestDueDate", before.LatestDueDate, next.LatestDueDate);
        Add("bomOn", before.BomOn, next.BomOn);
        Add("drawingReleasedOn", before.DrawingReleasedOn, next.DrawingReleasedOn);

        var oldValues = existing.ToDictionary(value => value.FieldId, value => value.Value);
        foreach (var column in layout.Columns.Where(column => proposed.ContainsKey(column.Field.Id)))
        {
            var id = column.Field.Id;
            var old = oldValues.GetValueOrDefault(id);
            var value = proposed[id];
            if (old == value) { proposed.Remove(id); continue; }
            if (column.BuiltIn is null)
                Add(column.Key, OutsourceFieldValues.Display(column.Field.DataType, old), OutsourceFieldValues.Display(column.Field.DataType, value));
            else if (!changes.Any(change => change.Field == column.Key))
                changes.Add(new(FallbackPrefix + column.Key, column.Label + "（Excel 补充值）", old, value));
        }
        return changes.ToArray();
    }

    private async Task WriteImportUpdateAsync(YfDbContext db, CurrentUser actor, PlannedRow row, DateTime now,
        IReadOnlySet<string> noticeKeys, CancellationToken ct)
    {
        var before = row.Before!;
        var next = row.Item!;
        var sendStatus = SendStatusAfterEdit(before, row.Changes, noticeKeys);
        // MySQL stores milliseconds. Even a custom-field-only write in the same millisecond
        // invalidates the preview token. The row is already locked by ExistingImportItemsAsync.
        var version = now > before.UpdatedAt ? now : before.UpdatedAt.AddMilliseconds(1);
        await db.OutsourceItems.Where(x => x.Id == before.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.OutsourceDate, next.OutsourceDate).SetProperty(x => x.VendorId, next.VendorId)
            .SetProperty(x => x.TypeCode, next.TypeCode).SetProperty(x => x.Status, next.Status).SetProperty(x => x.Remark, next.Remark)
            .SetProperty(x => x.Customer, next.Customer).SetProperty(x => x.MachineModel, next.MachineModel)
            .SetProperty(x => x.MechanicalOwner, next.MechanicalOwner).SetProperty(x => x.ElectricalOwner, next.ElectricalOwner)
            .SetProperty(x => x.Quantity, next.Quantity).SetProperty(x => x.WorkOrderIssuedOn, next.WorkOrderIssuedOn)
            .SetProperty(x => x.OriginalDueDate, next.OriginalDueDate).SetProperty(x => x.LatestDueDate, next.LatestDueDate)
            .SetProperty(x => x.BomOn, next.BomOn).SetProperty(x => x.DrawingReleasedOn, next.DrawingReleasedOn)
            .SetProperty(x => x.SendStatus, sendStatus).SetProperty(x => x.ModifiedBy, actor.Id)
            .SetProperty(x => x.ModifiedAt, now).SetProperty(x => x.UpdatedAt, version), ct);
        OutsourceHistory.Add(db, before.Id, row.Changes, OutsourceHistory.Edit, actor.Id);
        await OutsourceItemValues.WriteAsync(db, before.Id, row.StoredValues.Select(pair => (pair.Key, (string?)pair.Value)), ct);
        await audit.WriteAsync(db.Database.Connection(), db.Database.RequireTransaction(), actor.Id,
            "OUTSOURCE_ITEM_UPDATE", "outsource_item", before.Id, new { targetName = before.WorkOrderNo, changes = row.Changes, source = "IMPORT" }, null, ct);
    }
}
