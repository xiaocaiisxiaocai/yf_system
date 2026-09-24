namespace Yf.Api.Modules.Projects;

// Response bodies of the Projects module. Property names serialize camelCase, matching the
// anonymous objects these replaced.

public sealed record ProjectDictionaryResponse(
    ulong Id, string Type, string Name, ulong? ParentId, string? ParentName, int SortNo, bool Enabled, bool InUse);

public sealed record RobotPartResponse(
    ulong Id, ulong SupplierId, string SupplierName, string PartNumber, string Model,
    int SortNo, bool Enabled, bool InUse);

public sealed record RobotPartSupplierOption(ulong Id, string Name, string Status);

public sealed record CollaborationSummaryResponse(ulong UnreadCount, ulong LatestId, string Revision);

public sealed record CollaborationNotification(
    ulong Id,
    string Type,
    string Action,
    ulong ProjectId,
    string ProjectName,
    string ProjectGroupName,
    string ActorName,
    string Title,
    string? Summary,
    DateTime OccurredAt,
    ulong? TargetId,
    bool TargetAvailable,
    bool Read);

public sealed record CollaborationNotificationPage(
    IReadOnlyList<CollaborationNotification> List, ulong Total, ulong Page, ulong PageSize, ulong UnreadCount);

public sealed record DashboardMessage(
    ulong Id,
    ulong ProjectId,
    string ProjectName,
    string ProjectGroupName,
    string Content,
    string SenderName,
    DateTime CreatedAt,
    bool Unread);

public sealed record DashboardSummaryResponse(
    ulong ProjectCount,
    ulong ActiveProjectCount,
    ulong PendingConfirmations,
    ulong UnreadMessages,
    IReadOnlyList<DashboardMessage> RecentMessages);

public sealed record DashboardPendingProject(
    ulong Id,
    string Name,
    ulong ProjectGroupId,
    string ProjectGroupName,
    string Status,
    // COMPANY | SUPPLIER
    string ConfirmSide,
    DateTime UpdatedAt);

public sealed record MessageImageResponse(ulong Id, string Name, ulong SizeBytes, string MimeType);

public sealed record MessageResponse(
    ulong Id,
    ulong ProjectId,
    string Content,
    string Status,
    ulong SenderId,
    string SenderName,
    string SenderType,
    DateTime CreatedAt,
    int ReadCount,
    int TotalCount,
    bool ReadByMe,
    IReadOnlyList<MessageImageResponse> Images);

public sealed record MessageReader(ulong UserId, string RealName, string UserType, DateTime? ReadAt);

public sealed record MessageReadsResponse(IReadOnlyList<MessageReader> Readers, IReadOnlyList<MessageReader> Unread);

public sealed record MessageReceiptResponse(ulong Id, int ReadCount, int TotalCount, bool ReadByMe);

public sealed record ProjectActivityItem(
    ulong Id,
    string Type,
    string Action,
    string ActorName,
    DateTime OccurredAt,
    string Title,
    string? Summary,
    ulong? TargetId,
    bool TargetAvailable);

public sealed record ProjectActivitySummary(string Status, bool PendingConfirmation, string? ConfirmSide, DateTime? LastActivityAt);

public sealed record ProjectActivityPage(IReadOnlyList<ProjectActivityItem> List, string? NextCursor, ProjectActivitySummary Summary);

public sealed record ProjectCopySourceRef(ulong ProjectId, string Name);

/// <summary>A subproject as returned by list, workflow and write endpoints.</summary>
public record ProjectResponse
{
    public ulong Id { get; init; }
    public ulong ProjectGroupId { get; init; }
    public string ProjectGroupName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ulong SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ConfirmSide { get; init; }
    public ulong? LatestSubmissionId { get; init; }
    public ulong CreatedBy { get; init; }
    public string? CreatedByName { get; init; }
    public IReadOnlyList<string> WorkOrderNos { get; init; } = [];
    public string? MachineModel { get; init; }
    public ulong? RobotPartId { get; init; }
    public string? RobotPartNumber { get; init; }
    public string? RobotModelName { get; init; }
    public ulong? ResponsibleUserId { get; init; }
    public string? ResponsibleUserEmployeeNo { get; init; }
    public string? ResponsibleUserName { get; init; }
    public ulong? SectionId { get; init; }
    public string? SectionName { get; init; }
    public ulong? PriorityId { get; init; }
    public string? PriorityName { get; init; }
    /// <summary>yyyy-MM-dd</summary>
    public string? ExpectedCompletionDate { get; init; }
    public bool HasCopyHistory { get; init; }
    public ulong UnreadMessages { get; init; }
    public ProjectCopySourceRef? CopySource { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>GET /projects/{id}: the subproject plus the latest rejection reason and submitter.</summary>
public sealed record ProjectDetailResponse : ProjectResponse
{
    public ProjectDetailResponse(ProjectResponse project) : base(project) { }
    public string? RejectReason { get; init; }
    public ulong? LatestSubmitterId { get; init; }
}

public sealed record ProjectGroupResponse(
    ulong Id,
    string Name,
    string? Description,
    ulong SupplierId,
    string? SupplierName,
    string Status,
    ulong CreatedBy,
    string? CreatedByName,
    IReadOnlyList<string> WorkOrderNos,
    string? MachineModel,
    ulong? RobotPartId,
    string? RobotPartNumber,
    string? RobotModelName,
    ulong? ResponsibleUserId,
    string? ResponsibleUserEmployeeNo,
    string? ResponsibleUserName,
    ulong? SectionId,
    string? SectionName,
    ulong? PriorityId,
    string? PriorityName,
    // yyyy-MM-dd
    string? ExpectedCompletionDate,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    ulong SubprojectCount,
    ulong CompletedCount,
    ulong PendingCount,
    ulong TerminatedCount,
    ulong UnreadMessages);

public sealed record ProjectGroupDetailResponse(ProjectGroupResponse Group, IReadOnlyList<ProjectResponse> Projects);

public sealed record ProjectSummaryResponse(
    ulong UnreadMessages, string ActivityRevision, bool PendingConfirmation, ulong? LatestSubmissionId);

public sealed record SupplierOption(ulong Id, string Name);

public sealed record ProjectOwnerOption(ulong Id, string EmployeeNo, string RealName, ulong? SectionId, string? SectionName);

public sealed record ProjectCopyRecord(
    ulong CopyId, ulong SourceProjectId, ulong TargetProjectId, int FileCount, ulong TotalBytes, DateTime CreatedAt);

public sealed record ProjectCopyResponse(ProjectResponse Project, ProjectCopyRecord Copy);

public sealed record ProjectCopyJobResult(ulong ProjectId, ulong CopyFileCount);

public sealed record ProjectCopyJobResponse(
    ulong JobId,
    ulong SourceProjectId,
    ulong ProjectGroupId,
    string TargetName,
    string Status,
    ulong FilesTotal,
    ulong FilesCopied,
    ulong BytesTotal,
    ulong BytesCopied,
    string? Error,
    ProjectCopyJobResult? Result,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record ProjectCopyJobListResponse(IReadOnlyList<ProjectCopyJobResponse> Jobs);

public sealed record ProjectCopyHistoryItem(
    ulong CopyId, ulong ProjectId, string Name, ulong FileCount, ulong TotalBytes, string CopiedByName, DateTime CreatedAt);

public sealed record ProjectCopyHistoryResponse(
    ProjectCopyHistoryItem? Source, IReadOnlyList<ProjectCopyHistoryItem> Copies, bool HasRestrictedRelations);

public sealed record FileCopyHistoryItem(
    ulong SourceFileId, string SourceFileName, bool SourceDeleted, ulong TargetFileId, string TargetFileName, bool TargetDeleted);
