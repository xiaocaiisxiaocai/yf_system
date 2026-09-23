using System.Text.Json;

namespace Yf.Api.Modules.SystemManagement;

public sealed record SystemConfigResponse(string Key, string? Value, string? Description, DateTime UpdatedAt);

public sealed record AuditLogResponse(
    ulong Id,
    ulong? UserId,
    string? EmployeeNo,
    string Action,
    string? TargetType,
    string? TargetId,
    JsonElement? Detail,
    string? Ip,
    DateTime CreatedAt,
    string? ActorName,
    string? TargetName,
    // snapshot | current | unknown
    string ActorNameSource,
    // snapshot | current | unknown
    string TargetNameSource,
    bool CanDelete = false);

public sealed record AuditLogDeleteResponse(int Deleted);

public sealed record NotificationEventsResponse(
    bool MessageCreated,
    bool FileUploaded,
    bool ProjectSubmitted,
    bool ProjectConfirmed,
    bool ProjectRejected,
    bool ProjectWithdrawn);

public sealed record NotificationPolicyResponse(
    bool GlobalEnabled,
    bool InternalEnabled,
    bool SupplierEnabled,
    NotificationEventsResponse Events);

public sealed record MailQueueCounts(ulong Pending, ulong Sending, ulong Sent, ulong Failed, ulong Cancelled);

public sealed record MissingEmailAccount(ulong UserId, string EmployeeNo, string RealName, string UserType, string Status);

public sealed record MailAuditEntry(
    ulong Id,
    string Action,
    string? TargetType,
    string? TargetId,
    // Allow-listed, masked detail fields only.
    IReadOnlyDictionary<string, object?> Detail,
    DateTime CreatedAt);

public sealed record MailStatusResponse(
    bool Configured,
    string? Host,
    int? Port,
    string? From,
    bool NotificationsEnabled,
    NotificationPolicyResponse NotificationPolicy,
    MailQueueCounts Queue,
    DateTime? LatestSentAt,
    DateTime? LatestFailedAt,
    ulong MissingEmailCount,
    IReadOnlyList<MissingEmailAccount> MissingEmailAccounts,
    IReadOnlyList<MailAuditEntry> Recent);
