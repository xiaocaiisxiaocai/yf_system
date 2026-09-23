using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// The persisted email policy is deliberately made up of independent switches.
/// Missing optional rows use the enabled default; fresh databases receive every row
/// through BootstrapSeedCatalog and administrators can then change each switch.
/// </summary>
internal sealed record EmailNotificationPolicy(
    bool GlobalEnabled,
    bool InternalEnabled,
    bool SupplierEnabled,
    bool MessageCreatedEnabled,
    bool FileUploadedEnabled,
    bool ProjectSubmittedEnabled,
    bool ProjectConfirmedEnabled,
    bool ProjectRejectedEnabled,
    bool ProjectWithdrawnEnabled)
{
    internal const string DisabledReason = "邮件提醒规则已关闭，通知已取消";
    internal const string DisabledAuditReason = "NOTIFICATION_POLICY_DISABLED";
    internal const string GlobalKey = "notify.enabled";
    internal const string InternalKey = "notify.internal.enabled";
    internal const string SupplierKey = "notify.supplier.enabled";
    internal const string MessageCreatedKey = "notify.event.message_created";
    internal const string FileUploadedKey = "notify.event.file_uploaded";
    internal const string ProjectSubmittedKey = "notify.event.project_submitted";
    internal const string ProjectConfirmedKey = "notify.event.project_confirmed";
    internal const string ProjectRejectedKey = "notify.event.project_rejected";
    internal const string ProjectWithdrawnKey = "notify.event.project_withdrawn";

    internal static readonly string[] Keys =
    [
        GlobalKey,
        InternalKey,
        SupplierKey,
        MessageCreatedKey,
        FileUploadedKey,
        ProjectSubmittedKey,
        ProjectConfirmedKey,
        ProjectRejectedKey,
        ProjectWithdrawnKey,
    ];

    internal static async Task<EmailNotificationPolicy> LoadAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        var values = await context.SystemConfigs.Where(config => Enumerable.Contains(Keys, config.CfgKey))
            .Select(config => new { config.CfgKey, config.CfgValue })
            .ToDictionaryAsync(config => config.CfgKey, config => config.CfgValue, StringComparer.Ordinal, ct);
        return new(
            Read(values, GlobalKey),
            Read(values, InternalKey),
            Read(values, SupplierKey),
            Read(values, MessageCreatedKey),
            Read(values, FileUploadedKey),
            Read(values, ProjectSubmittedKey),
            Read(values, ProjectConfirmedKey),
            Read(values, ProjectRejectedKey),
            Read(values, ProjectWithdrawnKey));
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

    internal bool AllowsEvent(string eventType) => eventType switch
    {
        "MESSAGE_CREATED" => MessageCreatedEnabled,
        "FILE_UPLOADED" => FileUploadedEnabled,
        "PROJECT_SUBMITTED" => ProjectSubmittedEnabled,
        "PROJECT_CONFIRMED" => ProjectConfirmedEnabled,
        "PROJECT_REJECTED" => ProjectRejectedEnabled,
        "PROJECT_WITHDRAWN" => ProjectWithdrawnEnabled,
        // Test/system mails and future event types retain the existing global
        // switch until an explicit event switch is introduced for them.
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

    internal bool Allows(string eventType, string? userType) =>
        GlobalEnabled && AllowsEvent(eventType) && AllowsAudience(userType);

    internal NotificationPolicyResponse ToResponse() => new(
        GlobalEnabled,
        InternalEnabled,
        SupplierEnabled,
        new NotificationEventsResponse(
            MessageCreatedEnabled,
            FileUploadedEnabled,
            ProjectSubmittedEnabled,
            ProjectConfirmedEnabled,
            ProjectRejectedEnabled,
            ProjectWithdrawnEnabled));
}
