namespace Yf.Api.Infrastructure;

internal static class OemReadinessStatuses
{
    internal const string Ready = "ready";
    internal const string NotReady = "not-ready";
    internal const string NotVerified = "not-verified";
}

internal sealed record OemReadinessDatabaseEvidence(
    bool? ReconciliationReady)
{
    internal static readonly OemReadinessDatabaseEvidence NotVerified = new((bool?)null);
}

internal sealed record OemReadinessReport(
    bool Configured,
    bool ReadyForTransfers,
    string Storage,
    string Worker,
    string Reconciliation,
    IReadOnlyList<string> Issues);
