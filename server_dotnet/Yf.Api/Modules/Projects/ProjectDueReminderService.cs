using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Projects;

/// <summary>Business calendar: due dates are China Standard Time dates (UTC+8, no daylight saving).</summary>
internal static class BusinessCalendar
{
    internal static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(utcNow.AddHours(8));
}

/// <summary>
/// Reminds in-progress subprojects once N days before their due date and once when overdue. What was sent is kept
/// per due date on the project row, so a scan is idempotent and a changed due date reminds again.
/// See Robot仿真协作流程调整方案 §5.2.
/// </summary>
public sealed class ProjectDueReminderService(
    AppDb db,
    AppOptions options,
    AuditService audit,
    ILogger<ProjectDueReminderService> logger) : BackgroundService
{
    internal const int BatchSize = 100;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    /// <summary>Queues every reminder currently due and returns how many subprojects were reminded.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await RunOnceAsync(conn, ct);
    }

    internal async Task<int> RunOnceAsync(MySqlConnector.MySqlConnection conn, CancellationToken ct)
    {
        var policy = await EmailNotificationPolicy.LoadAsync(conn, null, ct);
        var dueSoonEnabled = policy.Allows(EmailNotificationPolicy.ProjectDueSoonEvent, null);
        var overdueEnabled = policy.Allows(EmailNotificationPolicy.ProjectOverdueEvent, null);
        if (!dueSoonEnabled && !overdueEnabled) return 0;

        DateOnly today;
        await using (var clock = EfDb.Use(conn))
            today = BusinessCalendar.Today(await DbClock.UtcNowAsync(clock, ct, 0));
        var horizon = today.AddDays(policy.DueRemindDaysBefore);
        var reminded = 0;
        ulong afterId = 0;
        while (true)
        {
            Candidate[] candidates;
            await using (var context = EfDb.Use(conn))
            {
                candidates = await context.Projects.AsNoTracking()
                    .Where(project => project.Id > afterId
                        && project.Status == ProjectStatuses.InProgress
                        && project.ExpectedCompletionDate != null
                        && ((overdueEnabled && project.ExpectedCompletionDate < today
                                && (project.OverdueNotifiedFor == null || project.OverdueNotifiedFor != project.ExpectedCompletionDate))
                            || (dueSoonEnabled && project.ExpectedCompletionDate >= today && project.ExpectedCompletionDate <= horizon
                                && (project.DueSoonNotifiedFor == null || project.DueSoonNotifiedFor != project.ExpectedCompletionDate))))
                    .OrderBy(project => project.Id)
                    .Select(project => new Candidate(project.Id, project.ExpectedCompletionDate!.Value))
                    .Take(BatchSize)
                    .ToArrayAsync(ct);
            }
            foreach (var candidate in candidates)
            {
                if (await RemindAsync(conn, candidate, today, ct)) reminded++;
            }
            if (candidates.Length < BatchSize) return reminded;
            afterId = candidates[^1].ProjectId;
        }
    }

    private async Task<bool> RemindAsync(MySqlConnector.MySqlConnection conn, Candidate candidate, DateOnly today, CancellationToken ct)
    {
        var overdue = candidate.DueDate < today;
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        await using var context = EfDb.Use(conn, tx);
        // Compare-and-set on the row: claims the reminder for this exact due date, and skips a subproject that
        // changed status or due date since the candidate scan. updated_at stays untouched (no business change).
        var claim = context.Projects.Where(project => project.Id == candidate.ProjectId
            && project.Status == ProjectStatuses.InProgress
            && project.ExpectedCompletionDate == candidate.DueDate);
        var claimed = overdue
            ? await claim.Where(project => project.OverdueNotifiedFor == null || project.OverdueNotifiedFor != candidate.DueDate)
                .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.OverdueNotifiedFor, candidate.DueDate), ct)
            : await claim.Where(project => project.DueSoonNotifiedFor == null || project.DueSoonNotifiedFor != candidate.DueDate)
                .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.DueSoonNotifiedFor, candidate.DueDate), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        var project = await ProjectQueries.Rows(context).SingleAsync(row => row.Id == candidate.ProjectId, ct);
        var days = Math.Abs(candidate.DueDate.DayNumber - today.DayNumber);
        await ProjectNotificationService.EnqueueDueReminderAsync(
            conn, tx, project, overdue, candidate.DueDate, days, options.WebBaseUrl, audit, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        // Hourly scans keep a restarted or briefly offline site from missing a day; the per-date markers make
        // repeated scans send each reminder only once.
        using var timer = new PeriodicTimer(ScanInterval);
        do
        {
            try
            {
                var reminded = await RunOnceAsync(stoppingToken);
                if (reminded > 0) logger.LogInformation("Queued due-date reminders for {Count} subprojects.", reminded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Due-date reminder scan failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record Candidate(ulong ProjectId, DateOnly DueDate);
}
