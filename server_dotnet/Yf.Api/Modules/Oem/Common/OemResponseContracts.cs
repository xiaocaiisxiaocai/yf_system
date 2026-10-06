using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Oem.Common;

public sealed record OemPageResponse<T>(IReadOnlyList<T> List, ulong Total, ulong Page, uint PageSize);

public sealed record OemOptionResponse(ulong Id, string Name);

public sealed record OemCompanyOptionResponse(ulong Id, string Name, bool CanReceive, string? UnavailableReason);

public sealed record OemCompanyListItemResponse(
    ulong Id,
    string Name,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    string? Remark,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long AccountCount,
    long ActiveAccountCount);

public sealed record OemCompanyResponse(
    ulong Id,
    string Name,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    string? Remark,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record OemAccountResponse(
    ulong Id,
    string EmployeeNo,
    string RealName,
    string Email,
    ulong CompanyId,
    string Status,
    bool MustChangePassword,
    bool Locked,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record OemRetentionTemplateResponse(
    ulong Id,
    string Name,
    string Mode,
    uint? ReleaseTtlMinutes,
    uint? ReceiptGraceMinutes,
    string Status,
    ulong Version,
    string Summary,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record OemSettingResponse(
    string Key,
    string Label,
    string Kind,
    string Value,
    long? Min,
    long? Max,
    bool ReadOnly,
    string? UnsupportedReason,
    string? Hint);

public sealed record OemFlowPersonResponse(ulong Id, string EmployeeNo, string RealName);

public sealed record OemFlowNodePersonResponse(ulong Id, string? EmployeeNo, string? RealName, bool Eligible);

public sealed record OemFlowNodeResponse(
    int SortNo,
    string Name,
    string ApproverSource,
    string ApprovalMode,
    string SelfPolicy,
    bool Enabled,
    IReadOnlyList<OemFlowNodePersonResponse> Approvers,
    IReadOnlyList<OemFlowNodePersonResponse> Fallbacks);

public sealed record OemFlowScopeResponse(ulong Id, string Name, string Kind, string Status);

public sealed record OemFlowTemplateResponse(
    ulong Id,
    string Name,
    bool IsDefault,
    string Status,
    ulong Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<OemFlowNodeResponse> Nodes,
    IReadOnlyList<OemFlowScopeResponse> Scopes);

public sealed record OemRoutingScopeResponse(ulong Id, string Name, string Kind);

public sealed record OemRoutingApproverResponse(ulong Id, string EmployeeNo, string RealName);

public sealed record OemRoutingNodeResponse(
    int SortNo,
    string Name,
    string ApproverSource,
    string ApprovalMode,
    bool Skipped,
    string? SkipReason,
    bool UsedFallback,
    string? ScopeName,
    IReadOnlyList<OemRoutingApproverResponse?> Approvers);

public sealed record OemRoutingPreviewResponse(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OemOptionResponse? Template,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OemRoutingScopeResponse? MatchedScope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RequiresApproval,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OemRoutingNodeResponse>? Nodes);

public sealed record OemInternalUserOptionResponse(
    ulong Id,
    string EmployeeNo,
    string RealName,
    bool CanApprove,
    string? DepartmentName);

public sealed record OemPersonRefResponse(string Realm, ulong Id, string EmployeeNo, string RealName);

public sealed record OemTransferSummaryResponse(
    ulong Id,
    string Direction,
    ulong CompanyId,
    string? CompanyName,
    string Title,
    OemPersonRefResponse Sender,
    string LifecycleStatus,
    string? ApprovalStatus,
    string? ApprovalBlockedReason,
    string? ValidationSummary,
    int FileCount,
    ulong TotalBytes,
    int AvailableCount,
    int PurgePendingCount,
    int PurgedCount,
    int MissingCount,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime? ReleasedAt,
    ulong Version);

public sealed record OemTransferRetentionResponse(
    ulong TemplateId,
    string? TemplateName,
    string? Mode,
    uint? ReleaseTtlMinutes,
    uint? ReceiptGraceMinutes,
    string? Summary);

public sealed record OemTransferCapabilitiesResponse(
    bool CanEdit,
    bool CanSend,
    bool CanDelete,
    bool CanReadContent,
    string ContentPurpose);

public sealed record OemTransferFileResponse(
    ulong Id,
    string OriginalName,
    string Ext,
    ulong SizeBytes,
    string Sha256,
    string ValidationStatus,
    string PayloadStatus,
    int? ValidationAttempts,
    string? ValidationMessage,
    DateTime CreatedAt,
    DateTime? FirstRecipientDownloadAt,
    DateTime? PurgeDueAt,
    DateTime? PurgedAt,
    bool Downloadable);

public sealed record OemApprovalTaskResponse(
    ulong Id,
    string Status,
    ulong ApproverUserId,
    string? ApproverName,
    string? ApproverEmployeeNo,
    string? Reason,
    DateTime? DecidedAt,
    ulong? ReplacesTaskId,
    string? ReassignReason,
    ulong Version);

public sealed record OemApprovalNodeResponse(
    int SortNo,
    string Name,
    string ApproverSource,
    string ApprovalMode,
    string Status,
    string? SkipReason,
    bool UsedFallback,
    DateTime? CompletedAt,
    IReadOnlyList<OemApprovalTaskResponse> Tasks);

public sealed record OemApprovalInfoResponse(
    ulong InstanceId,
    string Status,
    string? BlockedReason,
    int? CurrentSortNo,
    ulong Version,
    string? TemplateName,
    IReadOnlyList<OemApprovalNodeResponse> Nodes);

public sealed record OemTransferDetailResponse(
    OemTransferSummaryResponse Summary,
    string? Description,
    OemTransferRetentionResponse Retention,
    string? ManifestSha256,
    DateTime? ExpiresAt,
    string? ClosedReason,
    DateTime? ClosedAt,
    OemTransferCapabilitiesResponse Capabilities,
    IReadOnlyList<OemTransferFileResponse> Files,
    OemApprovalInfoResponse? Approval);

public sealed record OemUploadSessionInitResponse(
    string SessionId,
    uint ChunkSize,
    uint TotalChunks,
    IReadOnlyList<uint> UploadedChunks,
    bool Resumed);

public sealed record OemUploadSessionResponse(
    string SessionId,
    string Status,
    uint ChunkSize,
    uint TotalChunks,
    string FileName,
    ulong FileSize,
    IReadOnlyList<uint> UploadedChunks,
    ulong? ResultFileId,
    bool Expired);

public sealed record OemUploadedFileResponse(
    ulong Id,
    ulong TransferId,
    string OriginalName,
    string Ext,
    ulong SizeBytes,
    string Sha256,
    string ValidationStatus,
    string PayloadStatus,
    DateTime CreatedAt);

public sealed record OemFileValidationStatusResponse(ulong Id, string ValidationStatus, string PayloadStatus);

public sealed record OemPendingApprovalResponse(
    ulong TaskId,
    ulong Version,
    ulong TransferId,
    string Title,
    string CompanyName,
    string SenderName,
    string SenderEmployeeNo,
    string NodeName,
    string ApprovalMode,
    DateTime? SentAt,
    DateTime ActivatedAt);

public sealed record OemDownloadSessionResponse(
    string DownloadSessionId,
    string Url,
    DateTime ExpiresAt,
    string Purpose);

public sealed record OemDownloadStatusResponse(
    string DownloadSessionId,
    string Status,
    string Purpose,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    ulong ExpectedSize,
    ulong DeliveredBytes);

public sealed record OemAuditLogResponse(
    ulong Id,
    string Action,
    string ActorRealm,
    ulong? ActorId,
    string? EmployeeNo,
    string? TargetType,
    string? TargetId,
    string? Ip,
    DateTime CreatedAt,
    JsonElement? Detail);
