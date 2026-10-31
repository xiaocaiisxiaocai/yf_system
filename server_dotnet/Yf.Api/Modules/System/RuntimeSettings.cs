using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Data-retention and upload-quota settings maintained on the system settings page (system_configs). The configured
/// App values only seed a missing row on first start; afterwards the page wins. Every consumer reads the in-memory
/// <see cref="AppOptions"/>, which is refreshed at startup and right after a committed change (single instance).
/// </summary>
internal static class RuntimeSettings
{
    internal sealed record Setting(
        string Key, string Label, string Description, long Minimum, long Maximum,
        Func<AppOptions, long> Read, Action<AppOptions, long> Write);

    private const long GiB = 1024L * 1024 * 1024;

    internal static readonly Setting[] All =
    [
        new("retention.audit_days", "操作日志保留天数", "超过天数的操作日志自动清理", 1, 3650,
            options => options.AuditRetentionDays, (options, value) => options.AuditRetentionDays = (int)value),
        new("retention.mail_days", "邮件记录保留天数", "已发送、失败或取消的邮件记录保留天数", 1, 3650,
            options => options.MailRetentionDays, (options, value) => options.MailRetentionDays = (int)value),
        new("retention.mail_pending_days", "待发送邮件有效天数", "超过天数仍未发出的邮件自动取消", 1, 365,
            options => options.MailPendingTtlDays, (options, value) => options.MailPendingTtlDays = (int)value),
        new("retention.upload_session_days", "上传记录保留天数", "已结束的上传会话记录及其临时目录保留天数", 30, 3650,
            options => options.UploadSessionRetentionDays, (options, value) => options.UploadSessionRetentionDays = (int)value),
        new("upload.max_active_sessions_per_user", "每人同时上传数", "每个账号未完成的上传任务上限", 1, 1000,
            options => options.UploadMaxActiveSessionsPerUser, (options, value) => options.UploadMaxActiveSessionsPerUser = (int)value),
        new("upload.max_pending_bytes_per_user", "每人未完成上传总量", "每个账号未完成上传的文件总大小上限（字节）", GiB, 16L * 1024 * GiB,
            options => options.UploadMaxPendingBytesPerUser, (options, value) => options.UploadMaxPendingBytesPerUser = value),
    ];

    internal static Setting? Find(string key) => All.FirstOrDefault(setting => setting.Key == key);

    /// <summary>Seeds missing rows from the configured values (when <paramref name="seed"/>) and applies the stored ones.</summary>
    internal static async Task ApplyAsync(AppOptions options, bool seed, CancellationToken ct = default)
    {
        await using var conn = await new AppDb(options).OpenAsync(ct);
        await using var db = EfDb.Use(conn);
        var keys = All.Select(setting => setting.Key).ToArray();
        var rows = await db.SystemConfigs.Where(config => Enumerable.Contains(keys, config.CfgKey))
            .ToDictionaryAsync(config => config.CfgKey, StringComparer.Ordinal, ct);
        if (seed)
        {
            var now = await DbClock.UtcNowAsync(db, ct, 0);
            foreach (var setting in All.Where(setting => !rows.ContainsKey(setting.Key)))
            {
                var row = new SystemConfig
                {
                    CfgKey = setting.Key,
                    CfgValue = setting.Read(options).ToString(CultureInfo.InvariantCulture),
                    Description = setting.Label,
                    UpdatedAt = now,
                };
                db.SystemConfigs.Add(row);
                rows[setting.Key] = row;
            }
            await db.SaveChangesAsync(ct);
            var overridden = All.Where(setting => rows[setting.Key].CfgValue
                    != setting.Read(options).ToString(CultureInfo.InvariantCulture))
                .Select(setting => setting.Key).ToArray();
            if (overridden.Length > 0)
                Console.Error.WriteLine(
                    $"Managed in the system settings; the configured values are ignored: {string.Join(", ", overridden)}.");
        }
        foreach (var setting in All)
        {
            if (rows.TryGetValue(setting.Key, out var row)) Apply(options, setting.Key, row.CfgValue);
        }
    }

    /// <summary>Applies one stored value; values outside the range (manual edits) keep the current setting.</summary>
    internal static void Apply(AppOptions options, string key, string? value)
    {
        if (Find(key) is not { } setting) return;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= setting.Minimum && number <= setting.Maximum)
            setting.Write(options, number);
    }
}
