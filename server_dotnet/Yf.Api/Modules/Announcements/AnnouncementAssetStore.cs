using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

internal sealed class AnnouncementAssetStore(IDbContextFactory<YfDbContext> factory, AppOptions options,
    ILogger<AnnouncementAssetStore> logger)
{
    internal async Task<AnnouncementAsset> ReserveAsync(AnnouncementUpload upload, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var prefix = $"announcement-assets/{now:yyyy/MM}/{Guid.NewGuid():N}";
        var asset = new AnnouncementAsset
        {
            Kind = upload.Kind, FileName = upload.Name, SizeBytes = upload.File.Length, ContentType = upload.ContentType,
            Sha256 = upload.Sha256, StoragePath = prefix + "." + upload.Extension,
            ThumbnailStoragePath = upload.Thumbnail is null ? null : prefix + ".thumb.jpg", CreatedAt = now,
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AnnouncementAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    internal async Task WriteAsync(AnnouncementAsset asset, AnnouncementUpload upload, CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        FileStorage.EnsureFreeSpace(root, checked((ulong)asset.SizeBytes + (ulong)(upload.Thumbnail?.Length ?? 0)));
        await WriteNewAsync(root, asset.StoragePath, upload.File, ct);
        if (upload.Thumbnail is not null)
            await WriteNewAsync(root, asset.ThumbnailStoragePath!, upload.Thumbnail, ct);
    }

    private static async Task WriteNewAsync(string root, string relative, IFormFile file, CancellationToken ct)
    {
        var path = FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, relative), false);
        var directory = FileStorage.CreateDirectoryWithin(root, Path.GetDirectoryName(path)!, ct);
        await using var output = new FileStream(Path.Combine(directory, Path.GetFileName(path)), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.CopyToAsync(output, ct);
        if (output.Length != file.Length) throw ApiException.BadRequest("上传长度与文件大小不一致");
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    internal IResult Open(HttpContext context, AnnouncementAsset asset, bool thumbnail, bool download, CancellationToken ct)
    {
        var relative = thumbnail ? asset.ThumbnailStoragePath : asset.StoragePath;
        if (relative is null) throw ApiException.NotFound("文件没有缩略图");
        FileStream stream;
        try
        {
            var path = FileStorage.ResolveStoredFile(options.StorageRoots, relative, ct);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { throw ApiException.NotFound("公告文件不存在"); }
        catch (DirectoryNotFoundException) { throw ApiException.NotFound("公告文件不存在"); }
        var fileName = thumbnail ? Path.ChangeExtension(asset.FileName, ".thumb.jpg") : asset.FileName;
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentDisposition = (download ? "attachment" : "inline") +
            $"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        return Results.File(stream, thumbnail ? "image/jpeg" : asset.ContentType, enableRangeProcessing: true);
    }

    internal async Task CleanupAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await db.AnnouncementAssetLinks.Where(l => l.SortOrder == null && l.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        // A recent row without a link may be an upload in progress. Bound each sweep and leave failed IO for retry.
        var grace = DateTime.UtcNow.AddHours(-1);
        var orphans = await db.AnnouncementAssets.Where(a => a.CreatedAt < grace &&
                !db.AnnouncementAssetLinks.Any(l => l.AssetId == a.Id))
            .OrderBy(a => a.Id).Select(a => a.Id).Take(256).ToArrayAsync(ct);
        foreach (var id in orphans) await TryCleanupOrphanAsync(id, ct);
    }

    internal async Task TryCleanupOrphanAsync(ulong id, CancellationToken ct)
    {
        try
        {
            await using var uow = await OemUnitOfWork.BeginAsync(factory, ct);
            // A link's FK insertion shares this parent lock. The recheck after locking protects independently copied links.
            var exists = await uow.Db.Database.SqlQuery<int>(
                $"SELECT 1 AS Value FROM announcement_assets WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (exists == 0 || await uow.Db.AnnouncementAssetLinks.AnyAsync(l => l.AssetId == id, ct)) return;
            var asset = await uow.Db.AnnouncementAssets.SingleAsync(a => a.Id == id, ct);
            foreach (var root in FileStorage.Roots(options.StorageRoots))
            {
                DeleteOrphanFile(root, asset.StoragePath, ct);
                if (asset.ThumbnailStoragePath is not null) DeleteOrphanFile(root, asset.ThumbnailStoragePath, ct);
            }
            uow.Db.AnnouncementAssets.Remove(asset);
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Announcement asset {AssetId} cleanup will be retried", id);
        }
    }

    private static void DeleteOrphanFile(string root, string relative, CancellationToken ct)
    {
        if (!relative.StartsWith("announcement-assets/", StringComparison.Ordinal))
            throw new InvalidOperationException("公告资产未在专用目录内");
        try { File.Delete(FileStorage.ResolveExistingFile(root, Path.Combine(root, relative), ct)); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
