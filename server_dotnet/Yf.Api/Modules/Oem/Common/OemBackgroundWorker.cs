using System.Collections.Concurrent;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>One periodic OEM maintenance duty (validate, promote, purge, reconcile, ...).</summary>
public interface IOemBackgroundJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunOnceAsync(CancellationToken ct);
}

/// <summary>Adapter turning a service method into a background job.</summary>
public sealed class DelegateOemJob(string name, TimeSpan interval, Func<CancellationToken, Task> run) : IOemBackgroundJob
{
    public string Name => name;
    public TimeSpan Interval => interval;
    public Task RunOnceAsync(CancellationToken ct) => run(ct);
}

/// <summary>
/// Single hosted service that drives every registered <see cref="IOemBackgroundJob"/>
/// (composite). Each job has an independent, non-overlapping loop: slow validation cannot
/// delay approval, purge or reconciliation work, while one job is never invoked again
/// before its previous attempt completes. Failures are logged and retried with a per-job
/// exponential backoff (reset after a success) without affecting the others. Correctness never depends on this
/// process being the only one: every job claims its rows with database leases.
/// </summary>
public sealed class OemBackgroundWorker(IEnumerable<IOemBackgroundJob> jobs, AppOptions options, ILogger<OemBackgroundWorker> logger) : BackgroundService
{
    private readonly IOemBackgroundJob[] jobs = jobs.ToArray();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled || jobs.Length == 0) return Task.CompletedTask;
        return Task.WhenAll(jobs.Select(job => RunLoopAsync(job, stoppingToken)));
    }

    /// <summary>Upper bound of the retry delay after consecutive failures of one job.</summary>
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(5);

    private async Task RunLoopAsync(IOemBackgroundJob job, CancellationToken stoppingToken)
    {
        // Keep one job's synchronous setup from delaying the initial start of the
        // other independent loops. Production jobs normally yield on database I/O,
        // but IOemBackgroundJob does not require an asynchronous first operation.
        await Task.Yield();
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await job.RunOnceAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                consecutiveFailures++;
                logger.LogWarning(error, "OEM background job {Job} failed ({Failures} consecutive); retrying in {Delay}.",
                    job.Name, consecutiveFailures, RetryDelay(job.Interval, consecutiveFailures));
            }

            try { await Task.Delay(RetryDelay(job.Interval, consecutiveFailures), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// Delay before the next run: the job's interval after a success, otherwise the interval
    /// doubled per consecutive failure and capped at <see cref="MaximumBackoff"/> (never shorter
    /// than the interval itself), so an unavailable database is not hammered every few seconds.
    /// </summary>
    internal static TimeSpan RetryDelay(TimeSpan interval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return interval;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures, 30));
        var backoff = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, MaximumBackoff.Ticks));
        return backoff > interval ? backoff : interval;
    }
}

/// <summary>
/// In-memory per-item retry backoff for batch jobs. An item whose processing keeps throwing
/// (a "poisoned" row) is skipped for a growing period, so the batch query can exclude it and
/// later items are not starved. The state is advisory and process-local: losing it on restart
/// only means the item is retried once more.
/// </summary>
public sealed class OemItemBackoff<TKey> where TKey : notnull
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<TKey, (int Failures, DateTime RetryAt)> failures = new();
    private readonly Func<DateTime> clock;

    public OemItemBackoff() : this(() => DateTime.UtcNow) { }

    internal OemItemBackoff(Func<DateTime> clock) => this.clock = clock;

    /// <summary>Items currently backing off; pass them to the candidate query as exclusions.</summary>
    public TKey[] Suppressed()
    {
        var now = clock();
        foreach (var (key, state) in failures)
            if (state.RetryAt.Add(MaximumDelay) < now) failures.TryRemove(key, out _);
        return failures.Where(entry => entry.Value.RetryAt > now).Select(entry => entry.Key).ToArray();
    }

    public void Failed(TKey key) => failures.AddOrUpdate(key,
        _ => (1, clock().Add(BaseDelay)),
        (_, state) => (state.Failures + 1, clock().Add(Delay(state.Failures + 1))));

    public void Succeeded(TKey key) => failures.TryRemove(key, out _);

    private static TimeSpan Delay(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaximumDelay.Ticks, BaseDelay.Ticks << Math.Min(failures - 1, 7)));
}
