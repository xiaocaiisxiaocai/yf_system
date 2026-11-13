namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>Manual run: <c>ALL</c> fetches every page, <c>PAGE</c> fetches only <paramref name="Page"/>. Page size defaults to the configured one.</summary>
public sealed record MesMirrorRunRequest(string? Mode, int? Page, int? PageSize);

/// <summary>Decision on a pending manual result: <c>true</c> merges it into the mirror, <c>false</c> discards it.</summary>
public sealed record MesMirrorConfirmRequest(bool Apply);

public sealed record MesMirrorPreviewRow(string WorkOrderNo, string? CustomerName, string? DeviceModels);

/// <summary>
/// Phase is IDLE, RUNNING, STOPPING or AWAITING_CONFIRM; <paramref name="PendingReason"/> (STOPPED or PAGE_FETCHED) and
/// <paramref name="Preview"/> are only set while a result waits for confirmation.
/// </summary>
public sealed record MesMirrorStatusResponse(
    string Phase, string? RunMode, int? Page, int PageSize, int Fetched, int Pages, int? Total, string? PendingReason,
    DateTime? StartedAt, DateTime? LastFinishedAt, DateTime? LastSuccessAt, string? LastError, string? LastTrigger,
    int LastCount, int MirrorCount, MesMirrorPreviewRow[] Preview);

/// <summary>An empty or blank address clears the saved one so the configured address applies again.</summary>
public sealed record MesMirrorSettingsRequest(string? BaseUrl);

/// <summary>
/// <paramref name="BaseUrl"/> is the address in effect (saved, else configured; empty when neither is set).
/// The account and password only live in the external configuration file, so only whether they are set is reported.
/// </summary>
public sealed record MesMirrorSettingsResponse(string BaseUrl, string? ConfigBaseUrl, bool Saved, bool AccountConfigured);

/// <summary>Address to test; null tests the one in effect. Nothing is saved or written.</summary>
public sealed record MesMirrorTestRequest(string? BaseUrl);
