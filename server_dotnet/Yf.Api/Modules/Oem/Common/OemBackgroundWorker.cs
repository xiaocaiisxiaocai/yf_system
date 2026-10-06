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
/// before its previous attempt completes. Failures are logged and retried after that
/// job's interval without affecting the others. Correctness never depends on this
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

    private async Task RunLoopAsync(IOemBackgroundJob job, CancellationToken stoppingToken)
    {
        // Keep one job's synchronous setup from delaying the initial start of the
        // other independent loops. Production jobs normally yield on database I/O,
        // but IOemBackgroundJob does not require an asynchronous first operation.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await job.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("OEM background job {Job} failed ({ErrorType}); retrying next interval.", job.Name, error.GetType().Name);
            }

            try { await Task.Delay(job.Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
