using System.Net;
using System.Net.Http.Headers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests
{
    // A 10x4 PNG with a smaller real JPEG thumbnail. Original and thumbnail bytes intentionally differ.
    private static readonly byte[] RichImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAoAAAAECAIAAAA4WjmaAAAAFElEQVR4nGP0sK9nwA2Y8MhRLA0AfxEBDuAjWrYAAAAASUVORK5CYII=");
    private static readonly byte[] RichThumbnail = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAUDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAQX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCIAWw//9k=");

    private static string RichDocument(params object[] content) => JsonSerializer.Serialize(new { type = "doc", content });
    private static object Paragraph(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };
    private static object Figure(ulong assetId, string caption, int width = 75) =>
        new { type = "image", attrs = new { assetId, caption, width } };

    private object RichDraft(string title, string? documentJson, ulong[] attachmentIds, ulong? version = null,
        ulong? copyFromId = null, string? createKey = null) => new
    {
        title, body = "", documentJson, attachmentIds, version, copyFromId, createKey,
        internalScope = "OWN", externalScope = "SELECTED", companyIds = new[] { w.Company }
    };

    private async Task<JsonNode> StartRichDraft(string title) =>
        await w.Author.PostAsync(Manage, RichDraft(title, null, []), Ct).Ok();

    private static async Task<ApiResponse> PushFile(ApiClient actor, ulong announcement, string kind,
        string name, byte[] bytes, string? key = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(kind), "kind");
        form.Add(new StringContent(key ?? Guid.NewGuid().ToString()), "uploadKey");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(kind == "IMAGE" ? "image/png" : "application/pdf");
        form.Add(file, "file", name);
        if (kind == "IMAGE")
        {
            var thumb = new ByteArrayContent(RichThumbnail);
            thumb.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(thumb, "thumbnail", "thumbnail.jpg");
        }
        using var result = await actor.Http.PostAsync($"{Manage}/{announcement}/assets", form, Ct);
        return new ApiResponse(result.StatusCode, await result.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonNode> SaveRich(JsonNode draft, string title, string document, params ulong[] attachments) =>
        await w.Author.PutAsync($"{Manage}/{draft.Id()}", RichDraft(title, document, attachments, draft.Id("version")), Ct).Ok();

    private async Task ReleaseRich(JsonNode draft) =>
        await w.Author.PostAsync($"{Manage}/{draft.Id()}/publish?version={draft.Id("version")}", null, Ct).Ok();

    private static string AssetUrl(string group, ulong announcement, ulong asset) =>
        $"{group}/{announcement}/assets/{asset}";

    private static async Task FileBytes(ApiClient client, string path, byte[] expected, string mediaType,
        bool download = false)
    {
        using var response = await client.Http.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(Ct));
        if (download) Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task RichContentDraftAndFullTextKeepBlockOrderCaptionsAndTable()
    {
        await w.EnsureAsync(Ct);
        const string title = "Rich layout";
        var draft = await StartRichDraft(title);
        var oldImage = (await PushFile(w.Author, draft.Id(), "IMAGE", "old.png", RichImage).Ok()).Id();
        var newImage = (await PushFile(w.Author, draft.Id(), "IMAGE", "new.png", RichImage).Ok()).Id();
        var cell = new { type = "tableCell", content = new[] { Paragraph("A-102") } };
        var table = new { type = "table", content = new[] { new { type = "tableRow", content = new[] { cell } } } };
        var document = RichDocument(Paragraph("旧标签"), Figure(oldImage, "右上角贴签"),
            Paragraph("新标签"), Figure(newImage, "左侧贴签", 50), table);
        var saved = await SaveRich(draft, title, document);
        var reordered = RichDocument(Paragraph("新标签"), Figure(newImage, "左侧贴签", 50), table,
            Paragraph("旧标签"), Figure(oldImage, "右上角贴签"));
        saved = await SaveRich(saved, title, reordered);

        static void AssertPublishedLayout(JsonNode response, ulong first, ulong second)
        {
            var blocks = JsonNode.Parse(response["documentJson"]!.GetValue<string>())!["content"]!.AsArray();
            Assert.Equal(new[] { "paragraph", "image", "table", "paragraph", "image" },
                blocks.Select(node => node!["type"]!.GetValue<string>()));
            Assert.Equal(first, blocks[1]!["attrs"]!.Id("assetId"));
            Assert.Equal(50, blocks[1]!["attrs"]!["width"]!.GetValue<int>());
            Assert.Equal("左侧贴签", blocks[1]!["attrs"]!["caption"]!.GetValue<string>());
            Assert.Equal(second, blocks[4]!["attrs"]!.Id("assetId"));
            Assert.Equal("右上角贴签", blocks[4]!["attrs"]!["caption"]!.GetValue<string>());
            Assert.Equal("A-102", blocks[2]!["content"]![0]!["content"]![0]!["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        }

        AssertPublishedLayout(await w.Author.GetAsync($"{Manage}/{draft.Id()}", Ct).Ok(), newImage, oldImage);
        await ReleaseRich(saved);
        foreach (var (reader, route) in new[] { (w.SectionReader, Internal), (w.Vendor, External) })
        {
            AssertPublishedLayout(await reader.GetAsync($"{route}/{draft.Id()}", Ct).Ok(), newImage, oldImage);
            var item = Assert.Single((await List(reader, route, title))["list"]!.AsArray())!;
            Assert.Equal(2, item["imageCount"]!.GetValue<int>());
            Assert.Equal(newImage, item.Id("firstImageId"));
            Assert.Contains("新标签", item["preview"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task RichContentTextImageAndAttachmentOnlyAnnouncementsExposeSuitableSummaries()
    {
        await w.EnsureAsync(Ct);
        const string keyword = "Rich summary";
        var text = await Publish(w.Author, keyword + " text", companies: [w.Company]);
        var imageDraft = await StartRichDraft(keyword + " image");
        var picture = (await PushFile(w.Author, imageDraft.Id(), "IMAGE", "label.png", RichImage).Ok()).Id();
        await ReleaseRich(await SaveRich(imageDraft, keyword + " image", RichDocument(Figure(picture, ""))));

        var files = await StartRichDraft(keyword + " attachments");
        var sheet = OemTestHost.Pdf("packing list");
        var guide = OemTestHost.Pdf("guide");
        var packingId = (await PushFile(w.Author, files.Id(), "ATTACHMENT", "packing.pdf", sheet).Ok()).Id();
        var guideId = (await PushFile(w.Author, files.Id(), "ATTACHMENT", "guide.pdf", guide).Ok()).Id();
        var saved = await SaveRich(files, keyword + " attachments", RichDocument(new { type = "paragraph" }), guideId, packingId);
        await ReleaseRich(saved);

        var rows = (await List(w.Vendor, External, keyword))["list"]!.AsArray().ToDictionary(row => row!.Id(), row => row!);
        Assert.Equal(0, rows[text]["attachmentCount"]!.GetValue<int>());
        Assert.Equal(0, rows[text]["imageCount"]!.GetValue<int>());
        Assert.Contains("Notice:", rows[text]["preview"]!.GetValue<string>());
        Assert.Equal(picture, rows[imageDraft.Id()].Id("firstImageId"));
        Assert.Equal(1, rows[imageDraft.Id()]["imageCount"]!.GetValue<int>());
        Assert.Equal("guide.pdf", rows[files.Id()]["firstAttachmentName"]!.GetValue<string>());
        Assert.Equal(2, rows[files.Id()]["attachmentCount"]!.GetValue<int>());
        var detail = await w.Vendor.GetAsync($"{External}/{files.Id()}", Ct).Ok();
        Assert.Equal(new[] { guideId, packingId }, detail["assets"]!.AsArray().Select(a => a!.Id()));
        await FileBytes(w.Vendor, AssetUrl(External, files.Id(), guideId), guide, "application/pdf");
        await FileBytes(w.Vendor, AssetUrl(External, files.Id(), packingId) + "?download=true", sheet, "application/pdf", true);
        await FileBytes(w.Vendor, AssetUrl(External, imageDraft.Id(), picture) + "?thumbnail=true", RichThumbnail, "image/jpeg");
        Assert.Empty(await Reads(w.Author, files.Id()));
        Assert.Empty(await Reads(w.Author, imageDraft.Id()));
    }

    [Fact]
    public async Task RichContentRetryAndCopyKeepFilesReadableAfterOriginalWithdrawal()
    {
        await w.EnsureAsync(Ct);
        const string title = "Rich copy";
        var createKey = Guid.NewGuid().ToString();
        var firstRequest = RichDraft(title, null, [], createKey: createKey);
        var draft = await w.Author.PostAsync(Manage, firstRequest, Ct).Ok();
        var image = (await PushFile(w.Author, draft.Id(), "IMAGE", "label.png", RichImage).Ok()).Id();
        var key = Guid.NewGuid().ToString();
        var form = OemTestHost.Pdf("new packing checklist");
        // Send part of the second file, then cancel the actual HTTP request before the closing boundary.
        // Backpressure ensures the client consumed a chunk rather than cancelling before the request started.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 16 * 1024, resumeWriterThreshold: 8192));
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        using (var partial = new MultipartFormDataContent())
        {
            partial.Add(new StringContent("ATTACHMENT"), "kind");
            partial.Add(new StringContent(key), "uploadKey");
            partial.Add(new StreamContent(pipe.Reader.AsStream()), "file", "checklist.pdf");
            var request = w.Author.Http.PostAsync($"{Manage}/{draft.Id()}/assets", partial, cancelled.Token);
            try
            {
                await pipe.Writer.WriteAsync(OemTestHost.Pdf(new string('a', 32 * 1024)), Ct)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    using var response = await request.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                });
            }
            finally
            {
                await cancelled.CancelAsync();
                await pipe.Writer.CompleteAsync();
            }
        }
        await FileBytes(w.Author, AssetUrl(Manage, draft.Id(), image), RichImage, "image/png");
        var invalid = await PushFile(w.Author, draft.Id(), "ATTACHMENT", "checklist.pdf", [], key);
        Assert.InRange((int)invalid.Status, 400, 499);
        var attachment = (await PushFile(w.Author, draft.Id(), "ATTACHMENT", "checklist.pdf", form, key).Ok()).Id();
        Assert.Equal(attachment, (await PushFile(w.Author, draft.Id(), "ATTACHMENT", "checklist.pdf", form, key).Ok()).Id());
        await PushFile(w.Author, draft.Id(), "ATTACHMENT", "checklist.pdf", OemTestHost.Pdf("different"), key)
            .Status(HttpStatusCode.Conflict);
        var doc = RichDocument(Paragraph("请按图贴标"), Figure(image, "标签样例"));
        var original = await SaveRich(draft, title, doc, attachment);
        var replay = await w.Author.PostAsync(Manage, firstRequest, Ct).Ok();
        Assert.Equal(original.Id(), replay.Id());
        Assert.Equal(original["documentJson"]!.GetValue<string>(), replay["documentJson"]!.GetValue<string>());
        Assert.Equal(original.Id("version"), replay.Id("version"));
        await ReleaseRich(original);
        await w.Vendor.GetAsync($"{External}/{draft.Id()}", Ct).Ok();
        await w.Vendor.PostAsync($"{External}/{draft.Id()}/read", null, Ct).Ok();
        var copy = await w.Author.PostAsync(Manage, RichDraft(title + " reissue", doc, [attachment], copyFromId: draft.Id()), Ct).Ok();
        Assert.NotEqual(draft.Id(), copy.Id());
        await ReleaseRich(copy);
        await w.Author.PostAsync($"{Manage}/{draft.Id()}/withdraw", null, Ct).Ok();
        await FileBytes(w.Vendor, AssetUrl(External, copy.Id(), image), RichImage, "image/png");
        await FileBytes(w.Vendor, AssetUrl(External, copy.Id(), image) + "?thumbnail=true", RichThumbnail, "image/jpeg");
        await FileBytes(w.Vendor, AssetUrl(External, copy.Id(), attachment) + "?download=true", form, "application/pdf", true);
        Assert.Single(await Reads(w.Author, draft.Id()));
        Assert.Empty(await Reads(w.Author, copy.Id()));
        await Denied(w.Vendor.GetAsync(AssetUrl(External, draft.Id(), image), Ct));
        await Denied(w.Vendor.GetAsync(AssetUrl(External, draft.Id(), attachment) + "?download=true", Ct));
    }

    [Fact]
    public async Task RichContentAssetsFollowDraftPublicationAndWithdrawalAccess()
    {
        await w.EnsureAsync(Ct);
        var draft = await StartRichDraft("Rich protected files");
        var image = (await PushFile(w.Author, draft.Id(), "IMAGE", "design.png", RichImage).Ok()).Id();
        var pdf = OemTestHost.Pdf("department document");
        var attachment = (await PushFile(w.Author, draft.Id(), "ATTACHMENT", "plan.pdf", pdf).Ok()).Id();
        var notIncluded = (await PushFile(w.Author, draft.Id(), "ATTACHMENT", "unused.pdf", pdf).Ok()).Id();
        var foreignDraft = await StartRichDraft("Rich unrelated draft");
        var invalid = await w.Author.PutAsync($"{Manage}/{foreignDraft.Id()}",
            RichDraft("Rich unrelated draft", RichDocument(Figure(image, "图")), [attachment], foreignDraft.Id("version")), Ct);
        Assert.InRange((int)invalid.Status, 400, 499);
        await FileBytes(w.Author, AssetUrl(Manage, draft.Id(), image), RichImage, "image/png");
        var paths = new[] { (image, ""), (image, "?thumbnail=true"), (attachment, "?download=true") };
        foreach (var (assetId, suffix) in paths)
            await Denied(w.Vendor.GetAsync(AssetUrl(External, draft.Id(), assetId) + suffix, Ct));
        await ReleaseRich(await SaveRich(draft, "Rich protected files", RichDocument(Figure(image, "图")), attachment));
        await Denied(w.Vendor.GetAsync(AssetUrl(External, draft.Id(), notIncluded), Ct));
        await Denied(w.Author.GetAsync(AssetUrl(Manage, draft.Id(), notIncluded), Ct));
        await FileBytes(w.SectionReader, AssetUrl(Internal, draft.Id(), image) + "?thumbnail=true", RichThumbnail, "image/jpeg");
        foreach (var (assetId, suffix) in paths)
        {
            await Denied(w.OtherVendor.GetAsync(AssetUrl(External, draft.Id(), assetId) + suffix, Ct));
            await Denied(w.OtherStaff.GetAsync(AssetUrl(Internal, draft.Id(), assetId) + suffix, Ct));
        }
        Assert.Empty(await Reads(w.Author, draft.Id()));
        await w.Author.PostAsync($"{Manage}/{draft.Id()}/withdraw", null, Ct).Ok();
        foreach (var (assetId, suffix) in paths)
        {
            await Denied(w.Vendor.GetAsync(AssetUrl(External, draft.Id(), assetId) + suffix, Ct));
            await Denied(w.SectionReader.GetAsync(AssetUrl(Internal, draft.Id(), assetId) + suffix, Ct));
        }
        await FileBytes(w.Author, AssetUrl(Manage, draft.Id(), attachment), pdf, "application/pdf");
    }
}
