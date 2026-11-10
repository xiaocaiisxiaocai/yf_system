namespace Yf.Api.Modules.Outsource;

public sealed record OutsourceVendorUpsert(string? Name, string? Remark, int? SortNo);

public sealed record OutsourceContactUpsert(string? Name, string? Email, int? SortNo);

public sealed record OutsourceTypeUpsert(string? Code, string? Name, int? SortNo);

public sealed record OutsourceTypeVendorsRequest(ulong[]? VendorIds);

public sealed record OutsourceDefaultCcRequest(ulong[]? UserIds);

public sealed record OutsourceContactResponse(ulong Id, ulong VendorId, string? Name, string Email, string Status, int SortNo);

public sealed record OutsourceVendorResponse(
    ulong Id, string Name, string? Remark, string Status, int SortNo, OutsourceContactResponse[] Contacts);

public sealed record OutsourceTypeResponse(ulong Id, string Code, string Name, string Status, int SortNo, ulong[] VendorIds);

public sealed record OutsourceCcUserResponse(ulong UserId, string EmployeeNo, string RealName, string Email, string Status);

public sealed record OutsourceImportRow(int RowNumber, string? OutsourceDate, string? WorkOrderNo, string? VendorName);

public sealed record OutsourceImportRequest(OutsourceImportRow[]? Rows);

/// <summary>Preview of one import row: READY rows carry the IPMS snapshot that will be stored.</summary>
public sealed record OutsourceImportPreviewRow(
    int RowNumber, string WorkOrderNo, string Status, string? Message, string? VendorName, string? OutsourceDate,
    string? Customer, string? MachineModel, string? TypeLabel, int? Quantity, DateOnly? LatestDueDate);

public sealed record OutsourceImportPreviewResponse(int Ready, int Skipped, int Invalid, OutsourceImportPreviewRow[] Rows);

public sealed record OutsourceItemResponse(
    ulong Id, string WorkOrderNo, DateOnly OutsourceDate, ulong VendorId, string VendorName,
    string? Customer, string? MachineModel, string? MechanicalSection, string? MechanicalOwner, string? ElectricalOwner,
    DateOnly? WorkOrderIssuedOn, DateOnly? BomOn, DateOnly? DrawingReleasedOn,
    string? TypeCode, ulong? TypeId, string? TypeLabel, int? Quantity, DateOnly? OriginalDueDate, DateOnly? LatestDueDate,
    string? Remark, string Status, string SendStatus, string MatchState, bool Closed, DateTime? IpmsSyncedAt, DateTime UpdatedAt);

public sealed record OutsourceVendorCount(ulong VendorId, int Count);

public sealed record OutsourceItemSummaryResponse(int Total, OutsourceVendorCount[] Vendors);

public sealed record OutsourceItemUpdate(string? OutsourceDate, ulong? VendorId, string? Remark, string? Status, DateTime? ExpectedUpdatedAt);

public sealed record OutsourceItemVersion(ulong Id, DateTime ExpectedUpdatedAt);

/// <summary>Batch edit: only the fields that are present change; an empty remark clears it.</summary>
public sealed record OutsourceBatchUpdate(OutsourceItemVersion[]? Items, string? OutsourceDate, ulong? VendorId, string? Remark, string? Status);

public sealed record OutsourceBatchDelete(OutsourceItemVersion[]? Items);

public sealed record OutsourceBatchResponse(OutsourceItemResponse[] Items);

public sealed record OutsourceSyncTimeRequest(string? SyncTime);

/// <summary>Manual sync: the given open items, or every open item when none are given.</summary>
public sealed record OutsourceSyncRequest(ulong[]? ItemIds);

public sealed record OutsourceSyncStatusResponse(
    string SyncTime, bool IpmsConfigured, bool Running, DateTime? LastStartedAt, DateTime? LastFinishedAt,
    DateTime? LastSuccessAt, string? LastError, string? LastTrigger, int LastProcessed, int LastChanged, int LastClosed, int LastMissing);

public sealed record OutsourceItemChangeResponse(
    ulong Id, string Field, string Label, string? OldValue, string? NewValue, string Source, string? ActorName, DateTime ChangedAt);
