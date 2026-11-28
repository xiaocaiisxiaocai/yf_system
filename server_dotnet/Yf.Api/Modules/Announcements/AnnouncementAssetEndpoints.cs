using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

// The form is read after authorization. Describe its actual parts for API clients.
internal sealed record AnnouncementAssetUploadForm(Guid UploadKey, string Kind, IFormFile File,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IFormFile? Thumbnail);

internal static class AnnouncementAssetEndpoints
{
    internal const long RequestLimit = 33L * 1024 * 1024;

    internal static bool IsUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) ||
            request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            return false;
        if (!request.Path.StartsWithSegments("/api/v1/announcements/manage", StringComparison.OrdinalIgnoreCase, out var tail)) return false;
        var segments = tail.Value?.Trim('/').Split('/');
        return segments is [var id, var action] && action.Equals("assets", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
    }

    internal static void MapManagement(RouteGroupBuilder management)
    {
        management.MapPost("/{id:long}/assets", async (HttpContext c, ulong id, AnnouncementManagementService service,
            AnnouncementAssetStore store, FileRateLimiter limiter, CancellationToken ct) =>
        {
            var actor = AccessService.GetCurrent(c);
            await service.CheckUploadAccessAsync(actor, id, ct);
            limiter.Require(actor.Id, "announcement-asset-upload");
            if (!c.Request.HasFormContentType) throw new ApiException(415, 41501, "请使用 multipart/form-data 上传公告文件");
            IFormCollection form;
            try { form = await c.Request.ReadFormAsync(ct); }
            catch (InvalidDataException) { throw ApiException.BadRequest("公告上传表单无效或超过大小上限"); }
            var upload = await AnnouncementUpload.ParseAsync(form, ct);
            return await service.UploadAssetAsync(actor, id, upload, store, ct);
        })
        .Accepts<AnnouncementAssetUploadForm>("multipart/form-data")
        .WithMetadata(new RequestSizeLimitAttribute(RequestLimit), new RequestFormLimitsAttribute
            { MultipartBodyLengthLimit = RequestLimit, ValueLengthLimit = 1024, ValueCountLimit = 4 });

        management.MapGet("/{id:long}/assets/{assetId:long}", async (HttpContext c, ulong id, ulong assetId,
            bool? thumbnail, bool? download, AnnouncementManagementService service, AnnouncementAssetStore store, CancellationToken ct) =>
        {
            var asset = await service.ManagedAssetAsync(AccessService.GetCurrent(c), id, assetId, ct);
            return store.Open(c, asset, thumbnail == true, download == true, ct);
        }).Produces(StatusCodes.Status200OK, null, "application/octet-stream");
    }

    internal static void MapReader(RouteGroupBuilder group, Func<HttpContext, OemActor> actor)
    {
        group.MapGet("/{id:long}/assets/{assetId:long}", async (HttpContext c, ulong id, ulong assetId, bool? thumbnail,
            bool? download, [FromServices] IDbContextFactory<YfDbContext> factory, AnnouncementAssetStore store, CancellationToken ct) =>
        {
            await using var uow = await OemUnitOfWork.ReadAsync(factory, ct);
            var visible = await AnnouncementReaderService.VisibleAsync(uow, actor(c), ct);
            var asset = await (from link in uow.Db.AnnouncementAssetLinks.AsNoTracking()
                join announcement in visible on link.AnnouncementId equals announcement.Id
                where link.AnnouncementId == id && link.AssetId == assetId && link.SortOrder != null
                select link.Asset).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("公告文件不可查看");
            return store.Open(c, asset, thumbnail == true, download == true, ct);
        }).Produces(StatusCodes.Status200OK, null, "application/octet-stream");
    }
}

public sealed partial class AnnouncementManagementService
{
    internal async Task CheckUploadAccessAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Publish) throw ApiException.Forbidden();
        if (!await uow.Db.Announcements.AnyAsync(a => a.Id == id && a.Status == "DRAFT" && a.CreatedBy == actor.Id, ct))
            throw ApiException.NotFound("草稿不可编辑");
    }

    internal async Task<AnnouncementAssetResponse> UploadAssetAsync(CurrentUser actor, ulong id, AnnouncementUpload upload,
        AnnouncementAssetStore store, CancellationToken ct)
    {
        // Recording the private path before writing makes a crash during storage recoverable by the orphan sweep.
        var reserved = await store.ReserveAsync(upload, ct);
        var linked = false;
        try
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            var access = await AuthorizeAsync(uow, actor, ct);
            if (!access.Publish) throw ApiException.Forbidden();
            var draft = await LockedAsync(uow, id, ct);
            if (draft.CreatedBy != actor.Id) throw ApiException.Forbidden();
            if (draft.Status != "DRAFT") throw ApiException.Conflict("只有草稿可以上传文件");
            var existing = await uow.Db.AnnouncementAssetLinks.AsNoTracking()
                .Where(l => l.AnnouncementId == id && l.UploadKey == upload.Key)
                .Select(l => l.Asset).SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.Kind != reserved.Kind || existing.FileName != reserved.FileName || existing.Sha256 != reserved.Sha256)
                    throw ApiException.Conflict("上传标识已用于其他文件");
                return AssetResponse(existing);
            }
            // The HTTP body has already been buffered; serialize only the local copy with reclamation.
            var liveReservation = await uow.Db.Database.SqlQuery<int>(
                $"SELECT 1 AS Value FROM announcement_assets WHERE id={reserved.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (liveReservation == 0) throw ApiException.Conflict("上传已过期，请重试");
            await store.WriteAsync(reserved, upload, ct);
            uow.Db.AnnouncementAssetLinks.Add(new AnnouncementAssetLink { AnnouncementId = id, AssetId = reserved.Id,
                UploadKey = upload.Key, CreatedAt = uow.Now });
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            linked = true;
            return AssetResponse(reserved);
        }
        finally
        {
            // Check the committed link even on a disconnected/ambiguous commit; a failed check leaves a sweepable row.
            if (!linked) await store.TryCleanupOrphanAsync(reserved.Id, CancellationToken.None);
        }
    }

    internal async Task<AnnouncementAsset> ManagedAssetAsync(CurrentUser actor, ulong id, ulong assetId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        return await (from l in uow.Db.AnnouncementAssetLinks.AsNoTracking()
                      join a in access.Visible(uow.Db) on l.AnnouncementId equals a.Id
                      where a.Id == id && l.AssetId == assetId && (l.SortOrder != null || a.Status == "DRAFT")
                      select l.Asset).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("公告文件不可查看");
    }

    private static AnnouncementAssetResponse AssetResponse(AnnouncementAsset a) =>
        new(a.Id, a.Kind, a.FileName, a.ContentType, a.SizeBytes);
}
