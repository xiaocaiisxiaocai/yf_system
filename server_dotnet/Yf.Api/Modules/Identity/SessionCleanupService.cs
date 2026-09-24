using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

/// <summary>Deletes refresh-token rows only after their session family can no longer be active.</summary>
public sealed class SessionCleanupService(AppDb db, AppOptions options, ILogger<SessionCleanupService> logger) : BackgroundService
{
    // Every rotated hash must survive for the whole absolute session lifetime so replay can revoke the family.
    // Keep the rows for one more week after that family deadline for conservative cleanup.
    internal static readonly TimeSpan ReplayGrace = TimeSpan.FromDays(7);
    internal const int BatchSize = 1000;

    internal async Task<int> PurgeExpiredSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var now = await DbClock.UtcNowAsync(context, ct, 0);
        var cutoff = now - ReplayGrace;
        var deleted = 0;
        while (true)
        {
            var ids = await context.RefreshTokens.Where(token => token.SessionExpiresAt < cutoff)
                .OrderBy(token => token.Id).Select(token => token.Id).Take(BatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) return deleted;
            deleted += await context.RefreshTokens
                .Where(token => Enumerable.Contains(ids, token.Id) && token.SessionExpiresAt < cutoff)
                .ExecuteDeleteAsync(ct);
            if (ids.Length < BatchSize) return deleted;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try { await PurgeExpiredSessionsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Expired session cleanup failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
