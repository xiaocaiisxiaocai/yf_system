using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceColumnFilters
{
    internal static IReadOnlyDictionary<string, string>? Parse(string json)
    {
        if (json.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("columnFilters");
            var filters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw Invalid(property.Name);
                var value = property.Value.GetString()!.Trim();
                if (property.Name.Length == 0 || value.Length == 0
                    || value.EnumerateRunes().Count() > OutsourceFieldValues.MaximumTextLength
                    || !filters.TryAdd(property.Name, value)) throw Invalid(property.Name);
            }
            return filters;
        }
        catch (JsonException) { throw Invalid("columnFilters"); }
    }

    internal static async Task<IQueryable<OutsourceItem>> ApplyAsync(YfDbContext db, IQueryable<OutsourceItem> items,
        OutsourceItemQuery query, OutsourceReference reference, CancellationToken ct)
    {
        if (query.ColumnFilters is not { Count: > 0 } filters) return items;
        foreach (var (key, value) in filters)
        {
            var pattern = QueryValues.ContainsPattern(value);
            switch (key)
            {
                case "ipmsSyncStatus":
                    if (value != "unsynced") throw Invalid(key);
                    items = items.Where(x => x.IpmsSyncedAt == null);
                    break;
                case "serialNo":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var serial)) throw Invalid(key);
                    items = items.Where(x => x.SerialNo == serial);
                    break;
                case "quantity":
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var quantity)) throw Invalid(key);
                    items = items.Where(x => x.Quantity == quantity);
                    break;
                case "electricalHandoff":
                    var handoff = Boolean(value, key);
                    items = items.Where(x => x.ElectricalHandoff == handoff);
                    break;
                case "files":
                    items = OutsourceFiles.FilterItems(db, items, value, await reference.ArchiveTimeAsync(db, ct));
                    break;
                case "customer":
                    // Customer aliases use the same normalization as the displayed cells.
                    break;
                case "machineModel":
                    items = items.Where(x => EF.Functions.Like(x.MachineModel!, pattern, QueryValues.LikeEscape));
                    break;
                case "mechanicalOwner":
                    items = items.Where(x => EF.Functions.Like(x.MechanicalOwner!, pattern, QueryValues.LikeEscape));
                    break;
                case "electricalOwner":
                    items = items.Where(x => EF.Functions.Like(x.ElectricalOwner!, pattern, QueryValues.LikeEscape));
                    break;
                case "remark":
                    items = items.Where(x => EF.Functions.Like(x.Remark!, pattern, QueryValues.LikeEscape));
                    break;
                case "importedBy":
                case "modifiedBy":
                    var users = db.Users.Where(u => EF.Functions.Like(u.RealName, pattern, QueryValues.LikeEscape))
                        .Select(u => (ulong?)u.Id);
                    items = key == "importedBy" ? items.Where(x => users.Contains(x.CreatedBy)) : items.Where(x => users.Contains(x.ModifiedBy));
                    break;
                case "importedAt":
                case "modifiedAt":
                    var (start, end) = DayInUtc(value, key, query.ColumnFilterTimeZone);
                    items = key == "importedAt"
                        ? items.Where(x => x.CreatedBy != null && x.CreatedAt >= start && x.CreatedAt < end)
                        : items.Where(x => x.ModifiedAt >= start && x.ModifiedAt < end);
                    break;
                case "workOrderIssuedOn":
                case "bomOn":
                case "drawingReleasedOn":
                case "originalDueDate":
                case "latestDueDate":
                    var day = Date(value, key);
                    items = key switch
                    {
                        "workOrderIssuedOn" => items.Where(x => x.WorkOrderIssuedOn == day),
                        "bomOn" => items.Where(x => x.BomOn == day),
                        "drawingReleasedOn" => items.Where(x => x.DrawingReleasedOn == day),
                        "originalDueDate" => items.Where(x => x.OriginalDueDate == day),
                        _ => items.Where(x => x.LatestDueDate == day),
                    };
                    break;
                case "supervisor":
                    break; // Resolve the displayed supervisor after other columns have narrowed the owner directory.
                default:
                    var field = reference.Layout.Find(key);
                    if (field is not { Active: true, BuiltIn: null }) throw Invalid(key);
                    items = FilterCustom(db, items, field, value, pattern);
                    break;
            }
        }
        if (filters.TryGetValue("customer", out var customer))
        {
            var pattern = QueryValues.ContainsPattern(customer);
            var aliases = reference.CustomersWithShortNameContaining(customer);
            if (aliases.Length == 0) items = items.Where(x => EF.Functions.Like(x.Customer!, pattern, QueryValues.LikeEscape));
            else
            {
                var names = await items.Where(x => x.Customer != null)
                    .Select(x => EF.Functions.Collate(x.Customer!, "utf8mb4_bin")).Distinct().ToArrayAsync(ct);
                var keys = aliases.Select(OutsourceSettingsService.CustomerKey).ToHashSet(StringComparer.Ordinal);
                var matched = names.Where(name => keys.Contains(OutsourceSettingsService.CustomerKey(name))).ToArray();
                items = items.Where(x => EF.Functions.Like(x.Customer!, pattern, QueryValues.LikeEscape) || matched.Contains(x.Customer!));
            }
        }
        if (!filters.TryGetValue("supervisor", out var supervisor)) return items;
        // Only the distinct spellings enter OpenCC; counting, ordering and paging still use the filtered SQL query.
        var owners = await items.Where(x => x.MechanicalOwner != null)
            .Select(x => EF.Functions.Collate(x.MechanicalOwner!, "utf8mb4_bin")).Distinct().ToArrayAsync(ct);
        var directory = await MechanicalSupervisorDirectory.ResolveAsync(db, owners, ct);
        var matches = directory.Where(x => x.Value.Contains(supervisor, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray();
        return items.Where(x => matches.Contains(EF.Functions.Collate(x.MechanicalOwner!, "utf8mb4_bin")));
    }

    private static IQueryable<OutsourceItem> FilterCustom(YfDbContext db, IQueryable<OutsourceItem> items,
        OutsourceFieldLayout.Column field, string value, string pattern)
    {
        var fieldId = field.Field.Id;
        var values = db.OutsourceItemValues.Where(v => v.FieldId == fieldId);
        switch (field.Field.DataType)
        {
            case OutsourceFieldTypes.Boolean:
                Boolean(value, field.Key);
                values = values.Where(v => v.Value == value);
                break;
            case OutsourceFieldTypes.Date:
                Date(value, field.Key);
                values = values.Where(v => v.Value == value);
                break;
            case OutsourceFieldTypes.Select:
                if (!field.Options.Contains(value, StringComparer.Ordinal)) throw Invalid(field.Key);
                values = values.Where(v => EF.Functions.Collate(v.Value, "utf8mb4_bin") == value);
                break;
            case OutsourceFieldTypes.Number:
                var normalized = OutsourceFieldValues.Normalize(field.Field.DataType, field.Options, field.Label, value)!;
                var number = decimal.Parse(normalized, CultureInfo.InvariantCulture);
                // The editor preserves decimal scale (e.g. 1.0 and 1.00). Compare in MySQL's full precision.
                var matchingIds = db.Database.SqlQuery<ulong>($"""
                    SELECT item_id AS Value FROM outsource_item_values
                    WHERE field_id = {fieldId} AND CAST(`value` AS DECIMAL(65,30)) = {number}
                    """);
                return items.Where(x => matchingIds.Contains(x.Id));
            case OutsourceFieldTypes.Text:
                values = values.Where(v => EF.Functions.Like(v.Value, pattern, QueryValues.LikeEscape));
                break;
            default:
                throw Invalid(field.Key);
        }
        return items.Where(x => values.Any(v => v.ItemId == x.Id));
    }

    private static DateOnly Date(string text, string key) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value : throw Invalid(key);

    private static bool Boolean(string text, string key) => text switch
    {
        "true" => true, "false" => false, _ => throw Invalid(key),
    };

    private static (DateTime Start, DateTime End) DayInUtc(string text, string key, string? timeZoneId)
    {
        var day = Date(text, key);
        if (day == DateOnly.MaxValue) throw Invalid(key);
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrEmpty(timeZoneId) && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out zone))
            throw Invalid("columnFilterTimeZone");
        return (StartOfDay(day, zone), StartOfDay(day.AddDays(1), zone));
    }

    private static DateTime StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        // Midnight itself can be skipped or occur twice when the offset changes.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        return zone.IsAmbiguousTime(midnight)
            ? new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }

    private static ApiException Invalid(string key) => ApiException.BadRequest($"请求参数错误: {key}");
}
