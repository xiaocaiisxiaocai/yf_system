using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>
/// Serves IPMS work-order data from the daily copy of the IPMS MES monitoring list (<c>mes_work_orders</c>). Never touches
/// the network, so outsourcing imports and syncs are not slowed by IPMS. Work orders absent from the copy are simply not
/// found; before the first fetch nothing can be looked up, so lookups fail instead of reporting every work order missing.
/// </summary>
public sealed class MesMirrorIpmsClient(IDbContextFactory<YfDbContext> dbFactory, IMesSettings settings) : IIpmsClient
{
    /// <summary>Configured once the address, account and password are set (on the settings page or in configuration).</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct) => (await settings.ConnectionAsync(ct)).Complete;

    public async Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
    {
        var wanted = workOrderNos.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, IpmsWorkOrder>(StringComparer.Ordinal);
        if (wanted.Length == 0) return result;
        try
        {
            await using var context = await dbFactory.CreateDbContextAsync(ct);
            var fetchedBefore = await context.MesMirrorState.AsNoTracking().AnyAsync(x => x.Id == 1 && x.LastSuccessAt != null, ct)
                || await context.MesWorkOrders.AnyAsync(ct);
            if (!fetchedBefore) throw new IpmsUnavailableException("尚未从 IPMS 抓取资料，请先在设定页抓取");
            var rows = await context.MesWorkOrders.AsNoTracking().Where(x => wanted.Contains(x.WorkOrderNo))
                .Select(x => new { x.WorkOrderNo, x.RawJson }).ToArrayAsync(ct);
            foreach (var row in rows) result[row.WorkOrderNo] = Map(row.WorkOrderNo, row.RawJson);
        }
        catch (Exception error) when (error is DbUpdateException or InvalidOperationException or MySqlConnector.MySqlException)
        {
            throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试", error);
        }
        return result;
    }

    /// <summary>
    /// MES field → outsourcing field (names are the 外协清单 columns, 契约 → 数据与字段归属):
    /// 客户 ← customerName, 机型 ← deviceModels, 机构执行人 ← bomChargeUser (the 2D and rack-drawing owners are always the same person),
    /// 工令下发 ← applyEndTime (签收日期), 数量 ← deviceNum,
    /// 原始交期 ← customerEndDate (客户交期, 2026-11-24; not deliveryDate 原交期). MES has no
    /// 电控执行人. The outsourcing type, BOM时间 and 发图 are entered by staff (2026-11-17). MES only lists open work orders, so none is closed here.
    /// </summary>
    internal static IpmsWorkOrder Map(string workOrderNo, string rawJson)
    {
        using var json = JsonDocument.Parse(rawJson);
        var root = json.RootElement;
        return new(workOrderNo, Text(root, "customerName"), Text(root, "deviceModels"), Text(root, "bomChargeUser"), null,
            Date(root, "applyEndTime"),
            Quantity(root, "deviceNum"), Date(root, "customerEndDate"), false);
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static int? Quantity(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private static DateOnly? Date(JsonElement root, string name)
    {
        var text = Text(root, name);
        return text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }
}
