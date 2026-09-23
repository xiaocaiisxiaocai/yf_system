using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Keeps the audit log to <see cref="AppOptions.AuditRetentionDays"/> days. Project activity history is
/// stored separately in project_activities and is not affected.
/// </summary>
public sealed class AuditRetentionService(AppDb db, AppOptions options, AuditService audit,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    internal const string Action = "AUDIT_LOG_RETENTION";
    // Small id batches keep each delete short so request-path audit writes are not blocked for long.
    internal const int BatchSize = 1000;

    internal async Task<long> PurgeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        DateTime cutoff;
        await using (var clock = EfDb.Use(conn))
            cutoff = (await DbClock.UtcNowAsync(clock, ct))
                .AddDays(-options.AuditRetentionDays);
        long deleted = 0;
        while (true)
        {
            await using var context = EfDb.Use(conn);
            var ids = await context.AuditLogs.Where(log => log.CreatedAt < cutoff)
                .OrderBy(log => log.Id).Select(log => log.Id).Take(BatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            deleted += await context.AuditLogs.Where(log => Enumerable.Contains(ids, log.Id) && log.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
            if (ids.Length < BatchSize) break;
        }
        if (deleted > 0)
        {
            await audit.WriteAsync(conn, null, null, Action, "audit_log", null,
                new { deleted, retentionDays = options.AuditRetentionDays, cutoff }, null, ct);
        }
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await PurgeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Audit log retention failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
