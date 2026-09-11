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
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM project_workflow_cleanup_paths WHERE id=@Id", new { item.Id }, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS project_workflow_cleanup_paths", cancellationToken: ct));
    }

    internal async Task RunGarbageCollectionAsync(CancellationToken ct)
    {
        await ExpireUploadsAsync(ct);
        await PurgeDeletedFilesAsync(ct);
        await PurgeTemporaryArchivesAsync(ct);
        await PurgeOrphanUploadDirectoriesAsync(ct);
    }

    private async Task ExpireUploadsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var root = FileStorage.Root(options.StorageRoot);
        var expired = (await conn.QueryAsync<ExpiredUpload>(new CommandDefinition("""
            SELECT id AS Id FROM upload_sessions
            WHERE status='UPLOADING' AND expires_at<UTC_TIMESTAMP(6)
            """, cancellationToken: ct))).ToArray();
        foreach (var session in expired)
        {
            var claimed = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE upload_sessions SET status='EXPIRED',updated_at=UTC_TIMESTAMP(6)
                WHERE id=@Id AND status='UPLOADING' AND expires_at<UTC_TIMESTAMP(6)
                """, new { session.Id }, cancellationToken: ct));
            if (claimed != 1) continue;
            var directory = FileStorage.SessionDirectory(root, session.Id);
            try { await FileStorage.DeleteDirectoryTreeAsync(root, directory, ct); }
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
                await conn.ExecuteAsync(new CommandDefinition("DELETE FROM files WHERE id=@Id AND status='DELETED'",
                    new { row.Id }, cancellationToken: ct));
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
                await FileStorage.DeleteDirectoryTreeAsync(root, directory, ct);
            }
            catch (Exception error) { logger.LogWarning(error, "清理孤儿上传目录失败 {Directory}", info.Name); }
        }
    }

    private sealed class CleanupPath { public ulong Id { get; set; } public string Kind { get; set; } = ""; public string Value { get; set; } = ""; }
    private sealed class ExpiredUpload { public string Id { get; set; } = ""; }
    private sealed class DeletedFile { public ulong Id { get; set; } public string StoragePath { get; set; } = ""; }
}
