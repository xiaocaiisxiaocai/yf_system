using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Policies;

public static class RetentionModes
{
    public const string Keep = "KEEP";
    public const string AfterRelease = "AFTER_RELEASE";
    public const string AfterFirstReceipt = "AFTER_FIRST_RECEIPT";
    public const string FirstReceiptOrDeadline = "FIRST_RECEIPT_OR_DEADLINE";

    public static readonly IReadOnlyList<string> All = [Keep, AfterRelease, AfterFirstReceipt, FirstReceiptOrDeadline];
}

/// <summary>Durations snapshotted onto a transfer when it is sent (template changes never apply retroactively).</summary>
public sealed record RetentionSnapshot(string Mode, uint? ReleaseTtlMinutes, uint? ReceiptGraceMinutes)
{
    public IRetentionStrategy Strategy => RetentionStrategies.For(Mode);
}

/// <summary>
/// Deletion timing for one retention mode (strategy pattern). All times are database
/// UTC. A strategy never extends a deadline that is already set: a repeated receipt
/// or a late recalculation can only keep or shorten a file's lifetime.
/// </summary>
public interface IRetentionStrategy
{
    string Mode { get; }

    /// <summary>Validates template durations for this mode (unused durations must be empty).</summary>
    void Validate(uint? releaseTtlMinutes, uint? receiptGraceMinutes);

    /// <summary>Latest lifetime of the transfer, computed once at release.</summary>
    DateTime? ExpiresAt(RetentionSnapshot snapshot, DateTime releasedAt);

    /// <summary>Per-file purge time initialised in the release transaction.</summary>
    DateTime? InitialPurgeDue(RetentionSnapshot snapshot, DateTime releasedAt);

    /// <summary>Per-file purge time after the first complete recipient download.</summary>
    DateTime? PurgeDueAfterFirstReceipt(RetentionSnapshot snapshot, DateTime? currentDue, DateTime? expiresAt, DateTime completedAt);
}

public static class RetentionStrategies
{
    /// <summary>Upper bound for any configured duration: ten years.</summary>
    public const uint MaximumMinutes = 3650u * 24 * 60;

    private static readonly IReadOnlyDictionary<string, IRetentionStrategy> Strategies =
        new IRetentionStrategy[] { new KeepStrategy(), new AfterReleaseStrategy(), new AfterFirstReceiptStrategy(), new FirstReceiptOrDeadlineStrategy() }
            .ToDictionary(strategy => strategy.Mode, StringComparer.Ordinal);

    public static IRetentionStrategy For(string mode) =>
        Strategies.TryGetValue(mode, out var strategy) ? strategy : throw ApiException.BadRequest("删除策略模式无效");

    internal static DateTime? Earliest(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : left < right ? left : right;

    internal static void RequireDuration(uint? value, string label)
    {
        if (value is null or 0) throw ApiException.BadRequest($"{label}必须为正数");
        if (value > MaximumMinutes) throw ApiException.BadRequest($"{label}不能超过 3650 天");
    }

    internal static void RequireEmpty(uint? value, string label)
    {
        if (value is not null) throw ApiException.BadRequest($"该删除策略不使用{label}");
    }

    private sealed class KeepStrategy : IRetentionStrategy
    {
        public string Mode => RetentionModes.Keep;

        public void Validate(uint? releaseTtlMinutes, uint? receiptGraceMinutes)
        {
            RequireEmpty(releaseTtlMinutes, "发布后期限");
            RequireEmpty(receiptGraceMinutes, "接收宽限期");
        }

        public DateTime? ExpiresAt(RetentionSnapshot snapshot, DateTime releasedAt) => null;
        public DateTime? InitialPurgeDue(RetentionSnapshot snapshot, DateTime releasedAt) => null;
        public DateTime? PurgeDueAfterFirstReceipt(RetentionSnapshot snapshot, DateTime? currentDue, DateTime? expiresAt, DateTime completedAt) => currentDue;
    }

    private sealed class AfterReleaseStrategy : IRetentionStrategy
    {
        public string Mode => RetentionModes.AfterRelease;

        public void Validate(uint? releaseTtlMinutes, uint? receiptGraceMinutes)
        {
            RequireDuration(releaseTtlMinutes, "发布后期限");
            RequireEmpty(receiptGraceMinutes, "接收宽限期");
        }

        public DateTime? ExpiresAt(RetentionSnapshot snapshot, DateTime releasedAt) => releasedAt.AddMinutes(snapshot.ReleaseTtlMinutes!.Value);
        public DateTime? InitialPurgeDue(RetentionSnapshot snapshot, DateTime releasedAt) => ExpiresAt(snapshot, releasedAt);
        public DateTime? PurgeDueAfterFirstReceipt(RetentionSnapshot snapshot, DateTime? currentDue, DateTime? expiresAt, DateTime completedAt) => currentDue;
    }

    private sealed class AfterFirstReceiptStrategy : IRetentionStrategy
    {
        public string Mode => RetentionModes.AfterFirstReceipt;

        public void Validate(uint? releaseTtlMinutes, uint? receiptGraceMinutes)
        {
            RequireEmpty(releaseTtlMinutes, "发布后期限");
            RequireDuration(receiptGraceMinutes, "接收宽限期");
        }

        public DateTime? ExpiresAt(RetentionSnapshot snapshot, DateTime releasedAt) => null;
        // A file nobody downloads is kept indefinitely under this mode; the UI states this explicitly.
        public DateTime? InitialPurgeDue(RetentionSnapshot snapshot, DateTime releasedAt) => null;

        public DateTime? PurgeDueAfterFirstReceipt(RetentionSnapshot snapshot, DateTime? currentDue, DateTime? expiresAt, DateTime completedAt) =>
            Earliest(currentDue, completedAt.AddMinutes(snapshot.ReceiptGraceMinutes!.Value));
    }

    private sealed class FirstReceiptOrDeadlineStrategy : IRetentionStrategy
    {
        public string Mode => RetentionModes.FirstReceiptOrDeadline;

        public void Validate(uint? releaseTtlMinutes, uint? receiptGraceMinutes)
        {
            RequireDuration(releaseTtlMinutes, "发布后期限");
            RequireDuration(receiptGraceMinutes, "接收宽限期");
        }

        public DateTime? ExpiresAt(RetentionSnapshot snapshot, DateTime releasedAt) => releasedAt.AddMinutes(snapshot.ReleaseTtlMinutes!.Value);
        public DateTime? InitialPurgeDue(RetentionSnapshot snapshot, DateTime releasedAt) => ExpiresAt(snapshot, releasedAt);

        public DateTime? PurgeDueAfterFirstReceipt(RetentionSnapshot snapshot, DateTime? currentDue, DateTime? expiresAt, DateTime completedAt) =>
            Earliest(Earliest(currentDue, expiresAt), completedAt.AddMinutes(snapshot.ReceiptGraceMinutes!.Value));
    }
}
