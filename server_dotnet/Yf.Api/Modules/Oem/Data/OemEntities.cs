namespace Yf.Api.Modules.Oem.Data;

// Persistence shapes for the OEM business line. These are deliberately plain
// records of table state; business rules live in the domain/service layers so
// the same rules are not duplicated between EF and the pure policy classes.

public sealed class OemCompany
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? Remark { get; set; }
    public string Status { get; set; } = null!;
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemAccount
{
    public ulong Id { get; set; }
    public string EmployeeNo { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string RealName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public ulong OemCompanyId { get; set; }
    public string Status { get; set; } = null!;
    public bool MustChangePassword { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemRefreshToken
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public string SessionId { get; set; } = null!;
    public string TokenHash { get; set; } = null!;
    public DateTime SessionExpiresAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    /// <summary>Why the token was revoked (<see cref="Yf.Api.Infrastructure.Entities.RefreshRevokeReasons"/>); null while active or for legacy rows.</summary>
    public string? RevokeReason { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OemRetentionTemplate
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string Mode { get; set; } = null!;
    public uint? ReleaseTtlMinutes { get; set; }
    public uint? ReceiptGraceMinutes { get; set; }
    public string Status { get; set; } = null!;
    public ulong ConcurrencyVersion { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemTransfer
{
    public ulong Id { get; set; }
    public string Direction { get; set; } = null!;
    public ulong OemCompanyId { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public ulong? InternalSenderUserId { get; set; }
    public ulong? OemSenderAccountId { get; set; }
    public string LifecycleStatus { get; set; } = null!;
    public ulong RetentionTemplateId { get; set; }
    public string? RetentionMode { get; set; }
    public uint? ReleaseTtlMinutes { get; set; }
    public uint? ReceiptGraceMinutes { get; set; }
    public string? ManifestSha256 { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? ClosedReason { get; set; }
    public string? ClosedByRealm { get; set; }
    public ulong? ClosedById { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ulong ConcurrencyVersion { get; set; }
}

public sealed class OemTransferFile
{
    public ulong Id { get; set; }
    public ulong TransferId { get; set; }
    public ulong? UploadedByInternalUserId { get; set; }
    public ulong? UploadedByOemAccountId { get; set; }
    public string OriginalName { get; set; } = null!;
    public string StoredName { get; set; } = null!;
    public string Ext { get; set; } = null!;
    public string? MimeType { get; set; }
    public ulong SizeBytes { get; set; }
    public string? Md5 { get; set; }
    public string Sha256 { get; set; } = null!;
    public string StoragePath { get; set; } = null!;
    public string PayloadStatus { get; set; } = null!;
    // Legacy column/property name; contains file-validation states, never a malware verdict.
    public string ScanStatus { get; set; } = null!;
    public DateTime? FirstRecipientDownloadAt { get; set; }
    public string? FirstRecipientRealm { get; set; }
    public ulong? FirstRecipientId { get; set; }
    public DateTime? PurgeDueAt { get; set; }
    public string? PurgeReason { get; set; }
    public string? PurgeLeaseOwner { get; set; }
    public DateTime? PurgeLeaseUntil { get; set; }
    public int PurgeAttemptCount { get; set; }
    public DateTime? PurgeNextAttemptAt { get; set; }
    public string? PurgeLastError { get; set; }
    public DateTime? PurgedAt { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemUploadSession
{
    public string Id { get; set; } = null!;
    public ulong TransferId { get; set; }
    public string UploaderRealm { get; set; } = null!;
    public ulong UploaderId { get; set; }
    public string FileName { get; set; } = null!;
    public ulong FileSize { get; set; }
    public string? FileMd5 { get; set; }
    public uint ChunkSize { get; set; }
    public uint TotalChunks { get; set; }
    public string TempDir { get; set; } = null!;
    public ulong ReservedBytes { get; set; }
    public string Status { get; set; } = null!;
    public ulong? ResultFileId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Legacy persistence name retained to preserve pending work and historical evidence.
// New jobs run format/integrity validation only; engine metadata below is historical.
public sealed class OemFileScanJob
{
    public ulong Id { get; set; }
    public ulong FileId { get; set; }
    public string FileSha256 { get; set; } = null!;
    public ulong FileSizeBytes { get; set; }
    public string Status { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? EngineName { get; set; }
    public string? EngineVersion { get; set; }
    public string? SignatureVersion { get; set; }
    public string? ThreatName { get; set; }
    public string? LastError { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OemFilePromotion
{
    public string Id { get; set; } = null!;
    public ulong FileId { get; set; }
    public string FileSha256 { get; set; } = null!;
    public ulong SizeBytes { get; set; }
    public string SourcePath { get; set; } = null!;
    public string TargetPath { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class OemFlowTemplate
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public bool IsDefault { get; set; }
    public string Status { get; set; } = null!;
    public ulong ConcurrencyVersion { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemFlowTemplateScope
{
    public ulong Id { get; set; }
    public ulong TemplateId { get; set; }
    public ulong DepartmentId { get; set; }
}

public sealed class OemFlowTemplateNode
{
    public ulong Id { get; set; }
    public ulong TemplateId { get; set; }
    public int SortNo { get; set; }
    public string Name { get; set; } = null!;
    public string ApproverSource { get; set; } = null!;
    public string ApprovalMode { get; set; } = null!;
    public string SelfPolicy { get; set; } = null!;
    public bool Enabled { get; set; }
}

public sealed class OemFlowTemplateNodeUser
{
    public ulong Id { get; set; }
    public ulong NodeId { get; set; }
    public ulong UserId { get; set; }
    public string Role { get; set; } = null!;
}

public sealed class OemFlowInstance
{
    public ulong Id { get; set; }
    public ulong TransferId { get; set; }
    public ulong TemplateId { get; set; }
    public ulong InitiatorUserId { get; set; }
    public ulong InitiatorSectionId { get; set; }
    public string Status { get; set; } = null!;
    public string TemplateSnapshot { get; set; } = null!;
    public string? BlockedReason { get; set; }
    public int? CurrentSortNo { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OemFlowInstanceNode
{
    public ulong Id { get; set; }
    public ulong InstanceId { get; set; }
    public int SortNo { get; set; }
    public string Name { get; set; } = null!;
    public string ApproverSource { get; set; } = null!;
    public string ApprovalMode { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? SkipReason { get; set; }
    public bool UsedFallback { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class OemFlowTask
{
    public ulong Id { get; set; }
    public ulong InstanceId { get; set; }
    public ulong InstanceNodeId { get; set; }
    public ulong ApproverUserId { get; set; }
    public string Status { get; set; } = null!;
    public string? Reason { get; set; }
    public ulong? ReplacesTaskId { get; set; }
    public string? ReassignReason { get; set; }
    public ulong? ReassignedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>When the task last became PENDING for its approver (node activation or reassignment).</summary>
    public DateTime? ActivatedAt { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OemDownloadSession
{
    public string Id { get; set; } = null!;
    public ulong FileId { get; set; }
    public string FileStoredName { get; set; } = null!;
    public string FileSha256 { get; set; } = null!;
    public string ActorRealm { get; set; } = null!;
    public ulong ActorId { get; set; }
    public string LoginSessionId { get; set; } = null!;
    public string Purpose { get; set; } = null!;
    public bool RecipientSide { get; set; }
    public ulong ExpectedSize { get; set; }
    public string Status { get; set; } = null!;
    public DateTime? LastProgressAt { get; set; }
    public DateTime AbsoluteDeadline { get; set; }
    public ulong ConcurrencyVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class OemDownloadRange
{
    public ulong Id { get; set; }
    public string SessionId { get; set; } = null!;
    public ulong StartOffset { get; set; }
    public ulong EndOffset { get; set; }
}

public sealed class OemDownloadLease
{
    public string Id { get; set; } = null!;
    public string SessionId { get; set; } = null!;
    public ulong FileId { get; set; }
    public string Owner { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public DateTime HardDeadline { get; set; }
    public string Status { get; set; } = null!;
}
