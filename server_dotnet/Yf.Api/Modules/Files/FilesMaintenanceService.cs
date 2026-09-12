using Dapper;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

public sealed class FilesMaintenanceService(
    AppDb db,
    AppOptions options,
    ILogger<FilesMaintenanceService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? loop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DrainMigrationCleanupAsync(cancellationToken);
        if (options.WorkerEnabled) loop = Task.Run(() => RunLoopAsync(stopping.Token), CancellationToken.None);
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

    internal async Task DrainMigrationCleanupAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM information_schema.tables
                          WHERE table_schema=DATABASE() AND table_name='project_workflow_cleanup_paths')
            """, cancellationToken: ct));
        if (!exists) return;
        var paths = (await conn.QueryAsync<CleanupPath>(new CommandDefinition("""
            SELECT id AS Id,path_kind AS Kind,path_value AS Value
            FROM project_workflow_cleanup_paths ORDER BY id
            """, cancellationToken: ct))).ToArray();
        if (paths.Length == 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DROP TABLE IF EXISTS project_workflow_cleanup_paths", cancellationToken: ct));
            return;
        }

        var root = FileStorage.Root(options.StorageRoot);
        if (!Directory.Exists(root)) throw new InvalidOperationException($"存储根目录不是目录: {root}");
        foreach (var item in paths)
        {
            var resolved = await FileStorage.ResolveForCleanupAsync(root, item.Value, ct);
            try
            {
                switch (item.Kind)
                {
                    case "FILE": File.Delete(resolved); break;
                    case "TEMP_DIR": await FileStorage.DeleteDirectoryTreeAsync(root, resolved, ct); break;
                    default: throw new InvalidOperationException($"迁移清理队列包含未知路径类型 {item.Kind}");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error)
            {
                throw new InvalidOperationException($"删除迁移遗留路径失败 {resolved}: {error.Message}", error);
            }
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM project_workflow_cleanup_paths WHERE id=@Id", new { item.Id }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
            }
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS project_workflow_cleanup_paths", cancellationToken: ct));
    }

    internal async Task RunGarbageCollectionAsync(CancellationToken ct)
    {
        await RecoverAbandonedMergesAsync(ct);
        await ExpireUploadsAsync(ct);
        await PurgeDeletedFilesAsync(ct);
        await PurgeTemporaryArchivesAsync(ct);
        await PurgeOrphanUploadDirectoriesAsync(ct);
    }

    private async Task RecoverAbandonedMergesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var merging = (await conn.QueryAsync<UploadSessionRow>(new CommandDefinition("""
            SELECT id AS Id,project_id AS ProjectId,uploader_id AS UploaderId,file_name AS FileName,
                   file_size AS FileSize,file_md5 AS FileMd5,chunk_size AS ChunkSize,total_chunks AS TotalChunks,
                   temp_dir AS TempDir,status AS Status,result_file_id AS ResultFileId,
                   expires_at AS ExpiresAt,created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM upload_sessions WHERE status='MERGING'
            """, cancellationToken: ct))).ToArray();
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
                    var current = await conn.QuerySingleOrDefaultAsync<UploadSessionRow>(new CommandDefinition("""
                        SELECT id AS Id,project_id AS ProjectId,uploader_id AS UploaderId,file_name AS FileName,
                               file_size AS FileSize,file_md5 AS FileMd5,chunk_size AS ChunkSize,total_chunks AS TotalChunks,
                               temp_dir AS TempDir,status AS Status,result_file_id AS ResultFileId,
                               expires_at AS ExpiresAt,created_at AS CreatedAt,updated_at AS UpdatedAt
                        FROM upload_sessions WHERE id=@Id FOR UPDATE
                        """, new { candidate.Id }, tx, cancellationToken: ct));
                    if (current is null || current.Status != "MERGING")
                    {
                        await tx.CommitAsync(ct);
                        continue;
                    }
                    var changed = await conn.ExecuteAsync(new CommandDefinition("""
                        UPDATE upload_sessions
                        SET status=CASE WHEN expires_at<=UTC_TIMESTAMP(6) THEN 'EXPIRED' ELSE 'UPLOADING' END,
                            updated_at=CASE WHEN updated_at>=UTC_TIMESTAMP() THEN DATE_ADD(updated_at,INTERVAL 1 SECOND) ELSE UTC_TIMESTAMP() END
                        WHERE id=@Id AND status='MERGING'
                        """, new { current.Id }, tx, cancellationToken: ct));
                    if (changed != 1)
                    {
                        await tx.CommitAsync(ct);
                        continue;
                    }
                    expired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT status='EXPIRED' FROM upload_sessions WHERE id=@Id",
                        new { current.Id }, tx, cancellationToken: ct));
                    await tx.CommitAsync(ct);
                }
                if (expired)
                {
                    await UploadService.CleanupPendingFinalsAsync(
                        conn, options.StorageRoot, candidate.Id, logger, ct);
                    await FileStorage.DeleteDirectoryTreeAsync(options.StorageRoot,
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
        var expired = (await conn.QueryAsync<ExpiredUpload>(new CommandDefinition("""
            SELECT id AS Id FROM upload_sessions
            WHERE status='UPLOADING' AND expires_at<=UTC_TIMESTAMP(6)
            """, cancellationToken: ct))).ToArray();
        foreach (var session in expired)
        {
            int claimed;
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            {
                claimed = await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE upload_sessions SET status='EXPIRED',updated_at=UTC_TIMESTAMP(6)
                    WHERE id=@Id AND status='UPLOADING' AND expires_at<=UTC_TIMESTAMP(6)
                    """, new { session.Id }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
            }
            if (claimed != 1) continue;
            var directory = FileStorage.SessionDirectory(root, session.Id);
            try
            {
                await UploadService.CleanupPendingFinalsAsync(conn, root, session.Id, logger, ct);
                await FileStorage.DeleteDirectoryTreeAsync(root, directory, ct);
            }
            catch (Exception error) { logger.LogWarning(error, "清理过期上传目录失败 {SessionId}", session.Id); }
        }
    }

    private async Task PurgeDeletedFilesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DeletedFile>(new CommandDefinition("""
            SELECT id AS Id,storage_path AS StoragePath FROM files
            WHERE status='DELETED' AND deleted_at IS NOT NULL AND deleted_at<UTC_TIMESTAMP(6)-INTERVAL 30 DAY
            """, cancellationToken: ct))).ToArray();
        foreach (var row in rows)
        {
            try
            {
                try
                {
                    var path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot,
                        Path.Combine(options.StorageRoot, row.StoragePath), ct);
                    File.Delete(path);
                }
                catch (FileNotFoundException) { }
                await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                await conn.ExecuteAsync(new CommandDefinition("DELETE FROM files WHERE id=@Id AND status='DELETED'",
                    new { row.Id }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "拒绝或无法清理软删文件 {FileId}", row.Id);
            }
        }
    }

    private async Task PurgeTemporaryArchivesAsync(CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var temp = Path.Combine(root, "tmp");
        if (!Directory.Exists(temp)) return;
        temp = await FileStorage.ResolveExistingAsync(root, temp, requireFile: false, ct);
        var cutoff = DateTime.UtcNow.AddHours(-24);
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
        var active = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT id FROM upload_sessions WHERE status IN ('UPLOADING','MERGING')", cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = FileStorage.Root(options.StorageRoot);
        var temp = Path.Combine(root, "tmp");
        if (!Directory.Exists(temp)) return;
        temp = await FileStorage.ResolveExistingAsync(root, temp, requireFile: false, ct);
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
                    var isNowActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM upload_sessions WHERE id=@Id AND status IN ('UPLOADING','MERGING'))",
                        new { Id = info.Name }, cancellationToken: ct));
                    if (isNowActive) continue;
                    await UploadService.CleanupPendingFinalsAsync(conn, root, info.Name, logger, ct);
                    await FileStorage.DeleteDirectoryTreeAsync(root, directory, ct);
                }
                else
                {
                    await FileStorage.DeleteDirectoryTreeAsync(root, directory, ct);
                }
            }
            catch (Exception error) { logger.LogWarning(error, "清理孤儿上传目录失败 {Directory}", info.Name); }
        }
    }

    private sealed class CleanupPath { public ulong Id { get; set; } public string Kind { get; set; } = ""; public string Value { get; set; } = ""; }
    private sealed class ExpiredUpload { public string Id { get; set; } = ""; }
    private sealed class DeletedFile { public ulong Id { get; set; } public string StoragePath { get; set; } = ""; }
}
