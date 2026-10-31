using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// The persisted email policy is deliberately made up of independent switches.
/// Missing optional rows use the enabled default; fresh databases receive every row
/// through BootstrapSeedCatalog or migrations and administrators can then change each switch.
/// </summary>
internal sealed record EmailNotificationPolicy(
    bool GlobalEnabled,
    bool InternalEnabled,
    bool SupplierEnabled,
    bool MessageCreatedEnabled,
    bool FileUploadedEnabled,
    bool ProjectStartedEnabled,
    bool ProjectTerminatedEnabled,
    bool ProjectRestartedEnabled,
    bool ProjectReopenedEnabled,
    bool ProjectMaterialReviewedEnabled,
    bool ProjectDueSoonEnabled,
    bool ProjectOverdueEnabled,
    bool DueInternalEnabled,
    bool DueSupplierEnabled,
    int DueRemindDaysBefore)
{
    internal const string DisabledReason = "邮件提醒规则已关闭，通知已取消";
    internal const string DisabledAuditReason = "NOTIFICATION_POLICY_DISABLED";
    internal const string GlobalKey = "notify.enabled";
    internal const string InternalKey = "notify.internal.enabled";
    internal const string SupplierKey = "notify.supplier.enabled";
    internal const string MessageCreatedKey = "notify.event.message_created";
    internal const string FileUploadedKey = "notify.event.file_uploaded";
    internal const string ProjectStartedKey = "notify.event.project_started";
    internal const string ProjectTerminatedKey = "notify.event.project_terminated";
    internal const string ProjectRestartedKey = "notify.event.project_restarted";
    internal const string ProjectReopenedKey = "notify.event.project_reopened";
    internal const string ProjectMaterialReviewedKey = "notify.event.project_material_reviewed";
    internal const string ProjectDueSoonKey = "notify.event.project_due_soon";
    internal const string ProjectOverdueKey = "notify.event.project_overdue";
    internal const string DueInternalKey = "notify.due.internal.enabled";
    internal const string DueSupplierKey = "notify.due.supplier.enabled";
    internal const string DueRemindDaysBeforeKey = "notify.due.remind_days_before";

    internal const int DefaultDueRemindDaysBefore = 3;
    internal const int MinimumDueRemindDaysBefore = 1;
    internal const int MaximumDueRemindDaysBefore = 30;

    internal const string ProjectDueSoonEvent = "PROJECT_DUE_SOON";
    internal const string ProjectOverdueEvent = "PROJECT_OVERDUE";

    /// <summary>The boolean switches; <see cref="DueRemindDaysBeforeKey"/> is numeric and kept apart.</summary>
    internal static readonly string[] Keys =
    [
        GlobalKey,
        InternalKey,
        SupplierKey,
        MessageCreatedKey,
        FileUploadedKey,
        ProjectStartedKey,
        ProjectTerminatedKey,
        ProjectRestartedKey,
        ProjectReopenedKey,
        ProjectMaterialReviewedKey,
        ProjectDueSoonKey,
        ProjectOverdueKey,
        DueInternalKey,
        DueSupplierKey,
    ];

    internal static async Task<EmailNotificationPolicy> LoadAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        var keys = Keys.Append(DueRemindDaysBeforeKey).ToArray();
        var values = await context.SystemConfigs.Where(config => Enumerable.Contains(keys, config.CfgKey))
            .Select(config => new { config.CfgKey, config.CfgValue })
            .ToDictionaryAsync(config => config.CfgKey, config => config.CfgValue, StringComparer.Ordinal, ct);
        return new(
            Read(values, GlobalKey),
            Read(values, InternalKey),
            Read(values, SupplierKey),
            Read(values, MessageCreatedKey),
            Read(values, FileUploadedKey),
            Read(values, ProjectStartedKey),
            Read(values, ProjectTerminatedKey),
            Read(values, ProjectRestartedKey),
            Read(values, ProjectReopenedKey),
            Read(values, ProjectMaterialReviewedKey),
            Read(values, ProjectDueSoonKey),
            Read(values, ProjectOverdueKey),
            Read(values, DueInternalKey),
            Read(values, DueSupplierKey),
            ReadRemindDays(values));
    }

    private static bool Read(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return true;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var normalized when normalized.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var normalized when normalized.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };
    }

    private static int ReadRemindDays(IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue(DueRemindDaysBeforeKey, out var value)
        && int.TryParse(value?.Trim(), out var days)
        && days is >= MinimumDueRemindDaysBefore and <= MaximumDueRemindDaysBefore
            ? days
            : DefaultDueRemindDaysBefore;

    internal static bool IsDueEvent(string eventType) => eventType is ProjectDueSoonEvent or ProjectOverdueEvent;

    internal bool AllowsEvent(string eventType) => eventType switch
    {
        "MESSAGE_CREATED" => MessageCreatedEnabled,
        "FILE_UPLOADED" => FileUploadedEnabled,
        "PROJECT_STARTED" => ProjectStartedEnabled,
        "PROJECT_TERMINATED" => ProjectTerminatedEnabled,
        "PROJECT_RESTARTED" => ProjectRestartedEnabled,
        "PROJECT_REOPENED" => ProjectReopenedEnabled,
        "PROJECT_MATERIAL_APPROVED" or "PROJECT_MATERIAL_RETURNED" => ProjectMaterialReviewedEnabled,
        ProjectDueSoonEvent => ProjectDueSoonEnabled && (DueInternalEnabled || DueSupplierEnabled),
        ProjectOverdueEvent => ProjectOverdueEnabled && (DueInternalEnabled || DueSupplierEnabled),
        // Test/system mails, retired acceptance events and future event types retain the
        // global switch until an explicit event switch is introduced for them.
        _ => true,
    };

    internal bool AllowsAudience(string? userType) => userType switch
    {
        UserTypes.Internal => InternalEnabled,
        UserTypes.Supplier => SupplierEnabled,
        // Rows without a recipient user (for example maintenance test mail)
        // are governed by the event/global switch only.
        null or "" => true,
        _ => false,
    };

    /// <summary>Due reminders additionally have their own responsible-user / supplier switches.</summary>
    private bool AllowsDueAudience(string eventType, string? userType) =>
        !IsDueEvent(eventType) || userType switch
        {
            UserTypes.Internal => DueInternalEnabled,
            UserTypes.Supplier => DueSupplierEnabled,
            _ => true,
        };

    internal bool Allows(string eventType, string? userType) =>
        GlobalEnabled && AllowsEvent(eventType) && AllowsAudience(userType) && AllowsDueAudience(eventType, userType);

    internal NotificationPolicyResponse ToResponse() => new(
        GlobalEnabled,
        InternalEnabled,
        SupplierEnabled,
        new NotificationEventsResponse(
            MessageCreatedEnabled,
            FileUploadedEnabled,
            ProjectStartedEnabled,
            ProjectTerminatedEnabled,
            ProjectRestartedEnabled,
            ProjectReopenedEnabled,
            ProjectMaterialReviewedEnabled,
            ProjectDueSoonEnabled,
            ProjectOverdueEnabled),
        new NotificationDueResponse(DueInternalEnabled, DueSupplierEnabled, DueRemindDaysBefore));
}
