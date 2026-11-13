using System.Text.Json;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms;

/// <summary>
/// The IPMS fields the outsourcing list copies for one work order. Every field is read-only in this system;
/// IPMS stays the source of truth. The outsourcing type is not one of them: staff enter it (外协契约 → 外发类型字典与防呆). See 外协清单契约 → 数据与字段归属.
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
    int? Quantity,
    DateOnly? DueDate,
    bool Closed);

/// <summary>IPMS could not answer (not configured, unreachable, timed out or malformed). Callers reject the whole batch.</summary>
public sealed class IpmsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only work-order lookup: <see cref="Mes.MesMirrorIpmsClient"/> serves the copy of the IPMS MES monitoring list;
/// <see cref="FakeIpmsClient"/> serves development data.
/// </summary>
public interface IIpmsClient
{
    /// <summary>Whether a data source is configured. Unconfigured lookups always throw.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct);

    /// <summary>
    /// Looks up a batch of work orders. Work orders IPMS does not know are absent from the result.
    /// Throws <see cref="IpmsUnavailableException"/> when IPMS cannot answer at all.
    /// </summary>
    Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct);
}

public sealed class IpmsOptions
{
    /// <summary>
    /// Empty (or the former <c>MesMirror</c>): IPMS data comes from the IPMS MES module, set up on the outsourcing settings page.
    /// <c>Fake</c>: development data from <see cref="FakeDataPath"/>.
    /// </summary>
    public string Mode { get; set; } = "";
    /// <summary>Absolute path to the fake JSON file; read on every lookup so testers can edit it live.</summary>
    public string FakeDataPath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Optional default IPMS service root including the port (e.g. <c>http://host:6001</c>); the settings page overrides it.</summary>
    public string MesBaseUrl { get; set; } = "";
    /// <summary>Optional default IPMS account; the settings page overrides it. Never logged.</summary>
    public string MesAccount { get; set; } = "";
    /// <summary>Optional default password of <see cref="MesAccount"/>; the settings page overrides it (stored encrypted). Never logged.</summary>
    public string MesPassword { get; set; } = "";
    /// <summary>Default daily fetch time in business time (UTC+8), "HH:mm"; the settings page overrides it.</summary>
    public string MesSyncTime { get; set; } = "03:00";
    public int MesPageSize { get; set; } = 100;
    public int MesPageDelaySeconds { get; set; } = 2;
    public int MesPageTimeoutSeconds { get; set; } = 180;
    /// <summary>A run that fetches fewer rows than this percentage of the previous success is not applied.</summary>
    public int MesMinRatioPercent { get; set; } = 50;

    internal const string FakeMode = "Fake";
    internal const string MesMirrorMode = "MesMirror";

    public void Validate()
    {
        if (Mode is not ("" or FakeMode or MesMirrorMode)) throw new InvalidOperationException("App:Ipms:Mode must be empty, Fake or MesMirror.");
        if (TimeoutSeconds is < 1 or > 600) throw new InvalidOperationException("App:Ipms:TimeoutSeconds must be between 1 and 600.");
        if (Mode == FakeMode && (FakeDataPath.Length == 0 || !Path.IsPathFullyQualified(FakeDataPath)))
            throw new InvalidOperationException("App:Ipms:FakeDataPath must be an absolute path when App:Ipms:Mode is Fake.");
        if (Mode == FakeMode) return;
        // Messages name the setting only; values (the password above all) never appear in them.
        if (!string.IsNullOrWhiteSpace(MesBaseUrl) && (!Uri.TryCreate(MesBaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("App:Ipms:MesBaseUrl must be an absolute http or https URL when set (it can also be set on the settings page).");
        if (!TimeOnly.TryParseExact(MesSyncTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidOperationException("App:Ipms:MesSyncTime must be HH:mm.");
        if (MesPageSize is < 1 or > 200) throw new InvalidOperationException("App:Ipms:MesPageSize must be between 1 and 200.");
        if (MesPageDelaySeconds is < 0 or > 60) throw new InvalidOperationException("App:Ipms:MesPageDelaySeconds must be between 0 and 60.");
        if (MesPageTimeoutSeconds is < 10 or > 600) throw new InvalidOperationException("App:Ipms:MesPageTimeoutSeconds must be between 10 and 600.");
        if (MesMinRatioPercent is < 1 or > 100) throw new InvalidOperationException("App:Ipms:MesMinRatioPercent must be between 1 and 100.");
    }
}

/// <summary>
/// Development/test data source. File shape:
/// <c>{ "unavailable": false, "workOrders": [ { "workOrderNo": "21089901", "customer": "...", ... } ] }</c>.
/// <c>unavailable: true</c> simulates an IPMS outage.
/// </summary>
public sealed class FakeIpmsClient(string path) : IIpmsClient
{
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);

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
                row.WorkOrderIssuedOn, row.BomOn, row.DrawingReleasedOn, row.Quantity, row.DueDate, row.Closed);
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
        public int? Quantity { get; set; }
        public DateOnly? DueDate { get; set; }
        public bool Closed { get; set; }
    }
}

internal static class IpmsClientFactory
{
    internal static IIpmsClient Create(AppOptions options, IServiceProvider services) => options.Ipms.Mode == IpmsOptions.FakeMode
        ? new FakeIpmsClient(options.Ipms.FakeDataPath)
        : new Mes.MesMirrorIpmsClient(
            services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<YfDbContext>>(),
            services.GetRequiredService<Mes.IMesSettings>());
}
