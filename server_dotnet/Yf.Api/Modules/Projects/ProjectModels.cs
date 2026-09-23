using System.Text.Json.Serialization;

using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public sealed class ProjectUpsertRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("supplierId")]
    public ulong SupplierId { get; init; }

    [JsonPropertyName("workOrderNos")]
    public string?[]? WorkOrderNos { get; init; }

    [JsonPropertyName("machineModel")]
    public string? MachineModel { get; init; }

    [JsonPropertyName("robotVendorId")]
    public ulong? RobotVendorId { get; init; }

    [JsonPropertyName("robotModelId")]
    public ulong? RobotModelId { get; init; }

    [JsonPropertyName("responsibleUserId")]
    public ulong? ResponsibleUserId { get; init; }

    [JsonPropertyName("priorityId")]
    public ulong? PriorityId { get; init; }

    [JsonPropertyName("expectedCompletionDate")]
    public string? ExpectedCompletionDate { get; init; }

    [JsonPropertyName("subprojectNames")]
    public string?[]? SubprojectNames { get; init; }
}

public sealed class SubprojectUpsertRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed class ProjectCopyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ProjectDictionaryUpsertRequest
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("parentId")]
    public ulong? ParentId { get; init; }

    [JsonPropertyName("sortNo")]
    public int SortNo { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public sealed class ProjectStatusRequest
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class ProjectSubmitRequest
{
    [JsonPropertyName("confirmSide")]
    public string? ConfirmSide { get; init; }
}

public sealed class ProjectRejectRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("expectedSubmissionId")]
    public ulong? ExpectedSubmissionId { get; init; }
}

public sealed class ProjectDecisionRequest
{
    [JsonPropertyName("expectedSubmissionId")]
    public ulong? ExpectedSubmissionId { get; init; }
}

public sealed class MessageCreateRequest
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

public sealed class MarkMessagesReadRequest
{
    [JsonPropertyName("ids")]
    public ulong[]? Ids { get; init; }
}

public sealed class MarkCollaborationReadRequest
{
    [JsonPropertyName("ids")]
    public ulong[]? Ids { get; init; }
}

internal sealed class ProjectRow
{
    public ulong Id { get; init; }
    public ulong ProjectGroupId { get; init; }
    public string ProjectGroupName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ulong SupplierId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ConfirmSide { get; init; }
    public ulong? LatestSubmissionId { get; init; }
    public ulong CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? SupplierName { get; init; }
    public string? CreatedByName { get; init; }
    public string[] WorkOrderNos { get; set; } = [];
    public string? MachineModel { get; init; }
    public ulong? RobotVendorId { get; init; }
    public string? RobotVendorName { get; init; }
    public ulong? RobotModelId { get; init; }
    public string? RobotModelName { get; init; }
    public ulong? ResponsibleUserId { get; init; }
    public string? ResponsibleUserEmployeeNo { get; init; }
    public string? ResponsibleUserName { get; init; }
    public ulong? SectionId { get; init; }
    public string? SectionName { get; init; }
    public ulong? PriorityId { get; init; }
    public string? PriorityName { get; init; }
    public DateTime? ExpectedCompletionDate { get; init; }
    public bool HasCopyHistory { get; set; }
    public ulong UnreadMessages { get; set; }
    public ulong? CopySourceProjectId { get; set; }
    public string? CopySourceProjectName { get; set; }
}

internal sealed class ProjectGroupRow
{
    public ulong Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ulong SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public string Status { get; init; } = string.Empty;
    public ulong CreatedBy { get; init; }
    public string? CreatedByName { get; init; }
    public string[] WorkOrderNos { get; set; } = [];
    public string? MachineModel { get; init; }
    public ulong? RobotVendorId { get; init; }
    public string? RobotVendorName { get; init; }
    public ulong? RobotModelId { get; init; }
    public string? RobotModelName { get; init; }
    public ulong? ResponsibleUserId { get; init; }
    public string? ResponsibleUserEmployeeNo { get; init; }
    public string? ResponsibleUserName { get; init; }
    public ulong? SectionId { get; init; }
    public string? SectionName { get; init; }
    public ulong? PriorityId { get; init; }
    public string? PriorityName { get; init; }
    public DateTime? ExpectedCompletionDate { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ulong SubprojectCount { get; init; }
    public ulong CompletedCount { get; init; }
    public ulong PendingCount { get; init; }
    public ulong TerminatedCount { get; init; }
    public ulong UnreadMessages { get; set; }
}

internal sealed class UserRow
{
    public ulong Id { get; init; }
    public string EmployeeNo { get; init; } = string.Empty;
    public string RealName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string UserType { get; init; } = string.Empty;
    public ulong? SupplierId { get; init; }
    public ulong? DepartmentId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? DepartmentName { get; init; }
}

internal sealed class ProjectStatusLogRow
{
    public ulong Id { get; init; }
    public ulong ProjectId { get; init; }
    public string? FromStatus { get; init; }
    public string ToStatus { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public ulong OperatorId { get; init; }
    public string? ConfirmSide { get; init; }
    public string? Reason { get; init; }
    public DateTime CreatedAt { get; init; }
}

internal sealed class MessageRow
{
    public ulong Id { get; init; }
    public ulong ProjectId { get; init; }
    public ulong SenderId { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public string SenderType { get; init; } = string.Empty;
}

internal sealed class MessageReadRow
{
    public ulong MessageId { get; init; }
    public ulong UserId { get; init; }
    public DateTime ReadAt { get; init; }
}

internal sealed class MessageImageRow
{
    public ulong Id { get; init; }
    public ulong MessageId { get; init; }
    public string OriginalName { get; init; } = string.Empty;
    public string StoredName { get; init; } = string.Empty;
    public string Ext { get; init; } = string.Empty;
    public ulong SizeBytes { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

internal sealed record MessageImageDownload(
    string Path,
    string OriginalName,
    string MimeType,
    ulong SizeBytes);

internal sealed class ActivityRow
{
    public ulong Id { get; init; }
    public ulong ProjectId { get; init; }
    public string ActivityType { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public ulong? ActorId { get; init; }
    public string ActorName { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public ulong? TargetId { get; init; }
    public string SourceKey { get; init; } = string.Empty;
}

internal sealed class AuditRow
{
    public ulong Id { get; init; }
    public ulong? UserId { get; init; }
    public string? EmployeeNo { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Detail { get; init; }
    public DateTime CreatedAt { get; init; }
}

internal static class ProjectJson
{
    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Common subproject fields shared by list, workflow and write endpoints.</summary>
    internal static ProjectResponse Project(ProjectRow row) => new()
    {
        Id = row.Id,
        ProjectGroupId = row.ProjectGroupId,
        ProjectGroupName = row.ProjectGroupName,
        Name = row.Name,
        Description = row.Description,
        SupplierId = row.SupplierId,
        SupplierName = row.SupplierName,
        Status = row.Status,
        ConfirmSide = row.ConfirmSide,
        LatestSubmissionId = row.LatestSubmissionId,
        CreatedBy = row.CreatedBy,
        CreatedByName = row.CreatedByName,
        WorkOrderNos = row.WorkOrderNos,
        MachineModel = row.MachineModel,
        RobotVendorId = row.RobotVendorId,
        RobotVendorName = row.RobotVendorName,
        RobotModelId = row.RobotModelId,
        RobotModelName = row.RobotModelName,
        ResponsibleUserId = row.ResponsibleUserId,
        ResponsibleUserEmployeeNo = row.ResponsibleUserEmployeeNo,
        ResponsibleUserName = row.ResponsibleUserName,
        SectionId = row.SectionId,
        SectionName = row.SectionName,
        PriorityId = row.PriorityId,
        PriorityName = row.PriorityName,
        ExpectedCompletionDate = row.ExpectedCompletionDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        HasCopyHistory = row.HasCopyHistory,
        UnreadMessages = row.UnreadMessages,
        CopySource = row.CopySourceProjectId is { } sourceProjectId && row.CopySourceProjectName is { } sourceProjectName
            ? new ProjectCopySourceRef(sourceProjectId, sourceProjectName)
            : null,
        CreatedAt = Utc(row.CreatedAt),
        UpdatedAt = Utc(row.UpdatedAt),
    };

    internal static ProjectGroupResponse ProjectGroup(ProjectGroupRow row) => new(
        row.Id,
        row.Name,
        row.Description,
        row.SupplierId,
        row.SupplierName,
        row.Status,
        row.CreatedBy,
        row.CreatedByName,
        row.WorkOrderNos,
        row.MachineModel,
        row.RobotVendorId,
        row.RobotVendorName,
        row.RobotModelId,
        row.RobotModelName,
        row.ResponsibleUserId,
        row.ResponsibleUserEmployeeNo,
        row.ResponsibleUserName,
        row.SectionId,
        row.SectionName,
        row.PriorityId,
        row.PriorityName,
        row.ExpectedCompletionDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        row.CompletedAt is { } completedAt ? Utc(completedAt) : null,
        Utc(row.CreatedAt),
        Utc(row.UpdatedAt),
        row.SubprojectCount,
        row.CompletedCount,
        row.PendingCount,
        row.TerminatedCount,
        row.UnreadMessages);

    internal static PageResponse<T> Page<T>(IReadOnlyList<T> list, ulong total, ulong page, ulong pageSize) =>
        new(list, total, page, pageSize);

    internal static (ulong Page, ulong Size) ClampPage(ulong page, ulong size)
    {
        var actualSize = Math.Clamp(size, 1UL, 100UL);
        var maxPage = ulong.MaxValue / actualSize;
        return (Math.Clamp(page, 1UL, maxPage), actualSize);
    }
}
