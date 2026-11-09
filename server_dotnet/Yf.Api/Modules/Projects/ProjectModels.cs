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

    [JsonPropertyName("robotPartId")]
    public ulong? RobotPartId { get; init; }

    [JsonPropertyName("priorityId")]
    public ulong? PriorityId { get; init; }

    /// <summary>Supplier contacts (1–10) of the selected supplier; the joined names become robotOwnerName.</summary>
    [JsonPropertyName("robotOwnerContactIds")]
    public ulong[]? RobotOwnerContactIds { get; init; }

    [JsonPropertyName("robotTypeId")]
    public ulong? RobotTypeId { get; init; }

    [JsonPropertyName("expectedCompletionDate")]
    public string? ExpectedCompletionDate { get; init; }

    /// <summary>
    /// Required on <c>PUT /projects/{id}</c>. Missing or null is 400; a mismatch with the locked
    /// row is 409/40903. Create ignores this field.
    /// </summary>
    [JsonPropertyName("expectedUpdatedAt")]
    public DateTime? ExpectedUpdatedAt { get; init; }
}

public sealed class ProjectCopyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }
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

public sealed class RobotPartUpsertRequest
{
    [JsonPropertyName("supplierId")]
    public ulong SupplierId { get; init; }

    [JsonPropertyName("partNumber")]
    public string? PartNumber { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("sortNo")]
    public int SortNo { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public sealed class ProjectStatusRequest
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>
    /// Optional guard: the status the caller saw. When given and the locked project has another status the
    /// change fails with 409 / 40903; null skips the check.
    /// </summary>
    [JsonPropertyName("expectedStatus")]
    public string? ExpectedStatus { get; init; }
}

public sealed class ProjectTransferRequest
{
    [JsonPropertyName("responsibleUserId")]
    public ulong? ResponsibleUserId { get; init; }

    /// <summary>
    /// Optional guard: the responsible user the caller saw. When given and the locked project has another
    /// one the transfer fails with 409 / 40903; null skips the check.
    /// </summary>
    [JsonPropertyName("expectedResponsibleUserId")]
    public ulong? ExpectedResponsibleUserId { get; init; }
}

public sealed class ProjectReopenRequest
{
    /// <summary>The updatedAt the responsible user saw; a newer change fails with 409 / 40903.</summary>
    [JsonPropertyName("expectedUpdatedAt")]
    public DateTime? ExpectedUpdatedAt { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class ProjectMaterialReviewRequest
{
    /// <summary>The latestSupplierFileId the reviewer saw; a newer supplier upload fails with 409 / 40903.</summary>
    [JsonPropertyName("expectedSupplierFileId")]
    public ulong? ExpectedSupplierFileId { get; init; }

    /// <summary>Required when returning the material.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class MessageCreateRequest
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>Optional idempotency key. A replay of the same sender, project, and payload returns the original message.</summary>
    [JsonPropertyName("clientMessageId")]
    public string? ClientMessageId { get; init; }

    /// <summary>
    /// Typed signature: the name of the supplier contact providing the message. Required for supplier senders
    /// whose company has an active contact, and must match one of those contacts.
    /// </summary>
    [JsonPropertyName("signerName")]
    public string? SignerName { get; init; }
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
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ulong SupplierId { get; init; }
    public string Status { get; init; } = string.Empty;
    public ulong CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? SupplierName { get; init; }
    public string? CreatedByName { get; init; }
    public string[] WorkOrderNos { get; set; } = [];
    public string? MachineModel { get; init; }
    public ulong? RobotPartId { get; init; }
    public string? RobotPartNumber { get; init; }
    public string? RobotModelName { get; init; }
    public string? LegacyRobotModelName { get; init; }
    public ulong? ResponsibleUserId { get; init; }
    public string? ResponsibleUserEmployeeNo { get; init; }
    public string? ResponsibleUserName { get; init; }
    public string? RobotOwnerName { get; init; }
    public ulong[] RobotOwnerContactIds { get; set; } = [];
    public ulong? SectionId { get; init; }
    public string? SectionName { get; init; }
    public ulong? PriorityId { get; init; }
    public string? PriorityName { get; init; }
    public ulong? RobotTypeId { get; init; }
    public string? RobotTypeName { get; init; }
    public DateTime? ExpectedCompletionDate { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? MaterialReviewStatus { get; init; }
    public ulong? MaterialReviewFileId { get; init; }
    public string? MaterialReviewReason { get; init; }
    /// <summary>Latest available supplier-to-company file id; only loaded while the project is in progress.</summary>
    public ulong? LatestSupplierFileId { get; init; }
    public bool HasCopyHistory { get; set; }
    public ulong UnreadMessages { get; set; }
    public ulong? CopySourceProjectId { get; set; }
    public string? CopySourceProjectName { get; set; }
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
    public ulong? SignerContactId { get; init; }
    public string? SignerName { get; init; }
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
    public string? Sha256 { get; init; }
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

    /// <summary>
    /// Common project fields shared by list, detail, workflow and write endpoints. The responsible user's employee
    /// number is internal data: supplier-side readers pass <paramref name="includeEmployeeNo"/> false and keep the name.
    /// <paramref name="viewerId"/> sets <c>canManage</c> for the internal responsible user;
    /// <paramref name="canSupervise"/> is the caller's administrator or organization-leader authority over the
    /// project's section (see <see cref="ProjectDeletionAuthorization"/>), which allows deleting and transferring.
    /// </summary>
    internal static ProjectResponse Project(ProjectRow row, bool includeEmployeeNo = true, ulong? viewerId = null,
        bool canSupervise = false) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        SupplierId = row.SupplierId,
        SupplierName = row.SupplierName,
        Status = row.Status,
        CreatedBy = row.CreatedBy,
        CreatedByName = row.CreatedByName,
        WorkOrderNos = row.WorkOrderNos,
        MachineModel = row.MachineModel,
        RobotPartId = row.RobotPartId,
        RobotPartNumber = row.RobotPartNumber,
        RobotModelName = row.RobotModelName,
        ResponsibleUserId = row.ResponsibleUserId,
        ResponsibleUserEmployeeNo = includeEmployeeNo ? row.ResponsibleUserEmployeeNo : null,
        ResponsibleUserName = row.ResponsibleUserName,
        RobotOwnerName = row.RobotOwnerName,
        RobotOwnerContactIds = row.RobotOwnerContactIds,
        SectionId = row.SectionId,
        SectionName = row.SectionName,
        PriorityId = row.PriorityId,
        PriorityName = row.PriorityName,
        RobotTypeId = row.RobotTypeId,
        RobotTypeName = row.RobotTypeName,
        ExpectedCompletionDate = row.ExpectedCompletionDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        MaterialReviewStatus = ProjectWorkflowRules.EffectiveMaterialReview(row),
        MaterialReviewReason = ProjectWorkflowRules.EffectiveMaterialReview(row) == MaterialReviewStatuses.Returned
            ? row.MaterialReviewReason : null,
        LatestSupplierFileId = row.LatestSupplierFileId,
        HasCopyHistory = row.HasCopyHistory,
        UnreadMessages = row.UnreadMessages,
        CopySource = row.CopySourceProjectId is { } sourceProjectId && row.CopySourceProjectName is { } sourceProjectName
            ? new ProjectCopySourceRef(sourceProjectId, sourceProjectName)
            : null,
        CompletedAt = row.CompletedAt is { } completedAt ? Utc(completedAt) : null,
        CanManage = includeEmployeeNo && viewerId is { } viewer && row.ResponsibleUserId == viewer,
        CanDelete = includeEmployeeNo && canSupervise && row.Status == ProjectStatuses.Terminated,
        CanTransfer = includeEmployeeNo && (canSupervise || viewerId is { } transferViewer && row.ResponsibleUserId == transferViewer),
        CreatedAt = Utc(row.CreatedAt),
        UpdatedAt = Utc(row.UpdatedAt),
    };

    internal static PageResponse<T> Page<T>(IReadOnlyList<T> list, ulong total, ulong page, ulong pageSize) =>
        new(list, total, page, pageSize);

    internal static (ulong Page, ulong Size) ClampPage(ulong page, ulong size)
    {
        var actualSize = Math.Clamp(size, 1UL, 100UL);
        var maxPage = ulong.MaxValue / actualSize;
        return (Math.Clamp(page, 1UL, maxPage), actualSize);
    }
}
