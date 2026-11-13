using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Oem.Validation;

public sealed record SignatureVerdict(bool Accepted, string DetectedType, string MimeType, string? Reason);

/// <summary>
/// Checks that a file's leading bytes agree with its extension (the client MIME type
/// is never trusted). Executables are refused under any extension. Text-based CAD
/// formats only have to be free of binary executable markers.
/// </summary>
public static class FileSignatureInspector
{
    public const int HeadLength = 8192;

    private static readonly byte[] Ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] SevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] Rar = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]; // "Rar!" 0x1A 0x07
    private static readonly byte[] ZipLocal = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] Elf = [0x7F, 0x45, 0x4C, 0x46];
    private static readonly byte[][] MachO =
    [
        [0xFE, 0xED, 0xFA, 0xCE], [0xFE, 0xED, 0xFA, 0xCF], [0xCE, 0xFA, 0xED, 0xFE], [0xCF, 0xFA, 0xED, 0xFE], [0xCA, 0xFE, 0xBA, 0xBE],
    ];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static async Task<SignatureVerdict> InspectFileAsync(string path, string extension, CancellationToken ct)
    {
        var buffer = new byte[HeadLength];
        int read;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return Inspect(buffer.AsSpan(0, read), extension);
    }

    public static SignatureVerdict Inspect(ReadOnlySpan<byte> head, string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var mime = FileStorage.MimeType("x." + ext);
        if (IsExecutable(head)) return Reject("executable", "文件内容是可执行程序，禁止传递");
        var detected = Detect(head);
        bool ok = ext switch
        {
            "pdf" => detected == "pdf",
            "zip" or "docx" or "xlsx" or "pptx" => detected == "zip",
            "doc" or "xls" or "ppt" => detected == "ole2",
            "rar" => detected == "rar",
            "7z" => detected == "7z",
            "png" => detected == "png",
            "jpg" or "jpeg" => detected == "jpeg",
            "dwg" => detected == "dwg",
            "step" or "stp" => detected == "step",
            "iges" or "igs" or "obj" => IsText(head),
            "dxf" => IsText(head) || head.StartsWith("AutoCAD Binary DXF"u8),
            "stl" => true,
            // Formats without a dedicated rule only need to be free of executable markers.
            _ => true,
        };
        return ok
            ? new SignatureVerdict(true, detected, mime, null)
            : Reject(detected, $"文件内容与扩展名 .{ext} 不符");

        SignatureVerdict Reject(string type, string reason) => new(false, type, mime, reason);
    }

    public static string Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(ZipLocal) || head.StartsWith(ZipEmpty)) return "zip";
        if (head.StartsWith(Ole2)) return "ole2";
        if (head.StartsWith(Rar)) return "rar";
        if (head.StartsWith(SevenZip)) return "7z";
        if (head.StartsWith(Png)) return "png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpeg";
        if (head.StartsWith("AC10"u8) || head.StartsWith("AC1"u8) && head.Length >= 6) return "dwg";
        // A PDF marker can appear in a ZIP entry name. A structural signature at
        // offset zero takes precedence over PDF's permissive leading-byte rule.
        if (IndexOf(head[..Math.Min(head.Length, 1024)], "%PDF-"u8) >= 0) return "pdf";
        var trimmed = TrimLeading(head);
        if (trimmed.StartsWith("ISO-10303-21"u8)) return "step";
        return IsText(head) ? "text" : "binary";
    }

    internal static bool IsExecutable(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("MZ"u8) || head.StartsWith(Elf) || TrimLeading(head).StartsWith("#!"u8)) return true;
        foreach (var magic in MachO)
            if (head.StartsWith(magic)) return true;
        return false;
    }

    private static bool IsText(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) < 0;

    private static ReadOnlySpan<byte> TrimLeading(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf8Bom)) head = head[3..];
        var i = 0;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return head[i..];
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => haystack.IndexOf(needle);
}
