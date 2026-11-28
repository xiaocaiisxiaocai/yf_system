using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests
{
    [Fact]
    public async Task RichCopyKeepsUnselectedImagesAvailableForUndoUntilPublish()
    {
        await w.EnsureAsync(Ct);
        var original = await StartRichDraft("Undo source");
        var figure = (await PushFile(w.Author, original.Id(), "IMAGE", "figure.png", RichImage).Ok()).Id();
        var omitted = (await PushFile(w.Author, original.Id(), "IMAGE", "omitted.png", RichImage).Ok()).Id();
        var pending = (await PushFile(w.Author, original.Id(), "IMAGE", "pending.png", RichImage).Ok()).Id();
        await SaveRich(original, "Undo source", RichDocument(Figure(figure, "A"), Figure(omitted, "B")));

        // Start copying, remove both images in the editor, and attach a PDF. The latter first causes
        // the editor to create the destination draft with the already-edited (text-only) payload.
        var text = RichDocument(Paragraph("保留文字"));
        const string title = "Undo destination";
        var copy = await w.Author.PostAsync(Manage, RichDraft(title, text, [], copyFromId: original.Id()), Ct).Ok();
        var id = copy.Id();
        Assert.Empty(copy["assets"]!.AsArray());
        var pdf = (await PushFile(w.Author, id, "ATTACHMENT", "extra.pdf", OemTestHost.Pdf("extra")).Ok()).Id();
        await FileBytes(w.Author, AssetUrl(Manage, id, figure), RichImage, "image/png");
        await Denied(w.Author.GetAsync(AssetUrl(Manage, id, pending), Ct));
        await Denied(w.Vendor.GetAsync(AssetUrl(External, id, figure), Ct));

        // Undo before saving, then remove from an already saved draft and undo once more.
        var restored = RichDocument(Paragraph("保留文字"), Figure(figure, "A"));
        copy = await SaveRich(copy, title, restored, pdf);
        Assert.Equal(new[] { figure, pdf }, copy["assets"]!.AsArray().Select(asset => asset!.Id()));
        copy = await SaveRich(copy, title, text, pdf);
        Assert.Equal(new[] { pdf }, copy["assets"]!.AsArray().Select(asset => asset!.Id()));
        await FileBytes(w.Author, AssetUrl(Manage, id, figure), RichImage, "image/png");
        copy = await SaveRich(copy, title, restored, pdf);
        await ReleaseRich(copy);

        await FileBytes(w.Vendor, AssetUrl(External, id, figure), RichImage, "image/png");
        await Denied(w.Vendor.GetAsync(AssetUrl(External, id, omitted), Ct));
        await Denied(w.Author.GetAsync(AssetUrl(Manage, id, omitted), Ct));
        await using var connection = await w.Host.OpenAsync(Ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM announcement_asset_links WHERE announcement_id=@id AND sort_order IS NULL", new { id }));
        await FileBytes(w.Author, AssetUrl(Manage, original.Id(), omitted), RichImage, "image/png");
    }
}
