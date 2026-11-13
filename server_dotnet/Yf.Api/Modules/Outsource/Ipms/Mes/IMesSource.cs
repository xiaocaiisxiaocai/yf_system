namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>One MES monitoring row: the work-order number plus the row exactly as MES returned it.</summary>
public sealed record MesRow(string WorkOrderNo, string RawJson);

/// <summary>One page of the MES monitoring list; <see cref="Total"/> is MES's count over all pages.</summary>
public sealed record MesPage(int Total, IReadOnlyList<MesRow> Rows);

/// <summary>Outcome of a connection test: nothing was written, and <see cref="Message"/> never carries credentials.</summary>
public sealed record MesTestResult(bool Ok, string Message, int? Total, int ElapsedMs);

/// <summary>
/// How to reach the IPMS MES module right now: values saved on the settings page win over <c>App:Ipms</c>.
/// <see cref="Password"/> is a secret: never logged, stored only encrypted, never returned by the API.
/// </summary>
public sealed record MesConnection(string? BaseUrl, string Account, string Password)
{
    public bool Complete => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Account) && !string.IsNullOrEmpty(Password);

    /// <summary>Never prints the account or password (the generated record text would).</summary>
    public override string ToString() => $"MesConnection {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>The current IPMS connection (address, account, password).</summary>
public interface IMesSettings
{
    Task<MesConnection> ConnectionAsync(CancellationToken ct);
}

/// <summary>Reads the MES monitoring list page by page. Failures surface as <see cref="IpmsUnavailableException"/>.</summary>
public interface IMesSource
{
    /// <summary>
    /// Logs in afresh with <paramref name="connection"/> (never reusing or replacing the shared session) and fetches one
    /// row from page 1. Never throws for a MES failure.
    /// </summary>
    Task<MesTestResult> TestAsync(MesConnection connection, CancellationToken ct);

    /// <summary><paramref name="page"/> starts at 1. Throws <see cref="IpmsUnavailableException"/> when MES cannot answer.</summary>
    Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct);
}
