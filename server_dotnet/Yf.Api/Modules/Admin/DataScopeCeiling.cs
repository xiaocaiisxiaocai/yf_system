using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

/// <summary>
/// The delegation ceiling for data ranges (数据范围与权限拆分契约 §3). A non-administrator may not leave anybody with
/// a range that sees records the actor cannot see. ORG is compared by the organisations it really covers, not only
/// by its level: an ORG manager cannot hand another division an ORG range.
/// <para>
/// Every grant path takes a snapshot before writing and checks the accounts again after its writes, inside the same
/// transaction. Range, role, organisation and leadership can change together, and only the final state shows the
/// range an account actually ends up with; a refused check rolls the whole request back.
/// </para>
/// </summary>
internal static class DataScopeCeiling
{
    internal const string WiderThanActorMessage = "不能授予超出自身数据范围的范围";

    internal sealed class Snapshot
    {
        internal bool Unlimited { get; init; }
        internal Dictionary<string, DataScopeResolution> Grantor { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, IReadOnlyDictionary<ulong, DataScopeResolution>> Before { get; } = new(StringComparer.Ordinal);
    }

    private static readonly DataScopeResolution NoRange = new(DataScopeValues.Self, new HashSet<ulong>());

    /// <summary>Records the actor's ranges and the current ranges of the accounts the write may change.</summary>
    internal static async Task<Snapshot> CaptureAsync(
        YfDbContext context, CurrentUser actor, IReadOnlyCollection<ulong> targetIds, CancellationToken ct)
    {
        if (await AccessService.IsSystemAdminAsync(
                context.Database.Connection(), context.Database.Transaction(), actor.Id, ct))
            return new Snapshot { Unlimited = true };
        var snapshot = new Snapshot();
        foreach (var domain in DataScopeDomains.All)
        {
            snapshot.Grantor[domain] = await DataScopeService.ResolveAsync(context, actor.Id, domain, ct);
            snapshot.Before[domain] = await DataScopeService.ResolveManyAsync(context, targetIds, domain, ct);
        }
        return snapshot;
    }

    /// <summary>
    /// After the writes: every account whose range was set explicitly, or now sees more than before, must stay
    /// inside the actor's range. Narrowing and unchanged ranges are always allowed. Accounts missing from the
    /// snapshot (just created) count as having had no range.
    /// </summary>
    internal static async Task EnsureAsync(
        YfDbContext context, Snapshot snapshot, IReadOnlyCollection<ulong> targetIds,
        IReadOnlyCollection<string> explicitDomains, CancellationToken ct)
    {
        if (snapshot.Unlimited || targetIds.Count == 0) return;
        foreach (var domain in DataScopeDomains.All)
        {
            var grantor = snapshot.Grantor[domain];
            var before = snapshot.Before[domain];
            var after = await DataScopeService.ResolveManyAsync(context, targetIds, domain, ct);
            var explicitlySet = explicitDomains.Contains(domain);
            foreach (var (userId, range) in after)
            {
                var previous = before.GetValueOrDefault(userId) ?? NoRange;
                if ((explicitlySet || !DataScopeService.Covers(previous, range))
                    && !DataScopeService.Covers(grantor, range))
                    throw ApiException.Forbidden(WiderThanActorMessage);
            }
        }
    }
}
