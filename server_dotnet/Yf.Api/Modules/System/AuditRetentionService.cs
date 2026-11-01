using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Keeps the audit log to <see cref="AppOptions.AuditRetentionDays"/> days. OEM rows (see
/// <see cref="AuditScopes.IsOemRow"/>) share the table but have no setting of their own, so they are kept for at
/// least <see cref="OemMinimumRetentionDays"/> days even when the collaboration setting is lowered. Project activity
/// history is stored separately in project_activities and is not affected.
/// </summary>
public sealed class AuditRetentionService(AppDb db, AppOptions options, AuditService audit,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    internal const string Action = "AUDIT_LOG_RETENTION";
    // Small id batches keep each delete short so request-path audit writes are not blocked for long.
    internal const int BatchSize = 1000;
    internal const int OemMinimumRetentionDays = 30;

    internal async Task<long> PurgeAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var retentionDays = options.AuditRetentionDays;
        var oemRetentionDays = Math.Max(retentionDays, OemMinimumRetentionDays);
        DateTime now;
        await using (var clock = EfDb.Use(conn))
            now = await DbClock.UtcNowAsync(clock, ct);
        var cutoff = now.AddDays(-retentionDays);
        var oemCutoff = now.AddDays(-oemRetentionDays);
        long collaborationDeleted = 0, oemDeleted = 0;
        if (oemRetentionDays == retentionDays)
        {
            collaborationDeleted = await PurgeScopeAsync(conn, null, cutoff, ct);
        }
        else
        {
            collaborationDeleted = await PurgeScopeAsync(conn, AuditScopes.IsCollaborationRow, cutoff, ct);
            oemDeleted = await PurgeScopeAsync(conn, AuditScopes.IsOemRow, oemCutoff, ct);
        }
        var deleted = collaborationDeleted + oemDeleted;
        if (deleted > 0)
        {
            await audit.WriteAsync(conn, null, null, Action, "audit_log", null,
                new { deleted, retentionDays, cutoff, oemRetentionDays, oemCutoff, oemDeleted }, null, ct);
        }
        return deleted;
    }

    private static async Task<long> PurgeScopeAsync(
        MySqlConnection conn, Expression<Func<AuditLog, bool>>? scope, DateTime cutoff, CancellationToken ct)
    {
        long deleted = 0;
        while (true)
        {
            await using var context = EfDb.Use(conn);
            IQueryable<AuditLog> rows = scope is null ? context.AuditLogs : context.AuditLogs.Where(scope);
            var ids = await rows.Where(log => log.CreatedAt < cutoff)
                .OrderBy(log => log.Id).Select(log => log.Id).Take(BatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            deleted += await context.AuditLogs.Where(log => Enumerable.Contains(ids, log.Id) && log.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
            if (ids.Length < BatchSize) break;
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
