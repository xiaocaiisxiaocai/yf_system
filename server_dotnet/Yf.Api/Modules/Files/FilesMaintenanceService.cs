using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

public sealed class FilesMaintenanceService(
    AppDb db,
    AppOptions options,
    ILogger<FilesMaintenanceService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.WorkerEnabled) loop = Task.Run(() => RunLoopAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        if (loop is not null)
        {
            try { await loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => stopping.Dispose();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!ct.IsCancellationRequested)
        {
            try { await RunGarbageCollectionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "文件清理任务失败，将在下一周期重试"); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunGarbageCollectionAsync(CancellationToken ct)
    {
        await RecoverAbandonedMergesAsync(ct);
        await ExpireUploadsAsync(ct);
        await PurgeDeletedFilesAsync(ct);
        await PurgeDeletedMessageImagesAsync(ct);
        PurgeTemporaryArchives(ct);
        await PurgeOrphanUploadDirectoriesAsync(ct);
    }

    private async Task RecoverAbandonedMergesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        UploadSessionRow[] merging;
        await using (var context = EfDb.Use(conn))
        {
            var dbNow = await UploadService.DbNowAsync(context, ct);
            merging = (await context.UploadSessions.Where(session => session.Status == "MERGING").ToArrayAsync(ct))
                .Select(session => UploadService.ToRow(session, dbNow)).ToArray();
        }
        foreach (var candidate in merging)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(
                    conn, UploadService.MergeLockName(conn, candidate.Id), 0, ct);
                if (mergeLease is null) continue;
                var expired = false;
                await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
                {
                    await using var context = EfDb.Use(conn, tx);
                    var current = await context.UploadSessions
                        .FromSqlInterpolated($"SELECT * FROM upload_sessions WHERE id={candidate.Id} FOR UPDATE")
                        .SingleOrDefaultAsync(ct);
                    if (current is null || current.Status != "MERGING")
                    {
                        await tx.CommitAsync(ct);
                        continue;
                    }
                    var dbNow = await UploadService.DbNowAsync(context, ct);
                    var nextStatus = current.ExpiresAt <= dbNow ? "EXPIRED" : "UPLOADING";
                    var updatedAt = current.UpdatedAt >= dbNow ? current.UpdatedAt.AddSeconds(1) : dbNow;
                    var changed = await context.UploadSessions.Where(session => session.Id == current.Id && session.Status == "MERGING")
                        .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.Status, nextStatus)
                            .SetProperty(session => session.UpdatedAt, updatedAt), ct);
                    if (changed != 1)
                    {
                        await tx.CommitAsync(ct);
                        continue;
                    }
                    expired = nextStatus == "EXPIRED";
                    await tx.CommitAsync(ct);
                }
                if (expired)
                {
                    await UploadService.CleanupPendingFinalsAsync(
                        conn, options.StorageRoot, candidate.Id, logger, ct);
                    FileStorage.DeleteDirectoryTree(options.StorageRoot,
                        FileStorage.SessionDirectory(FileStorage.Root(options.StorageRoot), candidate.Id), ct);
                }
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "恢复失联文件合并失败 {SessionId}", candidate.Id);
            }
        }
    }

    private async Task ExpireUploadsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var root = FileStorage.Root(options.StorageRoot);
        ExpiredUpload[] expired;
        await using (var context = EfDb.Use(conn))
        {
            var dbNow = await UploadService.DbNowAsync(context, ct);
            expired = await context.UploadSessions.Where(session => session.Status == "UPLOADING" && session.ExpiresAt <= dbNow)
                .Select(session => new ExpiredUpload { Id = session.Id }).ToArrayAsync(ct);
        }
        foreach (var session in expired)
        {
            int claimed;
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            {
                await using var context = EfDb.Use(conn, tx);
                var dbNow = await UploadService.DbNowAsync(context, ct);
                claimed = await context.UploadSessions
                    .Where(item => item.Id == session.Id && item.Status == "UPLOADING" && item.ExpiresAt <= dbNow)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "EXPIRED")
                        .SetProperty(item => item.UpdatedAt, dbNow), ct);
                await tx.CommitAsync(ct);
            }
            if (claimed != 1) continue;
            var directory = FileStorage.SessionDirectory(root, session.Id);
            try
            {
                await UploadService.CleanupPendingFinalsAsync(conn, root, session.Id, logger, ct);
                FileStorage.DeleteDirectoryTree(root, directory, ct);
            }
            catch (Exception error) { logger.LogWarning(error, "清理过期上传目录失败 {SessionId}", session.Id); }
        }
    }

    private async Task PurgeDeletedFilesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        DeletedFile[] rows;
        await using (var context = EfDb.Use(conn))
        {
            var cutoff = (await UploadService.DbNowAsync(context, ct)).AddDays(-30);
            rows = await context.Files.Where(file => file.Status == "DELETED" && file.DeletedAt != null && file.DeletedAt < cutoff)
                .Select(file => new DeletedFile { Id = file.Id, StoragePath = file.StoragePath }).ToArrayAsync(ct);
        }
        foreach (var row in rows)
        {
            try
            {
                try
                {
                    var path = FileStorage.ResolveExistingFile(options.StorageRoot,
                        Path.Combine(options.StorageRoot, row.StoragePath), ct);
                    File.Delete(path);
                }
                catch (FileNotFoundException) { }
                await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                await using var context = EfDb.Use(conn, tx);
                var referenced = await context.FileCopyRefs.AnyAsync(reference =>
                    reference.SourceFileId == row.Id || reference.TargetFileId == row.Id, ct);
                if (referenced)
                    await context.Files.Where(file => file.Id == row.Id && file.Status == "DELETED")
                        .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.Status, "PURGED"), ct);
                else
                    await context.Files.Where(file => file.Id == row.Id && file.Status == "DELETED").ExecuteDeleteAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "拒绝或无法清理软删文件 {FileId}", row.Id);
            }
        }
    }

    internal async Task PurgeDeletedMessageImagesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        DateTime cutoff;
        await using (var clock = EfDb.Use(conn))
            cutoff = (await DbClock.UtcNowAsync(clock, ct)).AddDays(-30);
        ulong afterId = 0;
        while (true)
        {
            // Keyset batches bound memory and let later records progress past a damaged path.
            DeletedMessageImage[] candidates;
            await using (var scan = EfDb.Use(conn))
                candidates = await (from image in scan.MessageImages
                    join message in scan.Messages on image.MessageId equals message.Id
                    where image.Id > afterId && message.Status == "DELETED"
                        && message.DeletedAt != null && message.DeletedAt < cutoff
                    orderby image.Id
                    select new DeletedMessageImage(image.Id, image.MessageId)).Take(100).ToArrayAsync(ct);
            if (candidates.Length == 0) return;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                afterId = candidate.Id;
                try
                {
                    await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                    await using var context = EfDb.Use(conn, tx);
                    // Match the message-before-image order and recheck eligibility under locks.
                    var message = await context.Messages.FromSqlInterpolated(
                        $"SELECT * FROM messages WHERE id={candidate.MessageId} FOR UPDATE").SingleOrDefaultAsync(ct);
                    if (message is null || message.Status != "DELETED" || message.DeletedAt is null || message.DeletedAt >= cutoff)
                        continue;
                    var image = await context.MessageImages.FromSqlInterpolated(
                        $"SELECT * FROM message_images WHERE id={candidate.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
                    if (image is null || image.MessageId != message.Id) continue;
                    if (await context.MessageImages.AnyAsync(other => other.Id != image.Id && other.StoragePath == image.StoragePath, ct))
                        throw new InvalidOperationException("留言图片存储路径被其他记录引用，拒绝清理");
                    var path = MessageImageCleanupPath(options.StorageRoot, image.StoragePath, ct);
                    try { File.Delete(path); }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    // Delete disk first. A crash before commit leaves a retryable missing-file row.
                    context.MessageImages.Remove(image);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    logger.LogWarning(error, "拒绝或无法清理已删除留言图片 {ImageId}", candidate.Id);
                }
            }
        }
    }

    private static string MessageImageCleanupPath(string configuredRoot, string storagePath, CancellationToken ct)
    {
        var root = FileStorage.Root(configuredRoot);
        var imageRoot = Path.Combine(root, "message-images");
        var path = FileStorage.EnsureLexicallyWithin(imageRoot, Path.Combine(root, storagePath), allowRoot: false);
        var current = root;
        foreach (var component in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            ct.ThrowIfCancellationRequested();
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("留言图片清理路径包含符号链接，拒绝清理");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    private sealed record DeletedMessageImage(ulong Id, ulong MessageId);

    private void PurgeTemporaryArchives(CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var temp = Path.Combine(root, "tmp");
        if (!Directory.Exists(temp)) return;
        temp = FileStorage.ResolveExisting(root, temp, requireFile: false, ct);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        // Batch downloads now stream; this only removes archives staged on disk by earlier versions.
        foreach (var path in Directory.EnumerateFiles(temp, "yf_files_*.zip", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
            catch (Exception error) { logger.LogWarning(error, "清理残留批量下载文件失败"); }
        }
    }

    private async Task PurgeOrphanUploadDirectoriesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var activeStatuses = new[] { "UPLOADING", "MERGING" };
        var active = (await context.UploadSessions.Where(session => Enumerable.Contains(activeStatuses, session.Status))
            .Select(session => session.Id).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = FileStorage.Root(options.StorageRoot);
        var temp = Path.Combine(root, "tmp");
        if (!Directory.Exists(temp)) return;
        temp = FileStorage.ResolveExisting(root, temp, requireFile: false, ct);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var directory in Directory.EnumerateDirectories(temp, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if (active.Contains(info.Name) || info.LastWriteTimeUtc >= cutoff) continue;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                logger.LogWarning("跳过临时目录中的符号链接 {Directory}", info.Name);
                continue;
            }
            try
            {
                if (Guid.TryParseExact(info.Name, "D", out _))
                {
                    // The active set above is only a scan optimization. The lease and
                    // second database read are the authority, so another IIS worker
                    // cannot have its live merge directory removed from a stale snapshot.
                    await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(
                        conn, UploadService.MergeLockName(conn, info.Name), 0, ct);
                    if (mergeLease is null) continue;
                    var isNowActive = await context.UploadSessions.AnyAsync(
                        session => session.Id == info.Name && Enumerable.Contains(activeStatuses, session.Status), ct);
                    if (isNowActive) continue;
                    await UploadService.CleanupPendingFinalsAsync(conn, root, info.Name, logger, ct);
                    FileStorage.DeleteDirectoryTree(root, directory, ct);
                }
                else
                {
                    FileStorage.DeleteDirectoryTree(root, directory, ct);
                }
            }
            catch (Exception error) { logger.LogWarning(error, "清理孤儿上传目录失败 {Directory}", info.Name); }
        }
    }

    private sealed class ExpiredUpload { public string Id { get; set; } = ""; }
    private sealed class DeletedFile { public ulong Id { get; set; } public string StoragePath { get; set; } = ""; }
}
