namespace Yf.Api.Infrastructure;

/// <summary>
/// Diagnoses the optional OEM transfer path without changing collaboration startup readiness.
/// It writes only uniquely named harmless probes under the configured OEM root and never emits paths.
/// </summary>
internal static class OemReadiness
{
    internal static Task<OemReadinessReport?> CheckAsync(
        AppOptions options,
        OemReadinessDatabaseEvidence databaseEvidence,
        CancellationToken ct) => CheckAsync(
            options,
            databaseEvidence,
            DevelopmentReadiness.ProbeRootAsync,
            ct);

    internal static async Task<OemReadinessReport?> CheckAsync(
        AppOptions options,
        OemReadinessDatabaseEvidence databaseEvidence,
        Func<string, CancellationToken, Task<bool>> probeStorage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.OemStorageRoot)) return null;

        var issues = new List<string>();
        var storageReady = await probeStorage(options.OemStorageRoot, ct);
        var storage = storageReady ? OemReadinessStatuses.Ready : OemReadinessStatuses.NotReady;
        if (!storageReady) issues.Add("oem-storage-read-write-failed");

        var workerReady = options.WorkerEnabled;
        var worker = workerReady ? OemReadinessStatuses.Ready : OemReadinessStatuses.NotReady;
        if (!workerReady) issues.Add("oem-worker-disabled");

        var reconciliation = databaseEvidence.ReconciliationReady switch
        {
            true => OemReadinessStatuses.Ready,
            false => OemReadinessStatuses.NotReady,
            null => OemReadinessStatuses.NotVerified,
        };
        if (databaseEvidence.ReconciliationReady == false) issues.Add("oem-reconciliation-required");
        else if (databaseEvidence.ReconciliationReady is null) issues.Add("oem-reconciliation-not-verified");

        var readyForTransfers = storage == OemReadinessStatuses.Ready
            && worker == OemReadinessStatuses.Ready
            && reconciliation == OemReadinessStatuses.Ready;
        return new(true, readyForTransfers, storage, worker, reconciliation, issues);
    }
}
