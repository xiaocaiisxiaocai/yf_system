using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Policies;

public enum OemSettingGroup { File, Notify, System }

public enum OemSettingKind { Integer, Boolean, Extensions, Text }

/// <summary>One `oem.*` system parameter: its type, bounds, default and the admin page that owns it.</summary>
public sealed record OemSettingDefinition(string Key, OemSettingKind Kind, OemSettingGroup Group, string Label, string Default, long Min = 0, long Max = long.MaxValue)
{
    public string Normalize(string? input)
    {
        var value = input?.Trim() ?? string.Empty;
        switch (Kind)
        {
            case OemSettingKind.Integer:
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < Min || number > Max)
                    throw ApiException.BadRequest($"{Label}必须为 {Min}~{Max} 之间的整数");
                return number.ToString(CultureInfo.InvariantCulture);
            case OemSettingKind.Boolean:
                return value switch
                {
                    "1" => "true",
                    "0" => "false",
                    _ when bool.TryParse(value, out var flag) => flag ? "true" : "false",
                    _ => throw ApiException.BadRequest($"{Label}必须为 true 或 false"),
                };
            case OemSettingKind.Extensions:
                var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (parts.Length == 0 || parts.Any(part => part.Length is < 1 or > 16 || part.Any(c => !char.IsAsciiLetterOrDigit(c))))
                    throw ApiException.BadRequest($"{Label}需为逗号分隔的扩展名（字母或数字，1~16 位）");
                return string.Join(',', parts);
            default:
                return value;
        }
    }
}

/// <summary>
/// Registry of every `oem.*` parameter (metadata-driven: validation, admin pages and
/// the typed <see cref="OemSettings"/> view are all derived from this single list).
/// </summary>
public static class OemSettingCatalog
{
    public const string AllowedExtsInternalToOem = "oem.upload.allowed_exts.internal_to_oem";
    public const string AllowedExtsOemToInternal = "oem.upload.allowed_exts.oem_to_internal";
    public const string MaxFileSize = "oem.upload.max_file_size";
    public const string MaxTransferSize = "oem.upload.max_transfer_size";
    public const string ChunkSize = "oem.upload.chunk_size";
    public const string MaxConcurrentPerCompany = "oem.upload.max_concurrent_per_company";
    public const string MaxStoragePerCompany = "oem.upload.max_storage_per_company";
    public const string UploadSessionTtlHours = "oem.upload.session_ttl_hours";
    public const string ArchiveMaxEntries = "oem.scan.archive_max_entries";
    public const string ArchiveMaxDepth = "oem.scan.archive_max_depth";
    public const string ArchiveMaxExpandedBytes = "oem.scan.archive_max_expanded_bytes";
    public const string ArchiveMaxRatio = "oem.scan.archive_max_ratio";
    public const string ScanMaxRetries = "oem.scan.max_retries";
    public const string MaxSignatureAgeHours = "oem.scan.max_signature_age_hours";
    public const string BlockOnStaleSignatures = "oem.scan.block_on_stale_signatures";
    public const string BlockedRetentionHours = "oem.scan.blocked_retention_hours";
    public const string DraftTtlHours = "oem.transfer.draft_ttl_hours";
    public const string MaxRangesPerSession = "oem.download.max_ranges_per_session";
    public const string MaxParallelPerSession = "oem.download.max_parallel_per_session";
    public const string MaxRequestsPerMinute = "oem.download.max_requests_per_minute";
    public const string DownloadSessionTtlMinutes = "oem.download.session_ttl_minutes";
    public const string IdleTimeoutSeconds = "oem.download.idle_timeout_seconds";
    public const string MaxDurationMinutes = "oem.download.max_duration_minutes";
    public const string PurgeDrainMinutes = "oem.download.purge_drain_minutes";
    public const string NotifyEnabled = "oem.notify.enabled";
    public const string NotifyApprovalPending = "oem.notify.event.approval_pending";
    public const string NotifyTransferReleased = "oem.notify.event.transfer_released";
    public const string NotifySenderResult = "oem.notify.event.sender_result";
    public const string NotifyReceipt = "oem.notify.event.receipt";
    public const string ReconcileRequired = "oem.storage.reconcile_required";

    private const long GiB = 1024L * 1024 * 1024;

    public static readonly IReadOnlyList<OemSettingDefinition> All =
    [
        new(AllowedExtsInternalToOem, OemSettingKind.Extensions, OemSettingGroup.File, "公司出站允许的扩展名",
            "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip"),
        new(AllowedExtsOemToInternal, OemSettingKind.Extensions, OemSettingGroup.File, "OEM 入站允许的扩展名",
            "7z,doc,docx,dwg,dxf,igs,iges,jpeg,jpg,obj,pdf,png,ppt,pptx,rar,step,stl,stp,xls,xlsx,zip"),
        new(MaxFileSize, OemSettingKind.Integer, OemSettingGroup.File, "单文件大小上限", "2147483648", 1024 * 1024, 64 * GiB),
        new(MaxTransferSize, OemSettingKind.Integer, OemSettingGroup.File, "单传递单总大小上限", "10737418240", 1024 * 1024, 1024 * GiB),
        new(ChunkSize, OemSettingKind.Integer, OemSettingGroup.File, "分片大小", "16777216", 256 * 1024, 64 * 1024 * 1024),
        new(MaxConcurrentPerCompany, OemSettingKind.Integer, OemSettingGroup.File, "每厂商并发上传数", "4", 1, 100),
        new(MaxStoragePerCompany, OemSettingKind.Integer, OemSettingGroup.File, "每厂商在线占用上限", "107374182400", 1024 * 1024, 100 * 1024 * GiB),
        new(UploadSessionTtlHours, OemSettingKind.Integer, OemSettingGroup.File, "上传会话有效期（小时）", "24", 1, 720),
        new(ArchiveMaxEntries, OemSettingKind.Integer, OemSettingGroup.File, "压缩包条目上限", "10000", 1, 1_000_000),
        new(ArchiveMaxDepth, OemSettingKind.Integer, OemSettingGroup.File, "压缩包嵌套层级上限", "5", 1, 32),
        new(ArchiveMaxExpandedBytes, OemSettingKind.Integer, OemSettingGroup.File, "压缩包解压后总量上限", "21474836480", 1024 * 1024, 1024 * GiB),
        new(ArchiveMaxRatio, OemSettingKind.Integer, OemSettingGroup.File, "压缩比上限", "100", 1, 10_000),
        new(ScanMaxRetries, OemSettingKind.Integer, OemSettingGroup.File, "扫描错误最大重试次数", "5", 0, 50),
        new(MaxSignatureAgeHours, OemSettingKind.Integer, OemSettingGroup.File, "病毒库最长未更新时间（小时）", "48", 1, 24 * 90),
        new(BlockOnStaleSignatures, OemSettingKind.Boolean, OemSettingGroup.File, "病毒库过期时暂停放行", "false"),
        new(BlockedRetentionHours, OemSettingKind.Integer, OemSettingGroup.File, "不安全文件隔离保留时间（小时）", "72", 1, 24 * 90),
        new(DraftTtlHours, OemSettingKind.Integer, OemSettingGroup.File, "未发送草稿保留期限（小时）", "720", 1, 24 * 365),
        new(MaxRangesPerSession, OemSettingKind.Integer, OemSettingGroup.File, "单个下载会话区间数上限", "256", 1, 100_000),
        new(MaxParallelPerSession, OemSettingKind.Integer, OemSettingGroup.File, "单个下载会话并行请求上限", "8", 1, 64),
        new(MaxRequestsPerMinute, OemSettingKind.Integer, OemSettingGroup.File, "每账号每分钟下载请求上限", "600", 1, 100_000),
        new(DownloadSessionTtlMinutes, OemSettingKind.Integer, OemSettingGroup.File, "下载会话绝对寿命（分钟）", "1440", 5, 7 * 24 * 60),
        new(IdleTimeoutSeconds, OemSettingKind.Integer, OemSettingGroup.File, "下载无进展取消期限（秒）", "300", 10, 24 * 3600),
        new(MaxDurationMinutes, OemSettingKind.Integer, OemSettingGroup.File, "单个下载请求最长时长（分钟）", "720", 1, 7 * 24 * 60),
        new(PurgeDrainMinutes, OemSettingKind.Integer, OemSettingGroup.File, "到期后活动下载排空期限（分钟）", "30", 0, 24 * 60),
        new(NotifyEnabled, OemSettingKind.Boolean, OemSettingGroup.Notify, "OEM 邮件提醒总开关", "true"),
        new(NotifyApprovalPending, OemSettingKind.Boolean, OemSettingGroup.Notify, "待审批邮件", "true"),
        new(NotifyTransferReleased, OemSettingKind.Boolean, OemSettingGroup.Notify, "发布通知接收人邮件", "true"),
        new(NotifySenderResult, OemSettingKind.Boolean, OemSettingGroup.Notify, "发送结果通知发送人邮件", "true"),
        new(NotifyReceipt, OemSettingKind.Boolean, OemSettingGroup.Notify, "接收与删除进度通知发送人邮件", "false"),
        new(ReconcileRequired, OemSettingKind.Text, OemSettingGroup.System, "存储恢复核对标记", ""),
    ];

    private static readonly IReadOnlyDictionary<string, OemSettingDefinition> ByKey = All.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static OemSettingDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var definition) ? definition : throw ApiException.BadRequest($"未知参数：{key}");

    public static bool TryGet(string key, out OemSettingDefinition definition) => ByKey.TryGetValue(key, out definition!);
}

/// <summary>Typed read-only view of the current `oem.*` values (invalid or missing values fall back to their defaults).</summary>
public sealed class OemSettings(IReadOnlyDictionary<string, string?> values)
{
    public static async Task<OemSettings> LoadAsync(YfDbContext db, CancellationToken ct)
    {
        var keys = OemSettingCatalog.All.Select(item => item.Key).ToArray();
        var values = await db.SystemConfigs.AsNoTracking().Where(config => Enumerable.Contains(keys, config.CfgKey))
            .ToDictionaryAsync(config => config.CfgKey, config => config.CfgValue, StringComparer.Ordinal, ct);
        return new OemSettings(values);
    }

    public string Raw(string key)
    {
        var definition = OemSettingCatalog.Get(key);
        if (!values.TryGetValue(key, out var value) || value is null) return definition.Default;
        try { return definition.Normalize(value); }
        catch (ApiException) { return definition.Default; }
    }

    public long Long(string key) => long.Parse(Raw(key), CultureInfo.InvariantCulture);
    public bool Bool(string key) => Raw(key) == "true";
    public IReadOnlySet<string> Extensions(string key) => Raw(key).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public ulong MaxFileSize => (ulong)Long(OemSettingCatalog.MaxFileSize);
    public ulong MaxTransferSize => (ulong)Long(OemSettingCatalog.MaxTransferSize);
    public uint ChunkSize => (uint)Long(OemSettingCatalog.ChunkSize);
    public int MaxConcurrentPerCompany => (int)Long(OemSettingCatalog.MaxConcurrentPerCompany);
    public ulong MaxStoragePerCompany => (ulong)Long(OemSettingCatalog.MaxStoragePerCompany);
    public TimeSpan UploadSessionTtl => TimeSpan.FromHours(Long(OemSettingCatalog.UploadSessionTtlHours));
    public int ScanMaxRetries => (int)Long(OemSettingCatalog.ScanMaxRetries);
    public TimeSpan BlockedRetention => TimeSpan.FromHours(Long(OemSettingCatalog.BlockedRetentionHours));
    public TimeSpan DraftTtl => TimeSpan.FromHours(Long(OemSettingCatalog.DraftTtlHours));
    public int MaxRangesPerSession => (int)Long(OemSettingCatalog.MaxRangesPerSession);
    public int MaxParallelPerSession => (int)Long(OemSettingCatalog.MaxParallelPerSession);
    public int MaxRequestsPerMinute => (int)Long(OemSettingCatalog.MaxRequestsPerMinute);
    public TimeSpan DownloadSessionTtl => TimeSpan.FromMinutes(Long(OemSettingCatalog.DownloadSessionTtlMinutes));
    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(Long(OemSettingCatalog.IdleTimeoutSeconds));
    public TimeSpan MaxRequestDuration => TimeSpan.FromMinutes(Long(OemSettingCatalog.MaxDurationMinutes));
    public TimeSpan PurgeDrain => TimeSpan.FromMinutes(Long(OemSettingCatalog.PurgeDrainMinutes));
    public bool ReconcileRequired => !string.IsNullOrWhiteSpace(Raw(OemSettingCatalog.ReconcileRequired));

    public ArchiveLimits ArchiveLimits => new(
        Long(OemSettingCatalog.ArchiveMaxEntries), (int)Long(OemSettingCatalog.ArchiveMaxDepth),
        Long(OemSettingCatalog.ArchiveMaxExpandedBytes), Long(OemSettingCatalog.ArchiveMaxRatio));

    public IReadOnlySet<string> AllowedExtensions(string direction) => Extensions(direction == Transfers.TransferDirections.InternalToOem
        ? OemSettingCatalog.AllowedExtsInternalToOem
        : OemSettingCatalog.AllowedExtsOemToInternal);
}

public sealed record ArchiveLimits(long MaxEntries, int MaxDepth, long MaxExpandedBytes, long MaxRatio);
