using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Removes expired operational state that is no longer needed for recovery. Business history,
/// including messages, read receipts, project activities, status logs and copy records, is retained.
/// </summary>
public sealed class OperationalRetentionService(
    AppDb db,
    AppOptions options,
    ILogger<OperationalRetentionService> logger) : BackgroundService
{
    internal const int BatchSize = 500;
    internal async Task<long> PurgeUploadSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        DateTime cutoff;
        await using (var clock = EfDb.Use(conn))
            cutoff = (await DbClock.UtcNowAsync(clock, ct, 3)).AddDays(-options.UploadSessionRetentionDays);

        long deleted = 0;
        DateTime? cursorUpdatedAt = null;
        string? cursorId = null;
        var storageRoot = FileStorage.Root(options.StorageRoot);
        while (true)
        {
            var candidates = await ReadCandidatesAsync(
                conn, cutoff, cursorUpdatedAt, cursorId, ct);
            if (candidates.Length == 0) break;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var expectedDirectory = FileStorage.SessionDirectory(storageRoot, candidate.Id);
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!Path.GetFullPath(candidate.TempDir).Equals(expectedDirectory, comparison))
                        throw new InvalidOperationException("Upload session temp_dir does not match its canonical session directory.");
                    FileStorage.DeleteDirectoryTree(storageRoot, expectedDirectory, ct);
                    await using var context = EfDb.Use(conn);
                    deleted += await context.UploadSessions.Where(session => session.Id == candidate.Id
                            && (session.Status == "COMPLETED" || session.Status == "ABORTED" || session.Status == "EXPIRED")
                            && session.UpdatedAt < cutoff)
                        .ExecuteDeleteAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Keep the row when its directory cannot be safely removed so a later cycle can retry.
                    logger.LogWarning(error, "Unable to purge terminal upload session {SessionId}; retaining its row.", candidate.Id);
                }
            }

            cursorUpdatedAt = candidates[^1].UpdatedAt;
            cursorId = candidates[^1].Id;

            if (candidates.Length < BatchSize) break;
        }

        if (deleted > 0)
            logger.LogInformation("Purged {Count} terminal upload sessions older than {Days} days.",
                deleted, options.UploadSessionRetentionDays);
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await PurgeUploadSessionsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Operational-state retention failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static async Task<UploadSessionCandidate[]> ReadCandidatesAsync(
        MySqlConnection conn,
        DateTime cutoff,
        DateTime? cursorUpdatedAt,
        string? cursorId,
        CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = $"""
            SELECT id,temp_dir,updated_at
            FROM upload_sessions
            WHERE status IN ('COMPLETED','ABORTED','EXPIRED') AND updated_at < @cutoff
              AND (@hasCursor = 0 OR updated_at > @cursorUpdatedAt
                   OR (updated_at = @cursorUpdatedAt AND id > @cursorId))
            ORDER BY updated_at,id
            LIMIT {BatchSize}
            """;
        command.Parameters.AddWithValue("@cutoff", cutoff);
        command.Parameters.AddWithValue("@hasCursor", cursorUpdatedAt is not null);
        command.Parameters.AddWithValue("@cursorUpdatedAt", cursorUpdatedAt ?? cutoff);
        command.Parameters.AddWithValue("@cursorId", cursorId ?? "");
        var candidates = new List<UploadSessionCandidate>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            candidates.Add(new UploadSessionCandidate
            {
                Id = reader.GetString(0),
                TempDir = reader.GetString(1),
                UpdatedAt = reader.GetDateTime(2),
            });
        return candidates.ToArray();
    }

    private sealed class UploadSessionCandidate
    {
        public string Id { get; init; } = "";
        public string TempDir { get; init; } = "";
        public DateTime UpdatedAt { get; init; }
    }
}
