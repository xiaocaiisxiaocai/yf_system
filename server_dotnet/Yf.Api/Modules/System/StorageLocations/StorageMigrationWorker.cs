using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Moves a storage location online (存储路径页面配置与在线迁移方案 §5). From the start, new content goes to the new root
/// and reads fall back to the old one. The worker then:
/// <list type="number">
/// <item>COPYING: copies every old file through a temporary name, compares SHA-256 of source and copy, then renames it
/// into place, repeating passes until one finds nothing left to copy;</item>
/// <item>WAITING: keeps copying until no upload session that started in the old root is still active;</item>
/// <item>DELETING: deletes an old file only when the new root holds a byte-identical copy (re-hashed right before),
/// copying it first if it is missing; a second pass after a grace period catches late renames; files that differ or
/// stay locked are kept and counted. Only then is the old root dropped.</item>
/// </list>
/// State lives on the storage_locations row, so a restart resumes where it stopped. Every progress write is
/// conditional on the row still describing this migration; a cancel (a migration back) interrupts the pass.
/// </summary>
public sealed class StorageMigrationWorker(
    AppDb db,
    AppOptions options,
    AuditService audit,
    StorageMigrationSignal signal,
    ILogger<StorageMigrationWorker> logger,
    LogDirectorySink? logSink = null) : BackgroundService
{
    internal const string TemporarySuffix = ".yf-migrating";
    private const int BufferSize = 1024 * 1024;
    private const int MaximumPassesPerRun = 5;
    internal TimeSpan DeleteGracePeriod { get; set; } = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Storage migration cycle failed; retrying next cycle."); }
            await signal.WaitAsync(IdleInterval, stoppingToken);
        }
    }

    /// <summary>Advances the running migration once; returns false when none is running.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        StorageLocation? row;
        await using (var conn = await db.OpenAsync(stoppingToken))
        await using (var context = EfDb.Use(conn))
            row = await context.StorageLocations.AsNoTracking()
                .Where(item => item.Status == StorageStatuses.Migrating && item.PreviousPath != null)
                .OrderBy(item => item.Kind).FirstOrDefaultAsync(stoppingToken);
        if (row is null) return false;

        var run = signal.Begin(stoppingToken);
        try
        {
            var migration = new Migration(row.Kind, row.PreviousPath!, row.Path);
            if (row.Kind == StorageKinds.Log) await MoveLogsAsync(migration, run.Token);
            else await MigrateStorageAsync(migration, row.Phase ?? StoragePhases.Copying, run.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Redirected (cancelled) or the row changed; the next cycle reads the new state.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Storage migration of {Kind} failed.", row.Kind);
            await FailAsync(new Migration(row.Kind, row.PreviousPath!, row.Path), error, stoppingToken);
        }
        finally
        {
            signal.End(run);
        }
        return true;
    }

    private async Task MigrateStorageAsync(Migration migration, string phase, CancellationToken ct)
    {
        Directory.CreateDirectory(migration.Target);
        var progress = await LoadProgressAsync(migration, ct);
        if (phase == StoragePhases.Copying)
        {
            for (var pass = 0; pass < MaximumPassesPerRun; pass++)
            {
                var result = await CopyPassAsync(migration, progress, ct);
                if (result.Copied == 0 && result.Retry == 0)
                {
                    phase = StoragePhases.Waiting;
                    await SaveAsync(migration, progress, phase, ct);
                    break;
                }
            }
            if (phase == StoragePhases.Copying) return;
        }
        if (phase == StoragePhases.Waiting)
        {
            if (await ActiveOldSessionsAsync(migration, ct) > 0)
            {
                // Sessions that started in the old root keep writing there; copy what they produced so far.
                await CopyPassAsync(migration, progress, ct);
                await SaveAsync(migration, progress, phase, ct);
                return;
            }
            var result = await CopyPassAsync(migration, progress, ct);
            if (result.Copied > 0 || result.Retry > 0)
            {
                await SaveAsync(migration, progress, phase, ct);
                return;
            }
            phase = StoragePhases.Deleting;
            await SaveAsync(migration, progress, phase, ct);
        }
        if (phase == StoragePhases.Deleting)
        {
            await DeletePassAsync(migration, progress, ct);
            await Task.Delay(DeleteGracePeriod, ct);
            var remaining = await DeletePassAsync(migration, progress, ct);
            StorageTree.RemoveEmptyDirectories(migration.Source, ct);
            await CompleteAsync(migration, progress, remaining, ct);
        }
    }

    private async Task MoveLogsAsync(Migration migration, CancellationToken ct)
    {
        // The sink already writes to the new directory and has closed the old files; move whatever is left.
        if (logSink is not null && !StorageLocationBootstrap.SamePath(logSink.Directory, migration.Target))
            logSink.SwitchTo(migration.Target);
        Directory.CreateDirectory(migration.Target);
        var progress = await LoadProgressAsync(migration, ct);
        var remaining = new List<string>();
        if (Directory.Exists(migration.Source))
        {
            foreach (var file in new DirectoryInfo(migration.Source).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                progress.TotalFiles++;
                progress.TotalBytes += file.Length;
                var destination = Path.Combine(migration.Target, file.Name);
                for (var attempt = 1; File.Exists(destination); attempt++)
                    destination = Path.Combine(migration.Target,
                        $"{Path.GetFileNameWithoutExtension(file.Name)}-moved-{attempt}{file.Extension}");
                try
                {
                    var length = file.Length;
                    await CopyVerifiedAsync(file.FullName, destination, migration.Target, ct);
                    File.Delete(file.FullName);
                    progress.CopiedFiles++;
                    progress.CopiedBytes += length;
                    progress.DeletedFiles++;
                }
                catch (IOException)
                {
                    remaining.Add(file.Name);
                }
                await MaybeSaveAsync(migration, progress, StoragePhases.Copying, ct);
            }
        }
        await CompleteAsync(migration, progress, remaining, ct);
    }

    private async Task<(long Copied, long Retry)> CopyPassAsync(Migration migration, Progress progress, CancellationToken ct)
    {
        var (files, bytes) = await Task.Run(() => StoragePathPolicy.Measure(migration.Source, ct), ct);
        progress.TotalFiles = files;
        progress.TotalBytes = bytes;
        progress.CopiedFiles = 0;
        progress.CopiedBytes = 0;
        progress.SkippedFiles = 0;
        long copied = 0, retry = 0;
        foreach (var file in StorageTree.Files(migration.Source, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal)) continue;
            var destination = Destination(migration, file.FullName);
            try
            {
                var length = file.Length;
                if (File.Exists(destination))
                {
                    if (await SameContentAsync(file.FullName, destination, ct)) progress.CopiedFiles++;
                    else progress.SkippedFiles++;
                }
                else
                {
                    await CopyVerifiedAsync(file.FullName, destination, migration.Target, ct);
                    copied++;
                    progress.CopiedFiles++;
                }
                progress.CopiedBytes += length;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException error)
            {
                // In use (an active upload chunk) or changed while copying: the next pass retries it.
                retry++;
                logger.LogDebug(error, "Storage migration will retry a busy file.");
            }
            await MaybeSaveAsync(migration, progress, null, ct);
        }
        return (copied, retry);
    }

    /// <summary>Deletes every old file that has a verified identical copy; returns the relative paths kept.</summary>
    private async Task<List<string>> DeletePassAsync(Migration migration, Progress progress, CancellationToken ct)
    {
        var kept = new List<string>();
        foreach (var file in StorageTree.Files(migration.Source, ct))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(migration.Source, file.FullName);
            if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
            {
                TryDelete(file.FullName); // Our own leftovers from an interrupted copy into a cancelled target.
                continue;
            }
            var destination = Destination(migration, file.FullName);
            try
            {
                if (!File.Exists(destination)) await CopyVerifiedAsync(file.FullName, destination, migration.Target, ct);
                if (!await SameContentAsync(file.FullName, destination, ct))
                {
                    kept.Add(relative);
                    continue;
                }
                File.Delete(file.FullName);
                progress.DeletedFiles++;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException)
            {
                kept.Add(relative);
            }
            await MaybeSaveAsync(migration, progress, StoragePhases.Deleting, ct);
        }
        progress.SkippedFiles = kept.Count;
        await SaveAsync(migration, progress, StoragePhases.Deleting, ct);
        return kept;
    }

    private static string Destination(Migration migration, string sourcePath)
    {
        var relative = Path.GetRelativePath(migration.Source, sourcePath);
        return FileStorage.EnsureLexicallyWithin(migration.Target, Path.Combine(migration.Target, relative), allowRoot: false);
    }

    /// <summary>Copies through a temporary name, verifies both SHA-256 digests, then renames into place.</summary>
    private async Task CopyVerifiedAsync(string source, string destination, string targetRoot, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(destination)!;
        if (StorageLocationBootstrap.SamePath(parent, targetRoot)) Directory.CreateDirectory(targetRoot);
        else FileStorage.CreateDirectoryWithin(targetRoot, parent, ct);
        var temporary = destination + TemporarySuffix;
        TryDelete(temporary);
        try
        {
            string sourceDigest;
            long length;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                length = input.Length;
                sourceDigest = await CopyHashingAsync(input, output, ct);
                await output.FlushAsync(ct);
            }
            if (new FileInfo(source).Length != length) throw new IOException("Source changed while copying.");
            if (!sourceDigest.Equals(await DigestAsync(temporary, ct), StringComparison.Ordinal))
                throw new IOException("Copy verification failed.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<string> CopyHashingAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var started = Stopwatch.GetTimestamp();
        long written = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                // Throttle so the copy never crowds out live uploads and downloads.
                var due = TimeSpan.FromSeconds((double)written / options.StorageMigrationBytesPerSecond);
                var ahead = due - Stopwatch.GetElapsedTime(started);
                if (ahead > TimeSpan.FromMilliseconds(20)) await Task.Delay(ahead, ct);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<bool> SameContentAsync(string left, string right, CancellationToken ct) =>
        new FileInfo(left).Length == new FileInfo(right).Length
        && (await DigestAsync(left, ct)).Equals(await DigestAsync(right, ct), StringComparison.Ordinal);

    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>Upload sessions still active whose directory sits in the old root (they keep writing there).</summary>
    private async Task<int> ActiveOldSessionsAsync(Migration migration, CancellationToken ct)
    {
        var area = migration.Kind == StorageKinds.Oem ? "uploads" : "tmp";
        var directory = Path.Combine(migration.Source, area);
        if (!Directory.Exists(directory)) return 0;
        var ids = Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
            .Where(name => Guid.TryParseExact(name, "D", out _)).Select(name => name!).ToArray();
        if (ids.Length == 0) return 0;
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        return migration.Kind == StorageKinds.Oem
            ? await context.OemUploadSessions.CountAsync(session => Enumerable.Contains(ids, session.Id)
                && ((session.Status == "UPLOADING" && session.ExpiresAt > now) || session.Status == "MERGING"), ct)
            : await context.UploadSessions.CountAsync(session => Enumerable.Contains(ids, session.Id)
                && ((session.Status == "UPLOADING" && session.ExpiresAt > now) || session.Status == "MERGING"), ct);
    }

    private async Task CompleteAsync(Migration migration, Progress progress, IReadOnlyList<string> kept, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await using var context = EfDb.Use(conn, tx);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var note = kept.Count == 0 ? null
            : $"旧目录保留 {kept.Count} 个未删除的文件（内容与新目录不一致或无法删除），例如：{string.Join("、", kept.Take(5))}";
        var changed = await Current(context, migration).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.PreviousPath, (string?)null)
            .SetProperty(item => item.Status, StorageStatuses.Idle)
            .SetProperty(item => item.Phase, (string?)null)
            .SetProperty(item => item.TotalFiles, progress.TotalFiles)
            .SetProperty(item => item.TotalBytes, progress.TotalBytes)
            .SetProperty(item => item.CopiedFiles, progress.CopiedFiles)
            .SetProperty(item => item.CopiedBytes, progress.CopiedBytes)
            .SetProperty(item => item.DeletedFiles, progress.DeletedFiles)
            .SetProperty(item => item.SkippedFiles, (long)kept.Count)
            .SetProperty(item => item.LastError, note)
            .SetProperty(item => item.CompletedAt, now)
            .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed != 1) throw new OperationCanceledException();
        await audit.WriteAsync(conn, tx, null, "STORAGE_MIGRATION_COMPLETE", "storage_location", null, new
        {
            kind = migration.Kind,
            label = StorageKinds.Label(migration.Kind),
            from = migration.Source,
            to = migration.Target,
            files = progress.TotalFiles,
            bytes = progress.TotalBytes,
            deleted = progress.DeletedFiles,
            kept = kept.Count,
        }, null, ct);
        var row = await context.StorageLocations.AsNoTracking().SingleAsync(item => item.Kind == migration.Kind, ct);
        await tx.CommitAsync(ct);
        StorageLocationState.Apply(options, row);
    }

    private async Task FailAsync(Migration migration, Exception error, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var context = EfDb.Use(conn, tx);
            var now = await DbClock.UtcNowAsync(context, ct, 3);
            var message = error switch
            {
                UnauthorizedAccessException => "无权限读写存储目录，请检查 IIS 应用池账号的目录权限",
                IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "目标磁盘空间不足",
                _ => $"迁移出错（{error.GetType().Name}），请检查磁盘与权限后重试",
            };
            var changed = await Current(context, migration).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, StorageStatuses.Failed)
                .SetProperty(item => item.LastError, message)
                .SetProperty(item => item.UpdatedAt, now), ct);
            if (changed == 1)
                await audit.WriteAsync(conn, tx, null, "STORAGE_MIGRATION_FAILED", "storage_location", null,
                    new { kind = migration.Kind, label = StorageKinds.Label(migration.Kind), error = message }, null, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception secondary) when (secondary is not OperationCanceledException)
        {
            logger.LogError(secondary, "Could not record the storage migration failure.");
        }
    }

    private async Task<Progress> LoadProgressAsync(Migration migration, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var row = await Current(context, migration).AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new OperationCanceledException();
        return new Progress
        {
            TotalFiles = row.TotalFiles, TotalBytes = row.TotalBytes, CopiedFiles = row.CopiedFiles,
            CopiedBytes = row.CopiedBytes, DeletedFiles = row.DeletedFiles, SkippedFiles = row.SkippedFiles,
        };
    }

    private async Task MaybeSaveAsync(Migration migration, Progress progress, string? phase, CancellationToken ct)
    {
        if (Stopwatch.GetElapsedTime(progress.SavedAt) < ProgressInterval) return;
        await SaveAsync(migration, progress, phase, ct);
    }

    private async Task SaveAsync(Migration migration, Progress progress, string? phase, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var query = Current(context, migration);
        var changed = phase is null
            ? await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.TotalFiles, progress.TotalFiles)
                .SetProperty(item => item.TotalBytes, progress.TotalBytes)
                .SetProperty(item => item.CopiedFiles, progress.CopiedFiles)
                .SetProperty(item => item.CopiedBytes, progress.CopiedBytes)
                .SetProperty(item => item.DeletedFiles, progress.DeletedFiles)
                .SetProperty(item => item.SkippedFiles, progress.SkippedFiles)
                .SetProperty(item => item.UpdatedAt, now), ct)
            : await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Phase, phase)
                .SetProperty(item => item.TotalFiles, progress.TotalFiles)
                .SetProperty(item => item.TotalBytes, progress.TotalBytes)
                .SetProperty(item => item.CopiedFiles, progress.CopiedFiles)
                .SetProperty(item => item.CopiedBytes, progress.CopiedBytes)
                .SetProperty(item => item.DeletedFiles, progress.DeletedFiles)
                .SetProperty(item => item.SkippedFiles, progress.SkippedFiles)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed != 1) throw new OperationCanceledException();
        progress.SavedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>The row, only while it still describes this migration (a cancel swaps the paths).</summary>
    private static IQueryable<StorageLocation> Current(YfDbContext context, Migration migration) =>
        context.StorageLocations.Where(item => item.Kind == migration.Kind
            && item.Path == migration.Target && item.PreviousPath == migration.Source
            && item.Status == StorageStatuses.Migrating);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Migration(string Kind, string Source, string Target);

    private sealed class Progress
    {
        public long TotalFiles { get; set; }
        public long TotalBytes { get; set; }
        public long CopiedFiles { get; set; }
        public long CopiedBytes { get; set; }
        public long DeletedFiles { get; set; }
        public long SkippedFiles { get; set; }
        public long SavedAt { get; set; } = Stopwatch.GetTimestamp();
    }
}
