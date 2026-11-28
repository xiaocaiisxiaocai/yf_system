using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Uploads;
using Yf.Api.Modules.Oem.Validation;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Announcements;

internal sealed record AnnouncementUpload(string Key, string Kind, string Name, string Extension, string ContentType,
    string Sha256, IFormFile File, IFormFile? Thumbnail)
{
    internal static async Task<AnnouncementUpload> ParseAsync(IFormCollection form, CancellationToken ct)
    {
        if (form["uploadKey"].Count != 1 || !Guid.TryParseExact(form["uploadKey"].ToString(), "D", out var key) || key == Guid.Empty)
            throw ApiException.BadRequest("缺少有效的上传标识");
        var kind = form["kind"].ToString();
        if (kind is not ("IMAGE" or "ATTACHMENT")) throw ApiException.BadRequest("请选择图片或附件");
        var images = kind == "IMAGE";
        var originals = form.Files.GetFiles("file");
        var thumbs = form.Files.GetFiles("thumbnail");
        if (originals.Count != 1 || thumbs.Count != (images ? 1 : 0) || form.Files.Count != originals.Count + thumbs.Count)
            throw ApiException.BadRequest("每次上传一个文件，图片须同时上传缩略图");
        var file = originals[0];
        var name = OemUploadService.ValidateFileName(file.FileName);
        var ext = OemUploadService.ExtensionOf(name);
        var allowed = images ? ext is "jpeg" or "jpg" or "png" or "gif" or "webp"
            : ext is "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx";
        if (!allowed) throw ApiException.BadRequest("此公告文件格式不受支持");
        var maxMiB = images ? 10 : 32;
        if (file.Length <= 0 || file.Length > maxMiB * 1024L * 1024)
            throw ApiException.BadRequest($"公告文件为空或超过 {maxMiB} MiB");
        var thumbnail = thumbs.FirstOrDefault();
        if (thumbnail is not null)
        {
            if (thumbnail.Length is <= 0 or > 256 * 1024) throw ApiException.BadRequest("缩略图须小于等于 256 KiB");
            await InspectAsync(thumbnail, "jpg", true, true, ct);
        }
        await InspectAsync(file, ext, images, false, ct);
        var mime = ext switch
        {
            "doc" => "application/msword",
            "ppt" => "application/vnd.ms-powerpoint",
            _ => OemPreviewFormats.ContentTypeFor(ext),
        };
        await using var stream = file.OpenReadStream();
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return new(key.ToString("D"), kind, name, ext, mime, sha, file, thumbnail);
    }

    private static async Task InspectAsync(IFormFile file, string ext, bool image, bool thumbnail, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[(int)Math.Min(file.Length, FileSignatureInspector.HeadLength)];
        await stream.ReadExactlyAsync(header, ct);
        var signature = FileSignatureInspector.Inspect(header, ext);
        if (!signature.Accepted) throw ApiException.BadRequest(signature.Reason ?? "文件内容与格式不符");
        if (image)
        {
            if (!MessageService.HasExpectedSignature(ext, header)) throw ApiException.BadRequest("图片内容与格式不符");
            var (width, height) = Dimensions(stream, header, ext);
            var maxSide = thumbnail ? 480 : 32768;
            if (width <= 0 || height <= 0 || width > maxSide || height > maxSide || (long)width * height > 64_000_000)
                throw ApiException.BadRequest(thumbnail ? "缩略图须为最长边不超过 480 像素的 JPEG" : "图片尺寸无效或过大");
        }
        else if (ext is "docx" or "xlsx" or "pptx")
        {
            stream.Position = 0;
            VerifyOfficePackage(stream, ext);
        }
    }

    private static (int Width, int Height) Dimensions(Stream stream, ReadOnlySpan<byte> head, string ext)
    {
        switch (ext)
        {
            case "png" when head.Length >= 33 && head.Slice(12, 4).SequenceEqual("IHDR"u8) && HasPngEnd(stream):
                return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
            case "gif" when head.Length >= 13 && stream.Seek(-1, SeekOrigin.End) > 0 && stream.ReadByte() == 0x3b:
                return (BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));
            case "webp" when head.Length >= 30 && BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + 8L == stream.Length:
                if (head.Slice(12, 4).SequenceEqual("VP8X"u8))
                    return (1 + head[24] + (head[25] << 8) + (head[26] << 16), 1 + head[27] + (head[28] << 8) + (head[29] << 16));
                if (head.Slice(12, 4).SequenceEqual("VP8 "u8) && head.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                    return (BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3fff);
                if (head.Slice(12, 4).SequenceEqual("VP8L"u8) && head[20] == 0x2f)
                {
                    var packed = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
                    return ((int)(packed & 0x3fff) + 1, (int)((packed >> 14) & 0x3fff) + 1);
                }
                break;
            case "jpg" or "jpeg": return JpegDimensions(stream);
        }
        return (0, 0);
    }

    private static bool HasPngEnd(Stream stream)
    {
        Span<byte> end = stackalloc byte[12];
        stream.Seek(-end.Length, SeekOrigin.End);
        stream.ReadExactly(end);
        return BinaryPrimitives.ReadUInt32BigEndian(end) == 0 && end.Slice(4, 4).SequenceEqual("IEND"u8);
    }

    private static (int, int) JpegDimensions(Stream stream)
    {
        stream.Position = 2;
        Span<byte> size = stackalloc byte[5];
        var dimensions = (0, 0);
        while (stream.Position + 4 <= stream.Length && stream.ReadByte() == 0xff)
        {
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0) break;
            if (marker == 0xda) // Start of scan: require a complete file through the end marker.
            {
                stream.Position = stream.Length - 2;
                return stream.ReadByte() == 0xff && stream.ReadByte() == 0xd9 ? dimensions : (0, 0);
            }
            if (stream.Read(size[..2]) != 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(size) - 2;
            var next = stream.Position + length;
            if (length < 0 || next > stream.Length) break;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 5 || stream.Read(size) != 5) break;
                dimensions = (BinaryPrimitives.ReadUInt16BigEndian(size[3..]), BinaryPrimitives.ReadUInt16BigEndian(size[1..]));
            }
            stream.Position = next;
        }
        return (0, 0);
    }

    private static void VerifyOfficePackage(Stream stream, string extension)
    {
        var main = extension switch { "docx" => "word/document.xml", "xlsx" => "xl/workbook.xml", _ => "ppt/presentation.xml" };
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("[Content_Types].xml")?.Length > 0 && zip.GetEntry(main)?.Length > 0) return;
        }
        catch (InvalidDataException) { }
        throw ApiException.BadRequest("Office 文件损坏或内容与扩展名不符");
    }
}
