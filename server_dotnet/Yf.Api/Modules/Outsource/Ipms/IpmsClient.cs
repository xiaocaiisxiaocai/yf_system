using System.Text.Json;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms;

/// <summary>
/// The IPMS fields the outsourcing list copies for one work order. Every field is read-only in this system;
/// IPMS stays the source of truth. See 外协清单契约 → 数据与字段归属.
/// </summary>
public sealed record IpmsWorkOrder(
    string WorkOrderNo,
    string? Customer,
    string? MachineModel,
    string? MechanicalSection,
    string? MechanicalOwner,
    string? ElectricalOwner,
    DateOnly? WorkOrderIssuedOn,
    DateOnly? BomOn,
    DateOnly? DrawingReleasedOn,
    string? OutsourceTypeCode,
    int? Quantity,
    DateOnly? DueDate,
    bool Closed);

/// <summary>IPMS could not answer (not configured, unreachable, timed out or malformed). Callers reject the whole batch.</summary>
public sealed class IpmsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only work-order lookup. The real IPMS adapter is not built yet (interface unknown); until then
/// <see cref="UnconfiguredIpmsClient"/> refuses and <see cref="FakeIpmsClient"/> serves development data.
/// </summary>
public interface IIpmsClient
{
    /// <summary>Whether a data source is configured. Unconfigured lookups always throw.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Looks up a batch of work orders. Work orders IPMS does not know are absent from the result.
    /// Throws <see cref="IpmsUnavailableException"/> when IPMS cannot answer at all.
    /// </summary>
    Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct);
}

public sealed class IpmsOptions
{
    /// <summary>Empty: not configured. <c>Fake</c>: development data from <see cref="FakeDataPath"/>.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Absolute path to the fake JSON file; read on every lookup so testers can edit it live.</summary>
    public string FakeDataPath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    internal const string FakeMode = "Fake";

    public void Validate()
    {
        if (Mode is not ("" or FakeMode)) throw new InvalidOperationException("App:Ipms:Mode must be empty or Fake.");
        if (TimeoutSeconds is < 1 or > 600) throw new InvalidOperationException("App:Ipms:TimeoutSeconds must be between 1 and 600.");
        if (Mode == FakeMode && (FakeDataPath.Length == 0 || !Path.IsPathFullyQualified(FakeDataPath)))
            throw new InvalidOperationException("App:Ipms:FakeDataPath must be an absolute path when App:Ipms:Mode is Fake.");
    }
}

public sealed class UnconfiguredIpmsClient : IIpmsClient
{
    public bool IsConfigured => false;

    public Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct) =>
        throw new IpmsUnavailableException("IPMS 尚未设定，请联系系统管理员");
}

/// <summary>
/// Development/test data source. File shape:
/// <c>{ "unavailable": false, "workOrders": [ { "workOrderNo": "21089901", "customer": "...", "outsourceTypeCode": "OEMA", ... } ] }</c>.
/// <c>unavailable: true</c> simulates an IPMS outage.
/// </summary>
public sealed class FakeIpmsClient(string path) : IIpmsClient
{
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => true;

    public async Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
    {
        FakeFile file;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试");
            await using var stream = File.OpenRead(path);
            file = await JsonSerializer.DeserializeAsync<FakeFile>(stream, Json, ct)
                ?? throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试", error);
        }
        if (file.Unavailable) throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试");
        var wanted = workOrderNos.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, IpmsWorkOrder>(StringComparer.Ordinal);
        foreach (var row in file.WorkOrders ?? [])
        {
            var no = row.WorkOrderNo?.Trim();
            if (string.IsNullOrEmpty(no) || !wanted.Contains(no)) continue;
            result[no] = new(no, row.Customer, row.MachineModel, row.MechanicalSection, row.MechanicalOwner, row.ElectricalOwner,
                row.WorkOrderIssuedOn, row.BomOn, row.DrawingReleasedOn, row.OutsourceTypeCode, row.Quantity, row.DueDate, row.Closed);
        }
        return result;
    }

    private sealed class FakeFile
    {
        public bool Unavailable { get; set; }
        public FakeRow[]? WorkOrders { get; set; }
    }

    private sealed class FakeRow
    {
        public string? WorkOrderNo { get; set; }
        public string? Customer { get; set; }
        public string? MachineModel { get; set; }
        public string? MechanicalSection { get; set; }
        public string? MechanicalOwner { get; set; }
        public string? ElectricalOwner { get; set; }
        public DateOnly? WorkOrderIssuedOn { get; set; }
        public DateOnly? BomOn { get; set; }
        public DateOnly? DrawingReleasedOn { get; set; }
        public string? OutsourceTypeCode { get; set; }
        public int? Quantity { get; set; }
        public DateOnly? DueDate { get; set; }
        public bool Closed { get; set; }
    }
}

internal static class IpmsClientFactory
{
    internal static IIpmsClient Create(AppOptions options) => options.Ipms.Mode == IpmsOptions.FakeMode
        ? new FakeIpmsClient(options.Ipms.FakeDataPath)
        : new UnconfiguredIpmsClient();
}
