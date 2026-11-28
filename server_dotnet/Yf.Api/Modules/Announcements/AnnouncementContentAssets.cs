using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Announcements;

internal static class AnnouncementContentAssets
{
    internal static Task<AnnouncementAssetResponse[]> ListAsync(YfDbContext db, ulong id, CancellationToken ct) =>
        db.AnnouncementAssetLinks.AsNoTracking().Where(link => link.AnnouncementId == id && link.SortOrder != null)
            .OrderBy(link => link.SortOrder).ThenBy(link => link.AssetId)
            .Select(link => new AnnouncementAssetResponse(link.AssetId, link.Asset.Kind, link.Asset.FileName,
                link.Asset.ContentType, link.Asset.SizeBytes)).ToArrayAsync(ct);

    // Called in the draft save transaction, with the destination locked and the copy source already authorized.
    internal static async Task BindAsync(YfDbContext db, ulong draftId, ulong? copyFromId,
        ulong[] imageIds, ulong[]? attachmentIds, DateTime now, CancellationToken ct)
    {
        if (copyFromId is { } source)
        {
            // Uploading may create the copy after an image was removed in the editor. Its undo history
            // can still refer to any saved asset in the source, never to the source's pending uploads.
            var ids = await db.AnnouncementAssetLinks.AsNoTracking()
                .Where(link => link.AnnouncementId == source && link.SortOrder != null)
                .Select(link => link.AssetId).ToArrayAsync(ct);
            db.AnnouncementAssetLinks.AddRange(ids.Select(assetId => new AnnouncementAssetLink
            {
                AnnouncementId = draftId, AssetId = assetId, CreatedAt = now,
            }));
            await db.SaveChangesAsync(ct);
        }

        var links = await db.AnnouncementAssetLinks.Where(link => link.AnnouncementId == draftId)
            .Select(link => new { Link = link, link.Asset.Kind }).ToDictionaryAsync(item => item.Link.AssetId, ct);
        var attachments = attachmentIds ?? links.Values.Where(item => item.Kind == "ATTACHMENT" && item.Link.SortOrder.HasValue)
            .OrderBy(item => item.Link.SortOrder).Select(item => item.Link.AssetId).ToArray();
        var orderedIds = imageIds.Concat(attachments).ToArray();
        if (orderedIds.Length > 100) throw ApiException.BadRequest("每条公告最多保存 100 个图片和附件");
        var desired = orderedIds.ToHashSet();
        if (desired.Contains(0) || desired.Count != orderedIds.Length) throw ApiException.BadRequest("公告文件引用重复或无效");
        for (var i = 0; i < orderedIds.Length; i++)
        {
            var assetId = orderedIds[i];
            var expectedKind = i < imageIds.Length ? "IMAGE" : "ATTACHMENT";
            if (links.TryGetValue(assetId, out var existing) && existing.Kind == expectedKind)
                existing.Link.SortOrder = i;
            else throw ApiException.BadRequest("图片或附件不属于当前草稿或复制来源，请重新上传");
        }
        // Let the editor undo a removal after saving. Start the normal pending-upload TTL at removal,
        // without extending older pending links on unrelated saves.
        foreach (var item in links.Values.Where(item => item.Link.SortOrder.HasValue && !desired.Contains(item.Link.AssetId)))
        {
            item.Link.SortOrder = null;
            item.Link.CreatedAt = now;
        }
    }
}
