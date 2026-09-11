using Dapper;
using MySqlConnector;
using System.IO.Compression;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Files;

public sealed class FileService(AppDb db, AppOptions options, AuditService audit, BatchDownloadLimiter limiter)
{
    private const ulong PdfPreviewMaximumBytes = 50UL * 1024 * 1024;
    private const ulong BatchInputMaximumBytes = 256UL * 1024 * 1024;
    private const ulong BatchZipOverheadBytes = 2UL * 1024 * 1024;

    public async Task<object> ListAsync(HttpContext context, ulong projectId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var (page, size, offset) = QueryValues.Page(context.Request);
        var direction = context.Request.Query["direction"].ToString();
        var keyword = context.Request.Query["keyword"].ToString().Trim();
        var targetId = ulong.TryParse(context.Request.Query["targetId"], out var parsedTarget) ? parsedTarget : (ulong?)null;
        var where = " WHERE f.project_id=@ProjectId AND f.status='AVAILABLE'";
        if (targetId is not null) where += " AND f.id=@TargetId";
        if (!string.IsNullOrEmpty(direction)) where += " AND f.direction=@Direction";
        if (!string.IsNullOrEmpty(keyword)) where += " AND LOCATE(@Keyword,f.original_name)>0";
        var args = new { ProjectId = projectId, TargetId = targetId, Direction = direction, Keyword = keyword, Offset = offset, Size = size };
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM files f" + where, args, cancellationToken: ct));
        var rows = (await conn.QueryAsync<FileListRow>(new CommandDefinition("""
            SELECT f.id AS Id,f.project_id AS ProjectId,f.uploader_id AS UploaderId,f.direction AS Direction,
                   f.original_name AS OriginalName,f.ext AS Ext,f.size_bytes AS SizeBytes,f.mime_type AS MimeType,
                   f.sha256 AS Sha256,f.created_at AS CreatedAt,u.real_name AS UploaderName
            FROM files f LEFT JOIN users u ON u.id=f.uploader_id
            """ + where + " ORDER BY f.id DESC LIMIT @Size OFFSET @Offset", args, cancellationToken: ct))).ToArray();
        var canDelete = await ProjectAccessService.CanDeleteFilesAsync(conn, null, actor, ct);
        var list = rows.Select(row => new
        {
            id = row.Id, projectId = row.ProjectId, uploaderId = row.UploaderId, direction = row.Direction,
            originalName = row.OriginalName, ext = row.Ext, sizeBytes = row.SizeBytes, mimeType = row.MimeType,
            sha256 = row.Sha256, createdAt = row.CreatedAt, uploaderName = row.UploaderName, canDelete
        });
        return new { list, total, page, pageSize = size };
    }

    public async Task<IResult> StreamAsync(HttpContext context, ulong id, bool inline, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, inline ? "file:preview" : "file:download", ct);
        var row = await LoadAvailableAsync(conn, id, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
        string path;
        try
        {
            path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot,
                Path.Combine(options.StorageRoot, row.StoragePath), ct);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound(); }
        var physicalSize = (ulong)new FileInfo(path).Length;
        if (inline && row.Ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)
            && Math.Max(row.SizeBytes, physicalSize) > PdfPreviewMaximumBytes)
            throw ApiException.BadRequest($"PDF 超过 {PdfPreviewMaximumBytes / 1024 / 1024} MiB，不能在线预览，请下载原文件查看");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (inline)
            {
                context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(row.OriginalName)}";
                return Results.File(stream, row.MimeType ?? "application/octet-stream", enableRangeProcessing: true);
            }
            await audit.WriteAsync(conn, null, actor.Id, "FILE_DOWNLOAD", "file", id,
                new { name = row.OriginalName }, ClientIp.Resolve(context, options), ct);
            return Results.File(stream, row.MimeType ?? "application/octet-stream", row.OriginalName,
                enableRangeProcessing: true);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task DeleteAsync(HttpContext context, ulong id, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var initial = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
            UploadService.FileSelect + " WHERE f.id=@Id", new { Id = id }, cancellationToken: ct))
            ?? throw ApiException.NotFound();
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireFileDeleteAsync(conn, tx, current, initial.ProjectId, ct);
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
            UploadService.FileSelect + " WHERE f.id=@Id FOR UPDATE", new { Id = id }, tx, cancellationToken: ct))
            ?? throw ApiException.NotFound();
        if (row.ProjectId != initial.ProjectId) throw ApiException.Conflict("文件所属项目已变化，请刷新后重试");
        if (row.Status != "AVAILABLE") throw ApiException.NotFound();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE files SET status='DELETED',deleted_at=UTC_TIMESTAMP(6) WHERE id=@Id AND status='AVAILABLE'
            """, new { Id = id }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "FILE_DELETE", "file", id,
            new { name = row.OriginalName }, ClientIp.Resolve(context, options), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IResult> BatchDownloadAsync(HttpContext context, BatchDownloadRequest request, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:download", ct);
        if (request.Ids is null || request.Ids.Count is < 1 or > 100)
            throw ApiException.BadRequest("批量下载数量需为 1~100");
        var ids = request.Ids.Distinct().ToArray();
        var entries = new List<ArchiveSource>(ids.Length);
        ulong inputBytes = 0;
        foreach (var id in ids)
        {
            var row = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
                UploadService.FileSelect + " WHERE f.id=@Id", new { Id = id }, cancellationToken: ct))
                ?? throw ApiException.NotFound();
            if (row.Status != "AVAILABLE") throw ApiException.BadRequest($"文件 {row.OriginalName} 不可用");
            await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
            string path;
            try
            {
                path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot,
                    Path.Combine(options.StorageRoot, row.StoragePath), ct);
            }
            catch (FileNotFoundException) { throw ApiException.NotFound(); }
            inputBytes = checked(inputBytes + (ulong)new FileInfo(path).Length);
            if (inputBytes > BatchInputMaximumBytes)
                throw ApiException.BadRequest($"批量下载文件总大小不能超过 {BatchInputMaximumBytes / 1024 / 1024} MiB");
            entries.Add(new(path, row.OriginalName));
        }

        var lease = limiter.Acquire(actor.Id, checked(inputBytes + BatchZipOverheadBytes));
        var root = FileStorage.Root(options.StorageRoot);
        var tempDirectory = FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, "tmp"), false);
        Directory.CreateDirectory(tempDirectory);
        await FileStorage.ResolveExistingAsync(root, tempDirectory, requireFile: false, ct);
        var zipName = $"yf_files_{Guid.NewGuid():D}.zip";
        var zipPath = FileStorage.EnsureLexicallyWithin(root, Path.Combine(tempDirectory, zipName), false);
        CleanupFileStream? responseStream = null;
        try
        {
            await BuildArchiveAsync(zipPath, entries, ct);
            responseStream = new CleanupFileStream(zipPath, lease);
            lease = null;
            context.Response.Headers.CacheControl = "private, no-store";
            await audit.WriteAsync(conn, null, actor.Id, "FILE_BATCH_DOWNLOAD", "file", null,
                new { ids, inputBytes }, ClientIp.Resolve(context, options), ct);
            return Results.File(responseStream, "application/zip", zipName, enableRangeProcessing: false);
        }
        catch
        {
            responseStream?.Dispose();
            TryDelete(zipPath);
            throw;
        }
        finally { lease?.Dispose(); }
    }

    private static async Task BuildArchiveAsync(string outputPath, IReadOnlyList<ArchiveSource> sources, CancellationToken ct)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong copiedTotal = 0;
        try
        {
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var source in sources)
            {
                var entryName = UniqueEntryName(Path.GetFileName(source.OriginalName), usedNames);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    copiedTotal = checked(copiedTotal + (uint)read);
                    if (copiedTotal > BatchInputMaximumBytes)
                        throw ApiException.BadRequest($"批量下载文件总大小不能超过 {BatchInputMaximumBytes / 1024 / 1024} MiB");
                    await entryStream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        catch { TryDelete(outputPath); throw; }
    }

    private static string UniqueEntryName(string original, HashSet<string> used)
    {
        var safe = string.IsNullOrEmpty(original) ? "file" : original;
        if (used.Add(safe)) return safe;
        var extension = Path.GetExtension(safe);
        var stem = Path.GetFileNameWithoutExtension(safe);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static async Task<FileRow> LoadAvailableAsync(MySqlConnection conn, ulong id, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
            UploadService.FileSelect + " WHERE f.id=@Id", new { Id = id }, cancellationToken: ct));
        return row is { Status: "AVAILABLE" } ? row : throw ApiException.NotFound();
    }

    private sealed record ArchiveSource(string Path, string OriginalName);

    private sealed class FileListRow
    {
        public ulong Id { get; set; }
        public ulong ProjectId { get; set; }
        public ulong UploaderId { get; set; }
        public string Direction { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string Ext { get; set; } = "";
        public ulong SizeBytes { get; set; }
        public string? MimeType { get; set; }
        public string? Sha256 { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? UploaderName { get; set; }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

public sealed class BatchDownloadLimiter
{
    private const int MaximumJobs = 2;
    private const ulong MaximumReservedBytes = 2UL * (256UL * 1024 * 1024 + 2UL * 1024 * 1024);
    private readonly object gate = new();
    private readonly HashSet<ulong> activeUsers = [];
    private int activeJobs;
    private ulong reservedBytes;

    internal IDisposable Acquire(ulong userId, ulong bytes)
    {
        lock (gate)
        {
            if (activeUsers.Contains(userId)) throw ApiException.Conflict("当前账号已有批量下载正在处理，请完成后重试");
            if (activeJobs >= MaximumJobs) throw ApiException.Conflict("批量下载任务繁忙，请稍后重试");
            if (bytes > MaximumReservedBytes - reservedBytes) throw ApiException.Conflict("批量下载临时空间繁忙，请稍后重试");
            activeJobs++;
            reservedBytes += bytes;
            activeUsers.Add(userId);
            return new Lease(this, userId, bytes);
        }
    }

    private void Release(ulong userId, ulong bytes)
    {
        lock (gate)
        {
            activeJobs = Math.Max(0, activeJobs - 1);
            reservedBytes = reservedBytes >= bytes ? reservedBytes - bytes : 0;
            activeUsers.Remove(userId);
        }
    }

    private sealed class Lease(BatchDownloadLimiter owner, ulong userId, ulong bytes) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(userId, bytes);
        }
    }
}

internal sealed class CleanupFileStream : FileStream
{
    private readonly string path;
    private IDisposable? lease;

    public CleanupFileStream(string path, IDisposable lease)
        : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        this.path = path;
        this.lease = lease;
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            try { File.Delete(path); } catch { }
            Interlocked.Exchange(ref lease, null)?.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally
        {
            try { File.Delete(path); } catch { }
            Interlocked.Exchange(ref lease, null)?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
