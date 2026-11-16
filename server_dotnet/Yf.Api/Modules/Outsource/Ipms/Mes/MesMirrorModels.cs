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

/// <summary>
/// IPMS connection and daily fetch time set on the settings page. A blank address or account clears the saved value (the
/// configured one applies again; clearing the account also clears the saved password). A null or empty
/// <paramref name="Password"/> keeps the saved password. A blank <paramref name="SyncTime"/> keeps the current time.
/// </summary>
public sealed record MesMirrorSettingsRequest(string? BaseUrl, string? Account, string? Password, string? SyncTime);

/// <summary>
/// Values in effect: saved on the page, else configured (empty when neither is set). The password itself is never returned:
/// <paramref name="PasswordSet"/> says whether one is in effect, <paramref name="AccountConfigured"/> whether address,
/// account and password are all set. <paramref name="SyncTime"/> is the daily fetch time ("HH:mm", UTC+8).
/// </summary>
public sealed record MesMirrorSettingsResponse(
    string BaseUrl, string? ConfigBaseUrl, bool Saved, string Account, bool PasswordSet, bool AccountConfigured, string SyncTime,
    bool ScheduleEnabled);

/// <summary>
/// Connection to test, nothing is saved or written. Blank fields fall back to the values in effect, so a saved password
/// can be tested without typing it again.
/// </summary>
public sealed record MesMirrorTestRequest(string? BaseUrl, string? Account, string? Password);

/// <summary>One mirrored work order in the data page: the identifying columns plus a few fields read from the raw MES row.</summary>
public sealed record MesOrderRow(
    string WorkOrderNo, string? CustomerName, string? DeviceModels, string? ProcessName, string? DeliveryDate,
    string? CompleteDate, string? DeviceNum, DateTime FetchedAt);

/// <summary>One MES field exactly as returned (name and text value); <c>null</c> stays empty.</summary>
public sealed record MesOrderField(string Key, string? Value);

/// <summary>Every field MES returned for a work order, in MES order.</summary>
public sealed record MesOrderDetailResponse(string WorkOrderNo, DateTime FetchedAt, MesOrderField[] Fields);
