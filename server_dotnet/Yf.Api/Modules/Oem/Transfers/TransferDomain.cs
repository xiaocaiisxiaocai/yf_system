using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Transfers;

public static class TransferDirections
{
    public const string InternalToOem = "INTERNAL_TO_OEM";
    public const string OemToInternal = "OEM_TO_INTERNAL";
}

/// <summary>Business outcome of a transfer. Storage facts live on the files (<see cref="PayloadStatuses"/>).</summary>
public static class TransferLifecycle
{
    public const string Draft = "DRAFT";
    public const string Sealed = "SEALED";
    public const string Released = "RELEASED";
    public const string Rejected = "REJECTED";
    public const string Blocked = "BLOCKED";
    public const string Cancelled = "CANCELLED";
    public const string Abandoned = "ABANDONED";

    public static bool IsTerminal(string status) => status is Released or Rejected or Blocked or Cancelled or Abandoned;
}

/// <summary>Storage fact of one file; independent from the transfer's business outcome.</summary>
public static class PayloadStatuses
{
    public const string Quarantined = "QUARANTINED";
    public const string Promoting = "PROMOTING";
    public const string Available = "AVAILABLE";
    public const string PurgePending = "PURGE_PENDING";
    public const string Purged = "PURGED";
    public const string StorageLost = "STORAGE_LOST";
    public const string MissingUnverified = "MISSING_UNVERIFIED";

    /// <summary>States in which the file content is expected to exist on disk.</summary>
    public static readonly string[] OnDisk = [Quarantined, Promoting, Available, PurgePending];
    public static readonly string[] Missing = [StorageLost, MissingUnverified];
}

/// <summary>Externally visible scan state of a file (a running job is reported as SCANNING).</summary>
public static class ScanStatuses
{
    public const string Pending = "PENDING";
    public const string Scanning = "SCANNING";
    public const string Clean = "CLEAN";
    public const string Infected = "INFECTED";
    public const string Error = "ERROR";
    public const string Unscannable = "UNSCANNABLE";

    public static bool IsFinalFailure(string status) => status is Infected or Unscannable;
}

public static class PurgeReasons
{
    public const string Retention = "RETENTION";
    public const string Blocked = "BLOCKED";
    public const string DraftExpired = "DRAFT_EXPIRED";
    public const string DraftDeleted = "DRAFT_DELETED";
    public const string FileRemoved = "FILE_REMOVED";
    public const string Cancelled = "CANCELLED";
    public const string Rejected = "REJECTED";
    public const string Orphan = "ORPHAN";
}

/// <summary>
/// Allowed business transitions of a transfer (state pattern expressed as a table).
/// Services call <see cref="Ensure"/> before every lifecycle write so an illegal jump
/// is rejected in one place instead of by scattered status checks.
/// </summary>
public static class TransferStateMachine
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [TransferLifecycle.Draft] = [TransferLifecycle.Sealed, TransferLifecycle.Abandoned],
        [TransferLifecycle.Sealed] = [TransferLifecycle.Released, TransferLifecycle.Rejected, TransferLifecycle.Blocked, TransferLifecycle.Cancelled],
        [TransferLifecycle.Released] = [],
        [TransferLifecycle.Rejected] = [],
        [TransferLifecycle.Blocked] = [],
        [TransferLifecycle.Cancelled] = [],
        [TransferLifecycle.Abandoned] = [],
    };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to, StringComparer.Ordinal);

    public static void Ensure(string from, string to)
    {
        if (!CanTransition(from, to)) throw ApiException.Conflict($"传递单当前状态为 {Label(from)}，不能变更为 {Label(to)}");
    }

    public static string Label(string status) => status switch
    {
        TransferLifecycle.Draft => "草稿",
        TransferLifecycle.Sealed => "已发送",
        TransferLifecycle.Released => "已发布",
        TransferLifecycle.Rejected => "已驳回",
        TransferLifecycle.Blocked => "安全阻断",
        TransferLifecycle.Cancelled => "已终止",
        TransferLifecycle.Abandoned => "已废弃",
        _ => status,
    };
}
