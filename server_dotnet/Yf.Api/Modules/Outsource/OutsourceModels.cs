namespace Yf.Api.Modules.Outsource;



public sealed record OutsourceTypeUpsert(string? Code, string? Name, int? SortNo);

public sealed record OutsourceTypeVendorsRequest(ulong[]? VendorIds);

public sealed record OutsourceDefaultCcRequest(ulong[]? UserIds);

/// <summary>客户全称 → 简称; <paramref name="Manual"/> marks a short name set by hand, which syncs keep.</summary>
public sealed record OutsourceCustomerAliasResponse(ulong Id, string FullName, string ShortName, bool Manual, DateTime UpdatedAt);

/// <summary>A pair added by hand (for a customer IPMS gives no short name); it is marked manual like an edit.</summary>
public sealed record OutsourceCustomerAliasCreate(string? FullName, string? ShortName);

/// <summary>
/// A customer full name used by the IPMS data (MES mirror) or the outsourcing list that has no short name yet, with how many
/// work orders carry it there.
/// </summary>
public sealed record OutsourceMissingCustomerResponse(string FullName, int MesWorkOrders, int OutsourceItems);

/// <summary>A short name set by hand; an empty text hands the row back to the next IPMS sync.</summary>
public sealed record OutsourceCustomerAliasRequest(string? ShortName);

/// <summary>What one sync from the IPMS 工单查询 list changed.</summary>
public sealed record OutsourceCustomerSyncResponse(int Rows, int Customers, int Added, int Updated, int KeptManual,
    OutsourceCustomerAliasResponse[] Aliases);


/// <summary>An ODM/OEMA company used as an outsourcing vendor (its contacts are maintained and read in the ODM/OEMA directory).</summary>
public sealed record OutsourceVendorResponse(ulong Id, string Name, string Status);

public sealed record OutsourceTypeResponse(ulong Id, string Code, string Name, string Status, int SortNo, ulong[] VendorIds);

public sealed record OutsourceCcUserResponse(ulong UserId, string EmployeeNo, string RealName, string Email, string Status);

public sealed record OutsourceImportRow(int RowNumber, string? OutsourceDate, string? WorkOrderNo, string? VendorName, string? TypeName);

public sealed record OutsourceImportRequest(OutsourceImportRow[]? Rows);

/// <summary>Preview of one import row: READY rows carry the IPMS snapshot that will be stored.</summary>
public sealed record OutsourceImportPreviewRow(
    int RowNumber, string WorkOrderNo, string Status, string? Message, string? VendorName, string? OutsourceDate,
    string? Customer, string? MachineModel, string? TypeLabel, int? Quantity, DateOnly? LatestDueDate);

public sealed record OutsourceImportPreviewResponse(int Ready, int Skipped, int Invalid, OutsourceImportPreviewRow[] Rows);

public sealed record OutsourceItemResponse(
    ulong Id, uint SerialNo, string WorkOrderNo, DateOnly OutsourceDate, ulong VendorId, string VendorName,
    string? Customer, string? MachineModel, string? MechanicalSection, string? MechanicalOwner, string? ElectricalOwner,
    DateOnly? WorkOrderIssuedOn, DateOnly? BomOn, DateOnly? DrawingReleasedOn,
    string? TypeCode, ulong? TypeId, string? TypeLabel, int? Quantity, DateOnly? OriginalDueDate, DateOnly? LatestDueDate,
    string? Remark, string Status, string SendStatus, string MatchState, bool Closed, DateTime? IpmsSyncedAt, DateTime UpdatedAt,
    string[]? ChangedFields = null, string? ImportedBy = null, DateTime? ImportedAt = null, string? ModifiedBy = null, DateTime? ModifiedAt = null,
    string? CustomerFullName = null, IReadOnlyDictionary<string, string?>? CustomValues = null);

// Customer is the short name from the 客户简称 table when there is one; CustomerFullName is always IPMS's full name.
// ImportedBy/At: who imported the row and when; ModifiedBy/At: who last edited it by hand (never IPMS syncs or sending).
// ChangedFields: the vendor column keys whose value differs from what was last mailed to this vendor; shown yellow like in the mail.
// CustomValues: the stored value of every active custom field by key (BOOLEAN as "true"/"false", null when empty).

/// <summary><paramref name="TypeId"/> is optional: null keeps the current type (a record whose type was disabled can still be edited).
/// <paramref name="BomOn"/> and <paramref name="DrawingReleasedOn"/> (filled in by hand): null keeps, an empty text clears.
/// <paramref name="Values"/>: custom field values by key; an absent key keeps, an empty text clears.</summary>
public sealed record OutsourceItemUpdate(string? OutsourceDate, ulong? VendorId, ulong? TypeId, string? Remark, string? Status, DateTime? ExpectedUpdatedAt, string? LatestDueDate = null,
    string? BomOn = null, string? DrawingReleasedOn = null, Dictionary<string, string?>? Values = null);

public sealed record OutsourceItemVersion(ulong Id, DateTime ExpectedUpdatedAt);

/// <summary>Batch edit: only the fields that are present change; an empty remark clears it.</summary>
public sealed record OutsourceBatchUpdate(OutsourceItemVersion[]? Items, string? OutsourceDate, ulong? VendorId, ulong? TypeId, string? Remark, string? Status, string? LatestDueDate = null,
    string? BomOn = null, string? DrawingReleasedOn = null, Dictionary<string, string?>? Values = null);

public sealed record OutsourceBatchDelete(OutsourceItemVersion[]? Items);

/// <summary><c>completed=true</c> marks the items 已完成; <c>false</c> reopens them.</summary>
public sealed record OutsourceCompletionRequest(OutsourceItemVersion[]? Items, bool? Completed);

public sealed record OutsourceBatchResponse(OutsourceItemResponse[] Items);

public sealed record OutsourceSyncTimeRequest(string? SyncTime);

/// <summary>Starts (<c>true</c>) or stops (<c>false</c>) a daily scheduled task; manual runs work either way.</summary>
public sealed record ScheduleSwitchRequest(bool Enabled);

/// <summary>Manual sync: the given open items, or every open item when none are given.</summary>
public sealed record OutsourceSyncRequest(ulong[]? ItemIds);

public sealed record OutsourceSyncStatusResponse(
    string SyncTime, bool ScheduleEnabled, bool IpmsConfigured, bool Running, DateTime? LastStartedAt, DateTime? LastFinishedAt,
    DateTime? LastSuccessAt, string? LastError, string? LastTrigger, int LastProcessed, int LastChanged, int LastClosed, int LastMissing);

/// <summary><paramref name="SignerName"/>: for a change the vendor made (<c>VENDOR</c>), the name it signed with.</summary>
public sealed record OutsourceItemChangeResponse(
    ulong Id, string Field, string Label, string? OldValue, string? NewValue, string Source, string? ActorName, DateTime ChangedAt,
    string? SignerName = null);

public sealed record OutsourceMailPreviewRequest(ulong[]? ItemIds);

public sealed record OutsourceMailAddress(string Email, string? Name);

/// <summary>One mail the confirmation would send: vendor recipients and the default copy list are fixed.</summary>
public sealed record OutsourceMailDraft(
    string Key, string Kind, ulong VendorId, string VendorName, string Subject, OutsourceMailAddress[] To,
    OutsourceMailAddress[] DefaultCc, ulong[] ItemIds, string[] WorkOrderNos, int CustomerCount, string HtmlBody);

public sealed record OutsourceMailPreviewResponse(string Fingerprint, OutsourceMailDraft[] Mails);

/// <summary>Extra copy recipients for one draft, identified by its key.</summary>
public sealed record OutsourceExtraCc(string? Key, ulong[]? UserIds, string[]? Emails);

public sealed record OutsourceMailSendRequest(ulong[]? ItemIds, string? Fingerprint, string? RequestKey, OutsourceExtraCc[]? ExtraCc);

public sealed record OutsourceMailRecipientResponse(string Kind, string Email, string? Name, string Source, bool Rejected);

public sealed record OutsourceMailResponse(
    ulong Id, string BatchKey, string Kind, ulong VendorId, string VendorName, string Subject, string Status,
    OutsourceMailRecipientResponse[] Recipients, string[] WorkOrderNos, string? AttachmentName, int Attempts, string? LastError,
    string? CreatedByName, DateTime CreatedAt, DateTime? SentAt);

public sealed record OutsourceMailSendResponse(OutsourceMailResponse[] Mails);
