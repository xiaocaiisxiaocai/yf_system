using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

/// <summary>Deletes refresh-token rows well after they expire.</summary>
public sealed class SessionCleanupService(AppDb db, AppOptions options, ILogger<SessionCleanupService> logger) : BackgroundService
{
    // Rotated hashes are kept for a week past their expiry so a replayed token still revokes its session family.
    internal static readonly TimeSpan ReplayGrace = TimeSpan.FromDays(7);

    internal async Task<int> PurgeExpiredSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var now = await DbClock.UtcNowAsync(context, ct, 0);
        var cutoff = now - ReplayGrace;
        return await context.RefreshTokens.Where(token => token.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
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
