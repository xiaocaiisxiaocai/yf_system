namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>One MES monitoring row: the work-order number plus the row exactly as MES returned it.</summary>
public sealed record MesRow(string WorkOrderNo, string RawJson);

/// <summary>One page of the MES monitoring list; <see cref="Total"/> is MES's count over all pages.</summary>
public sealed record MesPage(int Total, IReadOnlyList<MesRow> Rows);

/// <summary>Outcome of a connection test: nothing was written, and <see cref="Message"/> never carries credentials.</summary>
public sealed record MesTestResult(bool Ok, string Message, int? Total, int ElapsedMs);

/// <summary>Where the MES service lives right now: the address saved on the page, else the configured one (null when neither is set).</summary>
public interface IMesSettings
{
    Task<string?> BaseUrlAsync(CancellationToken ct);
}

/// <summary>Reads the MES monitoring list page by page. Failures surface as <see cref="IpmsUnavailableException"/>.</summary>
public interface IMesSource
{
    /// <summary>
    /// Logs in afresh (never reusing or replacing the shared session) and fetches one row from page 1 at
    /// <paramref name="baseUrl"/>, or at the current address when null. Never throws for a MES failure.
    /// </summary>
    Task<MesTestResult> TestAsync(string? baseUrl, CancellationToken ct);

    /// <summary><paramref name="page"/> starts at 1. Throws <see cref="IpmsUnavailableException"/> when MES cannot answer.</summary>
    Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct);
}
