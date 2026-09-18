using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>One periodic OEM maintenance duty (scan, promote, purge, reconcile, ...).</summary>
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
/// (composite). Jobs are isolated: one failing job is logged and retried on its next
/// interval without affecting the others. Correctness never depends on this process
/// being the only one: every job claims its rows with database leases.
/// </summary>
public sealed class OemBackgroundWorker(IEnumerable<IOemBackgroundJob> jobs, AppOptions options, ILogger<OemBackgroundWorker> logger) : BackgroundService
{
    private readonly IOemBackgroundJob[] jobs = jobs.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled || jobs.Length == 0) return;
        var due = jobs.ToDictionary(job => job.Name, _ => DateTime.MinValue, StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            foreach (var job in jobs)
            {
                if (DateTime.UtcNow < due[job.Name]) continue;
                due[job.Name] = DateTime.UtcNow.Add(job.Interval);
                try { await job.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) { logger.LogWarning("OEM background job {Job} failed ({ErrorType}); retrying next interval.", job.Name, error.GetType().Name); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
