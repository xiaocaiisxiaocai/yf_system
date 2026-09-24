using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// In-process doorbell rung after a copy job is committed, so an idle worker claims it at once instead of
/// on its next fallback poll. The database queue stays authoritative; a missed ring only delays pickup.
/// </summary>
internal sealed class ProjectCopyWakeSignal
{
    private readonly SemaphoreSlim signal = new(0, 1);

    internal void Ring()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await signal.WaitAsync(timeout, ct);
}

/// <summary>Durable database queue with a cross-instance lease and bounded concurrency of one.</summary>
internal sealed class ProjectCopyWorker(
    AppDb database,
    AppOptions options,
    IServiceScopeFactory scopes,
    ILogger<ProjectCopyWorker> logger,
    ProjectCopyWakeSignal? wake = null) : BackgroundService
{
    /// <summary>Fallback poll while idle (jobs from another process, or a ring that was missed).</summary>
    internal static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private readonly ProjectCopyWakeSignal wakeSignal = wake ?? new ProjectCopyWakeSignal();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CopyWorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var leaseConnection = await database.OpenAsync(stoppingToken);
                var lockName = MySqlNamedLock.Name("project-copy-worker", leaseConnection.Database);
                await using var lease = await MySqlNamedLock.TryAcquireAsync(leaseConnection, lockName, 0, stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                var workerEpoch = await AdvanceWorkerEpochAsync(stoppingToken);
                await RunLeaseOwnerAsync(leaseConnection, lockName, workerEpoch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Project copy worker lease cycle failed; durable jobs will be retried.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task RunLeaseOwnerAsync(
        MySqlConnection leaseConnection, string lockName, ulong workerEpoch, CancellationToken stoppingToken)
    {
        if (!await LeaseStillOwnedAsync(leaseConnection, lockName, stoppingToken))
            throw new InvalidOperationException("Project copy worker lease was lost before recovery.");
        await RecoverInterruptedJobsAsync(workerEpoch, stoppingToken);
        await ScavengeOwnedDirectoriesAsync(stoppingToken);
        var nextScavenge = DateTime.UtcNow.AddMinutes(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await LeaseStillOwnedAsync(leaseConnection, lockName, stoppingToken))
                throw new InvalidOperationException("Project copy worker lease was lost.");
            var claim = await ClaimNextAsync(workerEpoch, stoppingToken);
            if (claim is null)
            {
                if (DateTime.UtcNow >= nextScavenge)
                {
                    await ScavengeOwnedDirectoriesAsync(stoppingToken);
                    nextScavenge = DateTime.UtcNow.AddMinutes(10);
                }
                await wakeSignal.WaitAsync(IdlePollInterval, stoppingToken);
                continue;
            }

            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var monitor = MonitorLeaseAsync(leaseConnection, lockName, execution, stoppingToken);
            try { await ExecuteClaimAsync(claim, execution.Token); }
            finally
            {
                execution.Cancel();
                try { await monitor; }
                catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
            }
            if (!await LeaseStillOwnedAsync(leaseConnection, lockName, stoppingToken))
                throw new InvalidOperationException("Project copy worker lease was lost during execution.");
        }
    }

    internal async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var workerEpoch = await AdvanceWorkerEpochAsync(ct);
        await RecoverInterruptedJobsAsync(workerEpoch, ct);
        var claim = await ClaimNextAsync(workerEpoch, ct);
        if (claim is null) return false;
        await ExecuteClaimAsync(claim, ct);
        return true;
    }

    private async Task<ClaimedJob?> ClaimNextAsync(ulong workerEpoch, CancellationToken ct)
    {
        await using var conn = await database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await using var db = EfDb.Use(conn, tx);
        var state = await db.ProjectCopyWorkerStates
            .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
            .SingleAsync(ct);
        if (state.Epoch != workerEpoch)
            throw new InvalidOperationException("Project copy worker epoch is stale.");
        var job = await db.ProjectCopyJobs
            .FromSqlRaw("SELECT * FROM project_copy_jobs WHERE status='pending' ORDER BY created_at,id LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (job is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }
        var now = await DbClock.UtcNowAsync(db, ct);
        var token = Guid.NewGuid().ToString("N");
        job.Status = ProjectCopyJobStatuses.Running;
        job.ExecutionToken = token;
        job.WorkerEpoch = workerEpoch;
        job.FilesCopied = 0;
        job.BytesCopied = 0;
        job.Error = null;
        job.StartedAt = now;
        job.CompletedAt = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(job.Id, token, workerEpoch);
    }

    private async Task ExecuteClaimAsync(ClaimedJob claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ProjectCopyService>()
            .ExecuteJobAsync(claim.Id, claim.Token, claim.WorkerEpoch, ct);
    }

    internal async Task<int> RecoverInterruptedJobsAsync(CancellationToken ct)
    {
        var workerEpoch = await AdvanceWorkerEpochAsync(ct);
        return await RecoverInterruptedJobsAsync(workerEpoch, ct);
    }

    private async Task<int> RecoverInterruptedJobsAsync(ulong expectedWorkerEpoch, CancellationToken ct)
    {
        List<ClaimedJob> recovered = [];
        await using (var conn = await database.OpenAsync(ct))
        await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
        await using (var db = EfDb.Use(conn, tx))
        {
            var state = await db.ProjectCopyWorkerStates
                .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
                .SingleAsync(ct);
            if (state.Epoch != expectedWorkerEpoch)
                throw new InvalidOperationException("Project copy worker epoch is stale during recovery.");
            var jobs = await db.ProjectCopyJobs
                .FromSqlInterpolated($"SELECT * FROM project_copy_jobs WHERE status='running' AND worker_epoch < {expectedWorkerEpoch} FOR UPDATE")
                .AsNoTracking()
                .ToArrayAsync(ct);
            if (jobs.Length != 0)
            {
                var now = await DbClock.UtcNowAsync(db, ct);
                foreach (var job in jobs)
                {
                    var token = job.ExecutionToken;
                    var changed = await db.ProjectCopyJobs.Where(current => current.Id == job.Id
                            && current.Status == ProjectCopyJobStatuses.Running
                            && current.ExecutionToken == token && current.WorkerEpoch == job.WorkerEpoch)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(current => current.Status, ProjectCopyJobStatuses.Pending)
                            .SetProperty(current => current.ExecutionToken, (string?)null)
                            .SetProperty(current => current.FilesCopied, 0UL)
                            .SetProperty(current => current.BytesCopied, 0UL)
                            .SetProperty(current => current.Error, (string?)null)
                            .SetProperty(current => current.UpdatedAt, now), ct);
                    if (changed == 1 && !string.IsNullOrWhiteSpace(token))
                        recovered.Add(new ClaimedJob(job.Id, token, job.WorkerEpoch));
                }
            }
            await tx.CommitAsync(ct);
        }

        foreach (var claim in recovered)
        {
            ct.ThrowIfCancellationRequested();
            try { CleanupExecutionDirectory(claim, ct); }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "Failed to clean interrupted project copy staging for job {JobId}; scavenging will retry.", claim.Id);
            }
        }
        return recovered.Count;
    }

    /// <summary>
    /// Deletes staging directories that no running execution owns. Committed files only reference the
    /// global blob namespace, so copy-job directories never own durable business content.
    /// </summary>
    internal async Task<int> ScavengeOwnedDirectoriesAsync(CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var jobsRoot = Path.Combine(root, "copy-jobs");
        if (!Directory.Exists(jobsRoot)) return 0;
        jobsRoot = FileStorage.ResolveExisting(root, jobsRoot, requireFile: false, ct);
        var candidates = new List<(ulong JobId, string Token, string Directory)>();
        foreach (var jobDirectory in Directory.EnumerateDirectories(jobsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (!ulong.TryParse(Path.GetFileName(jobDirectory), out var jobId)) continue;
            foreach (var executionDirectory in Directory.EnumerateDirectories(jobDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var token = Path.GetFileName(executionDirectory);
                if (token.Length != 32 || token.Any(character => !Uri.IsHexDigit(character))) continue;
                candidates.Add((jobId, token, executionDirectory));
            }
        }
        if (candidates.Count == 0) return 0;

        var jobIds = candidates.Select(candidate => candidate.JobId).Distinct().ToArray();
        await using var conn = await database.OpenAsync(ct);
        await using var db = EfDb.Use(conn);
        var jobs = await db.ProjectCopyJobs.AsNoTracking()
            .Where(job => Enumerable.Contains(jobIds, job.Id))
            .Select(job => new { job.Id, job.Status, job.ExecutionToken })
            .ToDictionaryAsync(job => job.Id, ct);
        var removed = 0;
        foreach (var (jobId, token, executionDirectory) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            jobs.TryGetValue(jobId, out var job);
            if (job is { Status: ProjectCopyJobStatuses.Running } && job.ExecutionToken == token) continue;
            try
            {
                FileStorage.DeleteDirectoryTree(root, executionDirectory, ct);
                removed++;
            }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "Failed to scavenge project copy staging for job {JobId} execution {ExecutionToken}.", jobId, token);
            }
        }
        return removed;
    }

    private async Task MonitorLeaseAsync(MySqlConnection leaseConnection, string lockName,
        CancellationTokenSource execution, CancellationToken stoppingToken)
    {
        while (!execution.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), execution.Token); }
            catch (OperationCanceledException) when (execution.IsCancellationRequested) { return; }
            if (await LeaseStillOwnedAsync(leaseConnection, lockName, stoppingToken)) continue;
            execution.Cancel();
            return;
        }
    }

    private static async Task<bool> LeaseStillOwnedAsync(MySqlConnection connection, string lockName, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT IS_USED_LOCK(@Name)=CONNECTION_ID()";
            command.Parameters.AddWithValue("@Name", lockName);
            var value = await command.ExecuteScalarAsync(ct);
            return value is not null and not DBNull
                && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task<ulong> AdvanceWorkerEpochAsync(CancellationToken ct)
    {
        await using var conn = await database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await using var db = EfDb.Use(conn, tx);
        var state = await db.ProjectCopyWorkerStates
            .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
            .SingleAsync(ct);
        state.Epoch = checked(state.Epoch + 1);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return state.Epoch;
    }

    private void CleanupExecutionDirectory(ClaimedJob claim, CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var directory = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(root, "copy-jobs", claim.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), claim.Token),
            allowRoot: false);
        FileStorage.DeleteDirectoryTree(root, directory, ct);
    }

    private sealed record ClaimedJob(ulong Id, string Token, ulong WorkerEpoch);
}
