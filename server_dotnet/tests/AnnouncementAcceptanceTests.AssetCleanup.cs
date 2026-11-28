using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Announcements;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests
{
    [Fact]
    public async Task AssetCleanupRemovesExpiredStagingButPreservesPublishedAndRecentReferences()
    {
        await w.EnsureAsync(Ct);
        const string title = "图片回收验收";
        var original = await StartRichDraft(title);
        var originalId = original.Id();
        var shared = (await PushFile(w.Author, originalId, "IMAGE", "正式图片.png", RichImage).Ok()).Id();
        await ReleaseRich(await SaveRich(original, title, RichDocument(Figure(shared, "正式图片"))));

        // A copy that omits the source image retains it temporarily for editing/undo.
        var draft = await w.Author.PostAsync(Manage, RichDraft(title + "草稿", RichDocument(Paragraph("待补图")),
            [], copyFromId: originalId), Ct).Ok();
        var draftId = draft.Id();
        var expired = (await PushFile(w.Author, draftId, "IMAGE", "过期.png", RichImage).Ok()).Id();
        var recent = (await PushFile(w.Author, draftId, "IMAGE", "编辑中.png", RichImage).Ok()).Id();
        await FileBytes(w.Author, AssetUrl(Manage, draftId, shared), RichImage, "image/png");

        await using var conn = await w.Host.OpenAsync(Ct);
        var root = w.Host.Service<AppOptions>().StorageRoot;
        var paths = await conn.QuerySingleAsync<(string Original, string Thumbnail)>(
            "SELECT storage_path, thumbnail_storage_path FROM announcement_assets WHERE id=@expired", new { expired });
        var filePath = Path.Combine(root, paths.Original);
        var thumbnailPath = Path.Combine(root, paths.Thumbnail);
        Assert.True(File.Exists(filePath));
        Assert.True(File.Exists(thumbnailPath));
        // Age every asset so that only its references, rather than the in-flight upload grace period, protect it.
        var ids = new[] { shared, expired, recent };
        await conn.ExecuteAsync("UPDATE announcement_assets SET created_at=UTC_TIMESTAMP()-INTERVAL 9 DAY WHERE id IN @ids", new { ids });
        await conn.ExecuteAsync("""
            UPDATE announcement_asset_links SET created_at=UTC_TIMESTAMP()-INTERVAL 8 DAY
            WHERE asset_id IN @oldIds
            """, new { oldIds = new[] { shared, expired } });

        var store = w.Host.Service<AnnouncementAssetStore>();
        await store.CleanupAsync(Ct);
        Assert.False(File.Exists(filePath));
        Assert.False(File.Exists(thumbnailPath));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM announcement_assets WHERE id=@expired", new { expired }));
        var survivors = (await conn.QueryAsync<(ulong Announcement, ulong Asset, int? SortOrder)>("""
            SELECT announcement_id,asset_id,sort_order FROM announcement_asset_links WHERE asset_id IN @ids
            ORDER BY announcement_id,asset_id
            """, new { ids })).ToArray();
        Assert.Equal([(originalId, shared, (int?)0), (draftId, recent, null)], survivors);
        await FileBytes(w.Vendor, AssetUrl(External, originalId, shared), RichImage, "image/png");
        await FileBytes(w.Vendor, AssetUrl(External, originalId, shared) + "?thumbnail=true", RichThumbnail, "image/jpeg");
        await FileBytes(w.Author, AssetUrl(Manage, draftId, recent), RichImage, "image/png");
        await FileBytes(w.Author, AssetUrl(Manage, draftId, recent) + "?thumbnail=true", RichThumbnail, "image/jpeg");
    }
}
