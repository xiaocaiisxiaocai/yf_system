using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Outsource;

internal static class ContactPersonScopes
{
    internal const string Internal = "INTERNAL";
    internal const string Vendor = "VENDOR";
    internal static bool IsValid(string? value) => value is Internal or Vendor;
}

internal static class ContactColumnTypes
{
    internal const string Text = "TEXT";
    internal const string Multiline = "MULTILINE";
    internal const string Person = "PERSON";
    internal static bool IsValid(string? value) => value is Text or Multiline or Person;
}

internal static class ContactPersonDisplays
{
    internal const string Combined = "COMBINED";
    internal const string Split = "SPLIT";
    internal static bool IsValid(string? value) => value is Combined or Split;
}

internal static class ContactRowVisibilities
{
    internal const string InternalOnly = "INTERNAL_ONLY";
    internal const string AllVendors = "ALL_VENDORS";
    internal const string SpecificVendors = "SPECIFIC_VENDORS";
    internal static bool IsValid(string? value) => value is InternalOnly or AllVendors or SpecificVendors;
}

internal static class ContactImportModes
{
    internal const string Append = "APPEND";
    internal const string ReplaceAll = "REPLACE_ALL";
    internal const string ReplaceGroups = "REPLACE_GROUPS";
    internal static bool IsValid(string? value) => value is Append or ReplaceAll or ReplaceGroups;
}

public sealed record OutsourceContactColumnResponse(
    ulong Id,
    string ColKey,
    string Title,
    string DataType,
    string PersonDisplay,
    string? PhoneTitle,
    bool AutoMerge,
    bool VendorVisible,
    bool IsGroupColumn,
    int SortNo,
    double WidthHint,
    string Status);

public sealed record OutsourceContactPersonResponse(
    ulong Id,
    string ScopeType,
    ulong? VendorId,
    string? VendorName,
    ulong? UserId,
    string? UserEmployeeNo,
    string? UserRealName,
    string Name,
    string Phone,
    string? Email,
    string? TitleHint,
    string Status,
    DateTime UpdatedAt);

public sealed record OutsourceContactCellPersonResponse(
    ulong PersonId,
    string? PrefixLabel,
    string Name,
    string Phone,
    string? Email,
    string ScopeType,
    ulong? VendorId,
    string? VendorName,
    string Status);

public sealed record OutsourceContactRowResponse(
    ulong Id,
    int SortNo,
    string Visibility,
    uint Version,
    ulong[] VendorIds,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, OutsourceContactCellPersonResponse[]> People,
    DateTime UpdatedAt);

public sealed record OutsourceContactVendorOption(ulong Id, string Name, string Status);

public sealed record OutsourceContactUserOption(ulong Id, string EmployeeNo, string RealName, string? Email);

public sealed record OutsourceContactMatrixResponse(
    OutsourceContactColumnResponse[] Columns,
    OutsourceContactRowResponse[] Rows,
    OutsourceContactVendorOption[] Vendors,
    bool CanManage,
    bool CanConfigureColumns);

public sealed record OutsourceContactPeopleResponse(
    OutsourceContactPersonResponse[] People,
    OutsourceContactVendorOption[] Vendors);

public sealed record OutsourceContactPersonReferenceResponse(
    ulong RowId,
    int RowIndex,
    string? GroupValue,
    ulong ColumnId,
    string ColumnTitle,
    string? PrefixLabel);

public sealed record OemContactColumnResponse(
    ulong Id,
    string ColKey,
    string Title,
    string DataType,
    string PersonDisplay,
    string? PhoneTitle,
    bool AutoMerge,
    bool IsGroupColumn,
    int SortNo,
    double WidthHint);

public sealed record OemContactCellPersonResponse(
    ulong PersonId,
    string? PrefixLabel,
    string Name,
    string Phone,
    string? Email);

public sealed record OemContactRowResponse(
    ulong Id,
    int SortNo,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, OemContactCellPersonResponse[]> People);

public sealed record OemContactMatrixResponse(
    OemContactColumnResponse[] Columns,
    OemContactRowResponse[] Rows);

public sealed record OutsourceContactColumnCreate(
    string? Title,
    string? DataType,
    string? PersonDisplay = null,
    string? PhoneTitle = null,
    bool AutoMerge = false,
    bool VendorVisible = true,
    bool IsGroupColumn = false,
    double? WidthHint = null);

public sealed record OutsourceContactColumnUpdate(
    string? Title = null,
    string? DataType = null,
    string? PersonDisplay = null,
    string? PhoneTitle = null,
    bool? AutoMerge = null,
    bool? VendorVisible = null,
    bool? IsGroupColumn = null,
    double? WidthHint = null);

public sealed record OutsourceContactColumnOrderRequest(ulong[]? ColumnIds);

public sealed record OutsourceContactRowCreate(ulong? InsertAfterRowId = null);

public sealed record OutsourceContactRowDeleteRequest(uint ExpectedVersion);

public sealed record OutsourceContactRowOrderRequest(ulong[]? RowIds);

public sealed record OutsourceContactRowVersion(ulong RowId, uint ExpectedVersion);

public sealed record OutsourceContactCellPersonInput(ulong PersonId, string? PrefixLabel = null);

public sealed record OutsourceContactCellsUpdateRequest(
    ulong ColumnId,
    OutsourceContactRowVersion[]? Rows,
    string? TextValue = null,
    OutsourceContactCellPersonInput[]? People = null);

public sealed record OutsourceContactVisibilityUpdateRequest(
    OutsourceContactRowVersion[]? Rows,
    string? Visibility,
    ulong[]? VendorIds = null);

public sealed record OutsourceContactPersonUpsert(
    string? ScopeType,
    ulong? VendorId = null,
    ulong? UserId = null,
    string? Name = null,
    string? Phone = null,
    string? Email = null,
    string? TitleHint = null);

public sealed record OutsourceContactHandoverRequest(
    ulong TargetPersonId,
    ulong[]? RowIds = null);

public sealed record OutsourceContactHandoverResponse(int ReplacedCount);

public sealed record OutsourceContactImportCellInput(
    string ColKey,
    string? TextValue = null,
    string? PhonePairText = null);

public sealed record OutsourceContactImportRowInput(
    int RowNumber,
    string? VisibilityText = null,
    OutsourceContactImportCellInput[]? Cells = null);

public sealed record OutsourceContactImportPersonOverride(
    string MatchKey,
    ulong? VendorId);

public sealed record OutsourceContactImportPreviewRequest(
    string? Mode,
    OutsourceContactImportRowInput[]? Rows,
    OutsourceContactImportPersonOverride[]? PersonOverrides = null);

public sealed record OutsourceContactImportPersonPlan(
    string MatchKey,
    string Name,
    string Phone,
    string ScopeType,
    ulong? VendorId,
    string? VendorName,
    string Action,
    string? ExistingPhone,
    ulong? ExistingPersonId,
    bool CanMarkVendor,
    ulong? SuggestedVendorId,
    string? SuggestedVendorName);

public sealed record OutsourceContactImportPreviewRow(
    int RowNumber,
    string Status,
    string? Message,
    string Visibility,
    string[] VendorNames,
    string? GroupValue,
    int ResolvedPeopleCount);

public sealed record OutsourceContactImportPreviewResponse(
    string Mode,
    int Ready,
    int Skipped,
    int Invalid,
    OutsourceContactImportPreviewRow[] Rows,
    OutsourceContactImportPersonPlan[] PeoplePlans,
    int PhoneChangeCount,
    int ReplaceCount,
    string PreviewVersion);

public sealed record OutsourceContactImportRequest(
    string? Mode,
    OutsourceContactImportRowInput[]? Rows,
    OutsourceContactImportPersonOverride[]? PersonOverrides = null,
    bool ConfirmPhoneUpdates = false,
    string? PreviewVersion = null);

public sealed record OutsourceContactImportResponse(
    int Inserted,
    int Replaced,
    int CreatedPeople,
    int UpdatedPhones,
    int Skipped);
