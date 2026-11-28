using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Announcements;

namespace Yf.Api.Tests;

public sealed class AnnouncementUploadTests
{
    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
    private const string PixelJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7Pj4+JS5ESUM8SDc9Pjv/" +
        "2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAABAAEDASIAAhEBAxEB/" +
        "8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/ALMAD//Z";

    [Fact]
    public async Task AcceptsActualImageWithJpegThumbnailAndRejectsMalformedOrOversizeContent()
    {
        var png = Convert.FromBase64String(PixelPng);
        var jpeg = Convert.FromBase64String(PixelJpeg);
        var ok = await Parse("IMAGE", "截图.png", png, jpeg);
        Assert.Equal("image/png", ok.ContentType);
        Assert.Equal("截图.png", ok.Name);
        await Reject("IMAGE", "截图.png", png, png);
        await Reject("IMAGE", "截图.jpg", jpeg[..^2], jpeg);
        await Reject("IMAGE", "截图.png", png[..^12], jpeg);
        await Reject("IMAGE", "../逃逸.png", png, jpeg);
        var dimension = jpeg.AsSpan().IndexOf(new byte[] { 0xff, 0xc0 });
        Assert.True(dimension > 0);
        jpeg[dimension + 7] = 2;
        await Reject("IMAGE", "截图.png", png, jpeg);
    }

    [Fact]
    public async Task RejectsRenamedZipAndExecutableAsOfficeFiles()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("notes.txt").Open());
            w.Write("notice");
        }
        await Reject("ATTACHMENT", "工令表.xlsx", buffer.ToArray(), null);
        await Reject("ATTACHMENT", "工令表.pdf", "MZ%PDF-1.5"u8.ToArray(), null);
    }

    [Theory]
    [InlineData("POST", "/API/V1/Announcements/Manage/11/Assets/", true)]
    [InlineData("POST", "/api/v1/announcements/manage/x/assets", false)]
    [InlineData("POST", "/api/v1/announcements/manage/11/assets/1", false)]
    [InlineData("PUT", "/api/v1/announcements/manage/11/assets", false)]
    public void OnlyDraftMultipartPostReceivesUploadBodyLimit(string method, string path, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        request.ContentType = "multipart/form-data; boundary=test";
        Assert.Equal(expected, AnnouncementAssetEndpoints.IsUpload(request));
    }

    private static async Task Reject(string kind, string name, byte[] content, byte[]? thumbnail)
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => Parse(kind, name, content, thumbnail));
        Assert.Equal(400, error.Status);
    }

    private static async Task<AnnouncementUpload> Parse(string kind, string name, byte[] content, byte[]? thumbnail)
    {
        using var source = new MemoryStream(content);
        using var thumb = new MemoryStream(thumbnail ?? []);
        var files = new FormFileCollection { new FormFile(source, 0, source.Length, "file", name) };
        if (thumbnail is not null) files.Add(new FormFile(thumb, 0, thumb.Length, "thumbnail", "thumb.jpg"));
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["kind"] = kind, ["uploadKey"] = Guid.NewGuid().ToString("D")
        }, files);
        return await AnnouncementUpload.ParseAsync(form, TestContext.Current.CancellationToken);
    }
}
