using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.IO.Compression;
using System.IO.Pipelines;
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
    DownloadGrantService downloadGrants,
    IdentityService identity)
{
    private const ulong PreviewMaximumBytes = 50UL * 1024 * 1024;
    private const ulong BatchInputMaximumBytes = 256UL * 1024 * 1024;

    public async Task<PageResponse<FileListItem>> ListAsync(HttpContext context, ulong projectId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var project = await ProjectAccessService.RequireViewAsync(conn, tx, actor, projectId, ct);
        await using var ef = EfDb.Use(conn, tx);
        var (page, size, offset) = QueryValues.Page(context.Request);
        var direction = context.Request.Query["direction"].ToString();
        var keyword = context.Request.Query["keyword"].ToString().Trim();
        var targetId = QueryValues.OptionalUInt64(context.Request, "targetId");
        var query = ef.Files.Where(file => file.ProjectId == projectId && file.Status == FileStatuses.Available);
        if (targetId is not null) query = query.Where(file => file.Id == targetId.Value);
        if (!string.IsNullOrEmpty(direction)) query = query.Where(file => file.Direction == direction);
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = QueryValues.ContainsPattern(keyword);
            query = query.Where(file => EF.Functions.Like(file.OriginalName, pattern, QueryValues.LikeEscape));
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
        var canDelete = await ProjectAccessService.CanDeleteFilesAsync(conn, tx, actor, project.Status, ct);
        var list = rows.Select(row => new FileListItem(
            row.Id, row.ProjectId, row.UploaderId, row.Direction, row.OriginalName, row.Ext, row.SizeBytes,
            row.MimeType, row.Sha256, row.CreatedAt, row.UploaderName, row.IsCopiedReference, canDelete)).ToArray();
        await tx.CommitAsync(ct);
        return new(list, total, page, size);
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
            path = FileStorage.ResolveExistingFile(options.StorageRoot,
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
                await WriteWindowedAuditAsync(context, conn, actor.Id, "FILE_PREVIEW", id,
                    new { name = row.OriginalName }, ct);
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

    public async Task<DownloadGrantResponse> CreateDownloadGrantAsync(HttpContext context, ulong id, CancellationToken ct)
    {
        RequireSameOrigin(context.Request);
        var actor = AccessService.GetCurrent(context);
        var claims = RequireMatchingClaims(context, actor);
        await using var conn = await db.OpenAsync(ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:download", ct);
        var row = await LoadAvailableAsync(conn, id, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
        _ = ResolveExisting(row, ct);

        var issue = downloadGrants.Issue(actor.Id, claims.SessionId, [id], batch: false);
        var path = NativeDownloadPath(id, issue.Handle);
        AppendGrantCookie(context, issue, path);
        SetNoStore(context.Response);
        return new(path, issue.ExpiresInSeconds);
    }

    public async Task<IResult> StreamNativeAsync(HttpContext context, ulong id, string handle, CancellationToken ct)
    {
        RequireSameOrigin(context.Request);
        var path = NativeDownloadPath(id, handle);
        var session = ResolveDownloadSession(context, handle, path);
        if (session.Batch || session.FileIds.Count != 1 || session.FileIds[0] != id)
            throw ApiException.Unauthorized("下载会话与文件不匹配");

        await using var conn = await db.OpenAsync(ct);
        var actor = await RequireActiveDownloadActorAsync(conn, session, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:download", ct);
        var row = await LoadAvailableAsync(conn, id, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, row.ProjectId, ct);
        var filePath = ResolveExisting(row, ct);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            await WriteSessionAuditAsync(context, conn, session, "FILE_DOWNLOAD", id,
                new { name = row.OriginalName }, ct);
            SetNoStore(context.Response);
            return Results.File(stream, row.MimeType ?? "application/octet-stream", row.OriginalName,
                enableRangeProcessing: true);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task<DownloadGrantResponse> CreateBatchDownloadGrantAsync(
        HttpContext context, BatchDownloadRequest request, CancellationToken ct)
    {
        RequireSameOrigin(context.Request);
        var actor = AccessService.GetCurrent(context);
        var claims = RequireMatchingClaims(context, actor);
        await using var conn = await db.OpenAsync(ct);
        var batch = await PrepareBatchAsync(conn, actor, request, ct);
        var issue = downloadGrants.Issue(actor.Id, claims.SessionId, batch.Ids, batch: true);
        var path = NativeBatchDownloadPath(issue.Handle);
        AppendGrantCookie(context, issue, path);
        SetNoStore(context.Response);
        return new(path, issue.ExpiresInSeconds);
    }

    public async Task<IResult> BatchDownloadNativeAsync(HttpContext context, string handle, CancellationToken ct)
    {
        RequireSameOrigin(context.Request);
        var path = NativeBatchDownloadPath(handle);
        var session = ResolveDownloadSession(context, handle, path);
        if (!session.Batch) throw ApiException.Unauthorized("下载会话与批量文件不匹配");

        await using var conn = await db.OpenAsync(ct);
        var actor = await RequireActiveDownloadActorAsync(conn, session, ct);
        var batch = await PrepareBatchAsync(conn, actor, new BatchDownloadRequest(session.FileIds), ct);
        var lease = limiter.Acquire(actor.Id);
        try
        {
            await WriteSessionAuditAsync(context, conn, session, "FILE_BATCH_DOWNLOAD", null,
                new { ids = batch.Ids, inputBytes = batch.InputBytes }, ct);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        SetNoStore(context.Response);
        return new ZipStreamResult(batch.Entries, $"yf_files_{Guid.NewGuid():D}.zip", lease);
    }

    public async Task<MediaSessionResponse> CreateMediaSessionAsync(HttpContext context, ulong id, CancellationToken ct)
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
        return new(MediaPath(id), MediaGrantService.LifetimeSeconds);
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
            path = FileStorage.ResolveExistingFile(options.StorageRoot,
                Path.Combine(options.StorageRoot, row.StoragePath), ct);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound(); }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            await WriteWindowedAuditAsync(context, conn, actor.Id, "FILE_PREVIEW", id,
                new { name = row.OriginalName }, ct, $"media:{actor.Id}:{grant.SessionId}:{id}");
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
        if (row.Status != FileStatuses.Available) throw ApiException.NotFound();
        var deletedAt = await DbClock.UtcNowAsync(ef, ct);
        var changed = await ef.Files.Where(file => file.Id == id && file.Status == FileStatuses.Available)
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.Status, FileStatuses.Deleted)
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
        var batch = await PrepareBatchAsync(conn, actor, request, ct);
        var lease = limiter.Acquire(actor.Id);
        try
        {
            await audit.WriteAsync(conn, null, actor.Id, "FILE_BATCH_DOWNLOAD", "file", null,
                new { ids = batch.Ids, inputBytes = batch.InputBytes }, ClientIp.Resolve(context, options), ct);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        context.Response.Headers.CacheControl = "private, no-store";
        // Stream the archive as it is compressed instead of staging up to 256 MiB on disk first, so the
        // download starts immediately. The limiter lease is held until the stream completes.
        return new ZipStreamResult(batch.Entries, $"yf_files_{Guid.NewGuid():D}.zip", lease);
    }

    /// <summary>
    /// ZipArchive on .NET 8 writes synchronously, which Kestrel forbids on the response body. The archive
    /// is written into a pipe on a worker thread while the request copies the pipe to the client
    /// asynchronously; pipe back-pressure bounds memory use.
    /// </summary>
    internal sealed class ZipStreamResult(IReadOnlyList<ArchiveSource> sources, string fileName, IDisposable lease) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using var _ = lease;
            var ct = httpContext.RequestAborted;
            httpContext.Response.ContentType = "application/zip";
            httpContext.Response.Headers.ContentDisposition =
                new System.Net.Mime.ContentDisposition { FileName = fileName, DispositionType = "attachment" }.ToString();
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024));
            var producer = Task.Run(async () =>
            {
                try
                {
                    await using (var output = pipe.Writer.AsStream(leaveOpen: true))
                        await WriteArchiveAsync(output, sources, ct);
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception error)
                {
                    await pipe.Writer.CompleteAsync(error);
                    throw;
                }
            }, CancellationToken.None);
            try
            {
                await pipe.Reader.CopyToAsync(httpContext.Response.Body, ct);
                await pipe.Reader.CompleteAsync();
            }
            catch (Exception error)
            {
                await pipe.Reader.CompleteAsync(error);
                // Never let a truncated archive look like a finished download.
                httpContext.Abort();
                try { await producer; } catch { }
                if (error is OperationCanceledException && ct.IsCancellationRequested) return;
                throw;
            }
            await producer;
        }
    }

    private static async Task WriteArchiveAsync(Stream output, IReadOnlyList<ArchiveSource> sources, CancellationToken ct)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong copiedTotal = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var source in sources)
        {
            var entryName = UniqueEntryName(Path.GetFileName(source.OriginalName), usedNames);
            var entry = archive.CreateEntry(entryName, CompressionLevelFor(entryName));
            await using var entryStream = entry.Open();
            await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                copiedTotal = checked(copiedTotal + (uint)read);
                if (copiedTotal > BatchInputMaximumBytes)
                    throw new InvalidOperationException("Batch download input grew beyond its validated size.");
                await entryStream.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }

    private static CompressionLevel CompressionLevelFor(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".7z" or ".avi" or ".bmp" or ".docx" or ".gif" or ".gz" or ".jpeg" or ".jpg"
            or ".m4a" or ".m4v" or ".mkv" or ".mov" or ".mp3" or ".mp4" or ".ogg" or ".ogv" or ".pdf"
            or ".png" or ".pptx" or ".rar" or ".webm" or ".webp" or ".xlsx" or ".xlsm" or ".zip"
            ? CompressionLevel.NoCompression
            : CompressionLevel.Fastest;
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

    private async Task<BatchPreparation> PrepareBatchAsync(
        MySqlConnection conn, CurrentUser actor, BatchDownloadRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count is < 1 or > 100)
            throw ApiException.BadRequest("批量下载数量需为 1~100");
        var ids = request.Ids.Distinct().ToArray();
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "file:download", ct);
        await using var ef = EfDb.Use(conn, tx);
        var available = await FileRows(ef).Where(file => Enumerable.Contains(ids, file.Id))
            .ToDictionaryAsync(file => file.Id, ct);
        var entries = new List<ArchiveSource>(ids.Length);
        ulong inputBytes = 0;
        var viewableProjects = new HashSet<ulong>();
        foreach (var id in ids)
        {
            if (!available.TryGetValue(id, out var row)) throw ApiException.NotFound();
            if (viewableProjects.Add(row.ProjectId))
                await ProjectAccessService.RequireViewAsync(conn, tx, actor, row.ProjectId, ct);
            if (row.Status != FileStatuses.Available) throw ApiException.BadRequest($"文件 {row.OriginalName} 不可用");
            var path = ResolveExisting(row, ct);
            inputBytes = checked(inputBytes + (ulong)new FileInfo(path).Length);
            if (inputBytes > BatchInputMaximumBytes)
                throw ApiException.BadRequest($"批量下载文件总大小不能超过 {BatchInputMaximumBytes / 1024 / 1024} MiB");
            entries.Add(new(path, row.OriginalName));
        }
        await tx.CommitAsync(ct);
        return new(ids, entries, inputBytes);
    }

    private async Task<CurrentUser> RequireActiveDownloadActorAsync(
        MySqlConnection conn, DownloadSession session, CancellationToken ct)
    {
        if (!await identity.HasActiveSessionAsync(conn, null, session.UserId, session.AuthSessionId, ct))
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        return await LoadMediaActorAsync(conn, session.UserId, ct);
    }

    private DownloadSession ResolveDownloadSession(HttpContext context, string handle, string path)
    {
        if (!DownloadGrantService.IsValidHandle(handle))
            throw ApiException.Unauthorized("下载会话无效或已过期，请重新发起下载");
        if (context.Request.Cookies.TryGetValue(DownloadGrantService.SessionCookieName(handle), out var sessionSecret))
            return downloadGrants.GetSession(handle, sessionSecret);
        if (!context.Request.Cookies.TryGetValue(DownloadGrantService.GrantCookieName(handle), out var grantSecret))
            throw ApiException.Unauthorized("缺少下载凭证，请重新发起下载");

        var session = downloadGrants.Redeem(handle, grantSecret);
        context.Response.Cookies.Delete(DownloadGrantService.GrantCookieName(handle), new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = path,
        });
        context.Response.Cookies.Append(DownloadGrantService.SessionCookieName(handle), session.Secret, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = path,
            MaxAge = TimeSpan.FromSeconds(DownloadGrantService.SessionLifetimeSeconds),
        });
        return session;
    }

    private void AppendGrantCookie(HttpContext context, DownloadGrantIssue issue, string path) =>
        context.Response.Cookies.Append(DownloadGrantService.GrantCookieName(issue.Handle), issue.Secret, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = path,
            MaxAge = TimeSpan.FromSeconds(issue.ExpiresInSeconds),
        });

    private async Task WriteSessionAuditAsync(
        HttpContext context, MySqlConnection conn, DownloadSession session, string action, ulong? targetId,
        object detail, CancellationToken ct)
    {
        using var lease = await downloadGrants.AcquireSessionAuditAsync(session.Handle, ct);
        if (!lease.ShouldWrite) return;
        await audit.WriteAsync(conn, null, session.UserId, action, "file", targetId, detail,
            ClientIp.Resolve(context, options), ct);
        lease.Complete();
    }

    private async Task WriteWindowedAuditAsync(
        HttpContext context, MySqlConnection conn, ulong actorId, string action, ulong targetId,
        object detail, CancellationToken ct, string? auditKey = null)
    {
        var authSessionId = context.Items.TryGetValue(typeof(AccessClaims), out var raw)
            && raw is AccessClaims claims ? claims.SessionId : context.TraceIdentifier;
        var key = auditKey ?? $"{action}:{actorId}:{authSessionId}:{targetId}";
        using var lease = await downloadGrants.AcquireWindowAuditAsync(key, ct);
        if (!lease.ShouldWrite) return;
        await audit.WriteAsync(conn, null, actorId, action, "file", targetId, detail,
            ClientIp.Resolve(context, options), ct);
        lease.Complete();
    }

    private static AccessClaims RequireMatchingClaims(HttpContext context, CurrentUser actor)
    {
        var claims = context.Items.TryGetValue(typeof(AccessClaims), out var raw) && raw is AccessClaims value
            ? value : throw ApiException.Unauthorized();
        return claims.UserId == actor.Id ? claims : throw ApiException.Unauthorized("登录状态无效");
    }

    private void RequireSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase)
            || fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase))
            throw ApiException.Forbidden("下载请求必须来自当前站点");
        var originText = request.Headers.Origin.ToString();
        if (!IdentityModule.OriginAllowed(originText, options.WebBaseUrl))
            throw ApiException.Forbidden("下载请求必须来自当前站点");
    }

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private string ResolveExisting(FileRow row, CancellationToken ct)
    {
        try
        {
            return FileStorage.ResolveExistingFile(options.StorageRoot,
                Path.Combine(options.StorageRoot, row.StoragePath), ct);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound(); }
    }

    private static string NativeDownloadPath(ulong id, string handle) =>
        $"/api/v1/files/{id}/native-download/{handle}";

    private static string NativeBatchDownloadPath(string handle) =>
        $"/api/v1/files/batch-download/{handle}";

    private static async Task<FileRow> LoadAvailableAsync(MySqlConnection conn, ulong id, CancellationToken ct)
    {
        await using var ef = EfDb.Use(conn);
        var row = await FileRows(ef).SingleOrDefaultAsync(file => file.Id == id, ct);
        return row is { Status: FileStatuses.Available } ? row : throw ApiException.NotFound();
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
        if (row is null || row.Status != AccountStatuses.Active || row.MustChangePassword)
            throw ApiException.Unauthorized("账号状态已变化，请重新登录");
        if (row.UserType == UserTypes.Supplier && (row.SupplierId is not ulong supplierId
            || !await ef.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == AccountStatuses.Active, ct)))
            throw ApiException.Unauthorized("所属供应商已被禁用");
        return new(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
    }

    private static IQueryable<FileRow> FileRows(YfDbContext context) =>
        context.Files.AsNoTracking().Select(FileRowMapping.Projection);

    private static FileRow ToRow(FileRecord file) => FileRowMapping.Map(file);

    internal sealed record ArchiveSource(string Path, string OriginalName);
    private sealed record BatchPreparation(ulong[] Ids, IReadOnlyList<ArchiveSource> Entries, ulong InputBytes);

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

}

public sealed class BatchDownloadLimiter
{
    private const int MaximumJobs = 2;
    private readonly object gate = new();
    private readonly HashSet<ulong> activeUsers = [];
    private int activeJobs;

    internal IDisposable Acquire(ulong userId)
    {
        lock (gate)
        {
            if (activeUsers.Contains(userId)) throw ApiException.Conflict("当前账号已有批量下载正在处理，请完成后重试");
            if (activeJobs >= MaximumJobs) throw ApiException.Conflict("批量下载任务繁忙，请稍后重试");
            activeJobs++;
            activeUsers.Add(userId);
            return new Lease(this, userId);
        }
    }

    private void Release(ulong userId)
    {
        lock (gate)
        {
            activeJobs = Math.Max(0, activeJobs - 1);
            activeUsers.Remove(userId);
        }
    }

    private sealed class Lease(BatchDownloadLimiter owner, ulong userId) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(userId);
        }
    }
}
