using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.IO.Compression;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Files;

public sealed class FileService(
    AppDb db,
    AppOptions options,
    AuditService audit,
    BatchDownloadLimiter limiter,
    MediaGrantService mediaGrants,
    IdentityService identity)
{
    private const ulong PreviewMaximumBytes = 50UL * 1024 * 1024;
    private const ulong BatchInputMaximumBytes = 256UL * 1024 * 1024;
    private const ulong BatchZipOverheadBytes = 2UL * 1024 * 1024;

    public async Task<object> ListAsync(HttpContext context, ulong projectId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var project = await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        await using var ef = EfDb.Use(conn);
        var (page, size, offset) = QueryValues.Page(context.Request);
        var direction = context.Request.Query["direction"].ToString();
        var keyword = context.Request.Query["keyword"].ToString().Trim();
        var targetId = QueryValues.OptionalUInt64(context.Request, "targetId");
        var query = ef.Files.Where(file => file.ProjectId == projectId && file.Status == "AVAILABLE");
        if (targetId is not null) query = query.Where(file => file.Id == targetId.Value);
        if (!string.IsNullOrEmpty(direction)) query = query.Where(file => file.Direction == direction);
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = "%" + keyword + "%";
            query = query.Where(file => EF.Functions.Like(file.OriginalName, pattern));
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(file => file.Id).Select(file => new FileListRow
        {
            Id = file.Id, ProjectId = file.ProjectId, UploaderId = file.UploaderId, Direction = file.Direction,
            OriginalName = file.OriginalName, Ext = file.Ext, SizeBytes = file.SizeBytes, MimeType = file.MimeType,
            Sha256 = file.Sha256, CreatedAt = file.CreatedAt,
            UploaderName = ef.Users.Where(user => user.Id == file.UploaderId).Select(user => user.RealName).FirstOrDefault(),
            IsCopiedReference = ef.FileCopyRefs.Any(reference => reference.TargetFileId == file.Id),
        }).Page(offset, size).ToArrayAsync(ct);
        var canDelete = await ProjectAccessService.CanDeleteFilesAsync(conn, null, actor, project.Status, ct);
        var list = rows.Select(row => new
        {
            id = row.Id, projectId = row.ProjectId, uploaderId = row.UploaderId, direction = row.Direction,
            originalName = row.OriginalName, ext = row.Ext, sizeBytes = row.SizeBytes, mimeType = row.MimeType,
            sha256 = row.Sha256, createdAt = row.CreatedAt, uploaderName = row.UploaderName,
            isCopiedReference = row.IsCopiedReference, canDelete
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
        if (inline && !IsPreviewable(row.Ext))
            throw ApiException.BadRequest("该文件类型不支持在线预览，请下载原文件查看");
        string path;
        try
        {
            path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot,
                Path.Combine(options.StorageRoot, row.StoragePath), ct);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound(); }
        var physicalSize = (ulong)new FileInfo(path).Length;
        if (inline && Math.Max(row.SizeBytes, physicalSize) > PreviewMaximumBytes)
            throw ApiException.BadRequest($"文件超过 {PreviewMaximumBytes / 1024 / 1024} MiB，不能在线预览，请下载原文件查看");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (inline)
            {
                context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(row.OriginalName)}";
                return Results.File(stream, FileStorage.MimeType("preview." + row.Ext), enableRangeProcessing: true);
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

    public async Task<object> CreateMediaSessionAsync(HttpContext context, ulong id, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        var claims = context.Items.TryGetValue(typeof(AccessClaims), out var rawClaims) && rawClaims is AccessClaims accessClaims
            ? accessClaims
            : throw ApiException.Unauthorized();
        if (claims.UserId != actor.Id) throw ApiException.Unauthorized("登录状态无效");

        await using var conn = await db.OpenAsync(ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:preview", ct);
        var row = await LoadAvailableAsync(conn, id, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
        if (!IsVideo(row.Ext)) throw ApiException.BadRequest("该文件类型不支持视频预览");

        context.Response.Cookies.Append(MediaGrantService.CookieName(id), mediaGrants.Issue(actor.Id, claims.SessionId, id),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = options.CookieSecure,
                SameSite = SameSiteMode.Strict,
                Path = MediaPath(id),
                MaxAge = TimeSpan.FromSeconds(MediaGrantService.LifetimeSeconds)
            });
        return new { url = MediaPath(id), expiresInSeconds = MediaGrantService.LifetimeSeconds };
    }

    public async Task<IResult> StreamMediaAsync(HttpContext context, ulong id, CancellationToken ct)
    {
        if (!context.Request.Cookies.TryGetValue(MediaGrantService.CookieName(id), out var token))
            throw ApiException.Unauthorized("缺少媒体预览凭证");
        var grant = mediaGrants.Parse(token);
        if (grant.FileId != id) throw ApiException.Unauthorized("媒体预览凭证与文件不匹配");

        await using var conn = await db.OpenAsync(ct);
        if (!await identity.HasActiveSessionAsync(conn, null, grant.UserId, grant.SessionId, ct))
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        var actor = await LoadMediaActorAsync(conn, grant.UserId, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:preview", ct);
        var row = await LoadAvailableAsync(conn, id, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
        var contentType = MediaMimeType(row.Ext)
            ?? throw ApiException.BadRequest("该文件类型不支持视频预览");

        string path;
        try
        {
            path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot,
                Path.Combine(options.StorageRoot, row.StoragePath), ct);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound(); }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(row.OriginalName)}";
            return Results.File(stream, contentType, enableRangeProcessing: true);
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
        await using var initialContext = EfDb.Use(conn);
        var initial = await FileRows(initialContext).SingleOrDefaultAsync(file => file.Id == id, ct) ?? throw ApiException.NotFound();
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireFileDeleteAsync(conn, tx, current, initial.ProjectId, ct);
        await using var ef = EfDb.Use(conn, tx);
        var locked = await ef.Files.FromSqlInterpolated($"SELECT * FROM files WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        var row = ToRow(locked);
        if (row.ProjectId != initial.ProjectId) throw ApiException.Conflict("文件所属项目已变化，请刷新后重试");
        if (row.Status != "AVAILABLE") throw ApiException.NotFound();
        var deletedAt = await ef.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        var changed = await ef.Files.Where(file => file.Id == id && file.Status == "AVAILABLE")
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.Status, "DELETED")
                .SetProperty(file => file.DeletedAt, deletedAt), ct);
        if (changed != 1) throw ApiException.NotFound();
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
        await using var ef = EfDb.Use(conn);
        var available = await FileRows(ef).Where(file => Enumerable.Contains(ids, file.Id)).ToDictionaryAsync(file => file.Id, ct);
        var entries = new List<ArchiveSource>(ids.Length);
        ulong inputBytes = 0;
        foreach (var id in ids)
        {
            if (!available.TryGetValue(id, out var row)) throw ApiException.NotFound();
            await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
            if (row.Status != "AVAILABLE") throw ApiException.BadRequest($"文件 {row.OriginalName} 不可用");
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
        tempDirectory = FileStorage.CreateDirectoryWithin(root, tempDirectory, ct);
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
        await using var ef = EfDb.Use(conn);
        var row = await FileRows(ef).SingleOrDefaultAsync(file => file.Id == id, ct);
        return row is { Status: "AVAILABLE" } ? row : throw ApiException.NotFound();
    }

    private static bool IsPreviewable(string extension) =>
        extension.Equals("pdf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("xls", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("pptx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("gif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("webp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("bmp", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVideo(string extension) => MediaMimeType(extension) is not null;

    internal static string? MediaMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        "ogv" => "video/ogg",
        _ => null
    };

    private static string MediaPath(ulong id) => $"/api/v1/files/{id}/media";

    private static async Task<CurrentUser> LoadMediaActorAsync(MySqlConnection conn, ulong userId, CancellationToken ct)
    {
        await using var ef = EfDb.Use(conn);
        var row = await ef.Users.Where(user => user.Id == userId).Select(user => new MediaActorRow
        {
            Id = user.Id, EmployeeNo = user.EmployeeNo, UserType = user.UserType, SupplierId = user.SupplierId,
            Status = user.Status, MustChangePassword = user.MustChangePassword,
        }).SingleOrDefaultAsync(ct);
        if (row is null || row.Status != "ACTIVE" || row.MustChangePassword)
            throw ApiException.Unauthorized("账号状态已变化，请重新登录");
        if (row.UserType == "SUPPLIER" && (row.SupplierId is not ulong supplierId
            || !await ef.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == "ACTIVE", ct)))
            throw ApiException.Unauthorized("所属供应商已被禁用");
        return new(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
    }

    private static IQueryable<FileRow> FileRows(YfDbContext context) => context.Files.AsNoTracking().Select(ToRowExpression);

    private static readonly System.Linq.Expressions.Expression<Func<FileRecord, FileRow>> ToRowExpression = file => new FileRow
    {
        Id = file.Id, ProjectId = file.ProjectId, UploaderId = file.UploaderId, Direction = file.Direction,
        OriginalName = file.OriginalName, StoredName = file.StoredName, Ext = file.Ext, SizeBytes = file.SizeBytes,
        MimeType = file.MimeType, Sha256 = file.Sha256, StoragePath = file.StoragePath, Status = file.Status,
        DeletedAt = file.DeletedAt, CreatedAt = file.CreatedAt,
    };

    private static FileRow ToRow(FileRecord file) => new()
    {
        Id = file.Id, ProjectId = file.ProjectId, UploaderId = file.UploaderId, Direction = file.Direction,
        OriginalName = file.OriginalName, StoredName = file.StoredName, Ext = file.Ext, SizeBytes = file.SizeBytes,
        MimeType = file.MimeType, Sha256 = file.Sha256, StoragePath = file.StoragePath, Status = file.Status,
        DeletedAt = file.DeletedAt, CreatedAt = file.CreatedAt,
    };

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
        public bool IsCopiedReference { get; set; }
    }

    private sealed class MediaActorRow
    {
        public ulong Id { get; set; }
        public string EmployeeNo { get; set; } = "";
        public string UserType { get; set; } = "";
        public ulong? SupplierId { get; set; }
        public string Status { get; set; } = "";
        public bool MustChangePassword { get; set; }
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
