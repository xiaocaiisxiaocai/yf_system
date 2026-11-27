using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

public sealed partial class MesMirrorSyncService
{
    private const int MaxImportRows = 5000;
    private const string ImportBusyMessage = "IPMS 资料抓取进行中或有待确认的结果，请稍后再导入";

    // Keep in sync with the MES_FIXED_COLUMNS and MES_LAYOUT fields in web/src/outsource/mesColumns.ts.
    private static readonly string[] ImportStagePrefixes =
    [
        "zzzz", "zzzk", "zzzzts", "zzzkts", "zzjczz", "zzjczk", "znhzb", "kshlt", "znhlt",
        "ortzz", "ortzk", "dbyz", "cpjh", "yj", "qjdbzk", "qjdbzz",
    ];

    internal static IReadOnlySet<string> ImportFields { get; } = new HashSet<string>(
    [
        "customerName", "workOrder", "deviceModels", "planEndDate", "customerEndDate", "productionBatch",
        "functionChangeDay", "innerExceptionDay", "materialDay", "applyEndTime",
        "bomActualDate", "bomPlanDate", "bomReportProgress", "bomChargeUser",
        "twodPlanDate", "twodActualDate", "twodReportProgress", "twodChargeUser",
        "jjtPlanDate", "jjtActualDate", "jjtReportProgress", "jjtChargeUser",
        "yfdkPlanDate", "yfdkActualDate", "yfdkChargeUser", "yfdkWorkorderbgbl",
        "cgPlanDate", "cgActualDate", "cgChargeUser", "poNum", "poDate", "pickDate", "cgWorkorderbgbl",
        "deviceNum", "deliveryDate", "deliveryCountDown", "workHource",
        "productionStatus", "processName", "historyDay", "startDay",
        "outMachineDate", "delayType", "outMachineStatus", "manufacturingProcess",
        .. ImportStagePrefixes.SelectMany(prefix => new[]
        {
            prefix + "PlanDate", prefix == "zzzk" ? "zzzkACtualDate" : prefix + "ActualDate",
            prefix + "Workorderbgbl", prefix + "ChargeUser",
        }),
    ], StringComparer.Ordinal);

    public async Task<MesImportPreviewResponse> PreviewImportAsync(CurrentUser actor, MesImportRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(actor, ct);
        var (valid, invalid) = ValidateImportRows(request.Rows);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var groups = await ReadImportGroupsAsync(context, valid, ct);
        var added = 0;
        var unchanged = 0;
        var existing = new List<MesImportExisting>();
        var duplicates = new List<MesImportDuplicate>();
        foreach (var group in groups)
        {
            var row = group.Rows[0];
            var hasStoredOrder = group.Stored.WorkOrderNo is not null;
            var current = ReadRawFields(group.Stored);
            if (group.Rows.Length > 1)
            {
                duplicates.Add(new(row.WorkOrder, hasStoredOrder, group.Rows.Select(x => new MesImportDuplicateRow(x.RowNumber,
                    x.Values.GetValueOrDefault("customerName"), x.Values.GetValueOrDefault("deviceModels"),
                    x.Values.GetValueOrDefault("planEndDate"), PreviewImportExisting(x, group.Stored, current))).ToArray()));
                continue;
            }
            if (!hasStoredOrder) added++;
            else if (PreviewImportExisting(row, group.Stored, current) is { } conflict) existing.Add(conflict);
            else unchanged++;
        }
        return new(added, unchanged, invalid, existing.ToArray(), duplicates.ToArray());
    }

    private static MesImportExisting? PreviewImportExisting(MesImportRow row, ImportMatch stored, Dictionary<string, JsonElement> current)
    {
        if (stored.WorkOrderNo is not { } workOrder) return null;
        var changed = CountChangedFields(row, workOrder, current);
        if (changed == 0) return null;
        var before = ReadImportSummary(stored, current);
        return new(workOrder, before, new(
            row.Values.GetValueOrDefault("customerName", before.CustomerName),
            row.Values.GetValueOrDefault("deviceModels", before.DeviceModels),
            row.Values.GetValueOrDefault("planEndDate", before.PlanEndDate)), changed);
    }

    public async Task<MesImportConfirmResponse> ConfirmImportAsync(CurrentUser actor, MesImportConfirmRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(actor, ct);
        var (valid, invalid) = ValidateImportRows(request.Rows);
        if (request.Overwrite is null || request.ChosenRows is null) throw ApiException.BadRequest("缺少导入选择");

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.Settings);
        await LockImportStateAsync(context, ct);
        var groups = await ReadImportGroupsAsync(context, valid, ct);
        var chosen = groups.Select(group => (Group: group, Row: ChooseImportRow(group, request.ChosenRows))).ToArray();
        var overwrite = request.Overwrite.ToHashSet(StringComparer.Ordinal);
        // A 36-character column leaves 30 hex digits after the prefix.
        var batchId = "excel-" + Guid.NewGuid().ToString("N")[..30];
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var added = 0;
        var overwritten = 0;
        var skipped = invalid.Length + valid.Length - groups.Length;
        var unchanged = 0;
        foreach (var (group, row) in chosen)
        {
            var stored = group.Stored;
            var workOrder = stored.WorkOrderNo ?? row.WorkOrder;
            var raw = ReadRawFields(stored);
            if (stored.WorkOrderNo is not null)
            {
                if (CountChangedFields(row, workOrder, raw) == 0)
                {
                    unchanged++;
                    continue;
                }
                if (!overwrite.Contains(workOrder))
                {
                    skipped++;
                    continue;
                }
            }

            foreach (var (field, value) in row.Values)
                raw[field] = JsonSerializer.SerializeToElement(field == "workOrder" ? workOrder : value);
            if (stored.WorkOrderNo is null)
                raw["workOrder"] = JsonSerializer.SerializeToElement(workOrder);
            var entity = new MesWorkOrder
            {
                WorkOrderNo = workOrder, BatchId = batchId, FetchedAt = now, RawJson = JsonSerializer.Serialize(raw),
                CustomerName = row.Values.TryGetValue("customerName", out var customer) ? Cut(customer) : stored.CustomerName,
                DeviceModels = row.Values.TryGetValue("deviceModels", out var model) ? Cut(model) : stored.DeviceModels,
            };
            if (stored.WorkOrderNo is null)
            {
                context.MesWorkOrders.Add(entity);
                added++;
            }
            else
            {
                context.MesWorkOrders.Update(entity);
                overwritten++;
            }
        }
        await context.SaveChangesAsync(ct);
        await WriteAuditAsync(context, actor.Id, new { targetName = "IPMS 资料 Excel 导入", trigger = "EXCEL", added, overwritten, skipped }, ct);
        await transaction.CommitAsync(ct);
        return new(added, overwritten, skipped, unchanged, invalid);
    }

    private static async Task LockImportStateAsync(YfDbContext context, CancellationToken ct)
    {
        const string sql = "SELECT lease_until AS LeaseUntil FROM mes_mirror_state WHERE id=1 FOR UPDATE";
        var state = (await context.Database.SqlQueryRaw<ImportLease>(sql).ToArrayAsync(ct)).SingleOrDefault();
        if (state is null)
        {
            // The same singleton and primary key used to claim the fetch lease, including on a fresh installation.
            await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
            state = (await context.Database.SqlQueryRaw<ImportLease>(sql).ToArrayAsync(ct)).Single();
        }
        if (state.LeaseUntil is { } leaseUntil && leaseUntil > await DbClock.UtcNowAsync(context, ct, 3))
            throw ApiException.Conflict(ImportBusyMessage);
    }

    private sealed record ImportLease
    {
        public DateTime? LeaseUntil { get; init; }
    }

    private sealed record ImportMatch
    {
        public string InputWorkOrder { get; init; } = null!;
        public string CollationKey { get; init; } = null!;
        public string? WorkOrderNo { get; init; }
        public string? CustomerName { get; init; }
        public string? DeviceModels { get; init; }
        public string? RawJson { get; init; }
    }

    private sealed record ImportGroup(ImportMatch Stored, MesImportRow[] Rows);

    private static async Task<ImportGroup[]> ReadImportGroupsAsync(YfDbContext context, MesImportRow[] rows, CancellationToken ct)
    {
        var matches = new Dictionary<string, ImportMatch>(StringComparer.Ordinal);
        foreach (var batch in rows.Select(x => x.WorkOrder).Distinct(StringComparer.Ordinal).Chunk(100))
        {
            // Use the primary key's collation for both stored matches and in-file duplicates.
            // SQL only interpolates generated parameter names. The file contents are always bound values.
            var parameters = batch.Select((workOrder, index) => new MySqlParameter($"@n{index}", workOrder)).ToArray();
            var inputs = parameters.Select(parameter =>
                $"SELECT CAST({parameter.ParameterName} AS CHAR CHARACTER SET utf8mb4) COLLATE utf8mb4_unicode_ci AS OrderNo");
            var sql = $"""
                SELECT i.OrderNo AS InputWorkOrder, HEX(WEIGHT_STRING(i.OrderNo)) AS CollationKey,
                       m.work_order_no AS WorkOrderNo, m.customer_name AS CustomerName,
                       m.device_models AS DeviceModels, m.raw_json AS RawJson
                FROM ({string.Join(" UNION ALL ", inputs)}) AS i
                LEFT JOIN mes_work_orders AS m ON m.work_order_no = i.OrderNo
                """;
            foreach (var match in await context.Database.SqlQueryRaw<ImportMatch>(sql, parameters).ToArrayAsync(ct))
                matches.Add(match.InputWorkOrder, match);
        }
        return rows.GroupBy(row => matches[row.WorkOrder].CollationKey, StringComparer.Ordinal)
            .Select(group => new ImportGroup(matches[group.First().WorkOrder], group.ToArray())).ToArray();
    }

    private static MesImportRow ChooseImportRow(ImportGroup group, Dictionary<string, int> chosen)
    {
        var first = group.Rows[0];
        if (group.Rows.Length == 1) return first;
        if (chosen.TryGetValue(first.WorkOrder, out var rowNumber)
            && group.Rows.FirstOrDefault(row => row.RowNumber == rowNumber) is { } row) return row;
        throw ApiException.BadRequest($"请选择工单号“{first.WorkOrder}”要导入的 Excel 行");
    }

    private static (MesImportRow[] Valid, MesImportInvalidRow[] Invalid) ValidateImportRows(MesImportRow[]? rows)
    {
        if (rows is null || rows.Length is < 1 or > MaxImportRows)
            throw ApiException.BadRequest($"导入行数需为 1 到 {MaxImportRows}");
        var valid = new List<MesImportRow>();
        var invalid = new List<MesImportInvalidRow>();
        var repeatedNumbers = rows.OfType<MesImportRow>().GroupBy(row => row.RowNumber)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var row in rows)
        {
            var workOrder = row?.WorkOrder?.Trim() ?? "";
            // Omit empty cells once so preview and confirmation handle them the same way.
            var values = row?.Values?.Where(field => !string.IsNullOrWhiteSpace(field.Value))
                .ToDictionary(StringComparer.Ordinal);
            var reason = row switch
            {
                null => "缺少行内容",
                { RowNumber: < 1 } => "行号无效",
                _ when repeatedNumbers.Contains(row.RowNumber) => "行号重复",
                _ when workOrder.Length is < 1 or > 64 => "工单号需为 1 到 64 字符",
                _ when values is null => "缺少 MES 字段",
                _ => ValidateImportValues(values, workOrder),
            };
            if (reason is not null)
                invalid.Add(new(row?.RowNumber ?? 0, workOrder, reason));
            else
                valid.Add(row! with { WorkOrder = workOrder, Values = values! });
        }
        return (valid.ToArray(), invalid.ToArray());
    }

    private static string? ValidateImportValues(Dictionary<string, string?> values, string workOrder)
    {
        foreach (var (field, value) in values)
        {
            if (!ImportFields.Contains(field)) return $"不支持的 MES 字段：{field}";
            if (value?.Length > 1000) return $"{field} 超过 1000 字符";
            if (field == "workOrder" && value?.Trim() != workOrder) return "工单号与 MES 字段值不一致";
        }
        return null;
    }

    private static Dictionary<string, JsonElement> ReadRawFields(ImportMatch stored) =>
        stored.RawJson is { } json ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [] : [];

    private static int CountChangedFields(MesImportRow row, string workOrder, Dictionary<string, JsonElement> current) =>
        row.Values.Count(field => !SameImportValue(
            current.TryGetValue(field.Key, out var before) ? FieldText(before) : null,
            field.Key == "workOrder" ? workOrder : field.Value));

    /// <summary>Numbers compare by value (MES sends 100.0, Excel shows 100).</summary>
    private static bool SameImportValue(string? stored, string? excel)
    {
        if (stored?.Trim() == excel?.Trim()) return true;
        const System.Globalization.NumberStyles number = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return decimal.TryParse(stored, number, culture, out var a) && decimal.TryParse(excel, number, culture, out var b) && a == b;
    }

    private static MesImportOrderSummary ReadImportSummary(ImportMatch stored, Dictionary<string, JsonElement> values) => new(
        values.TryGetValue("customerName", out var customer) ? FieldText(customer) : stored.CustomerName,
        values.TryGetValue("deviceModels", out var models) ? FieldText(models) : stored.DeviceModels,
        FieldText(values.GetValueOrDefault("planEndDate")));
}
