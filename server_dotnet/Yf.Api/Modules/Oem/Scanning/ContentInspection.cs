using System.IO.Compression;
using System.Text;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Modules.Oem.Scanning;

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
        if (IndexOf(head[..Math.Min(head.Length, 1024)], "%PDF-"u8) >= 0) return "pdf";
        if (head.StartsWith(ZipLocal) || head.StartsWith(ZipEmpty)) return "zip";
        if (head.StartsWith(Ole2)) return "ole2";
        if (head.StartsWith(Rar)) return "rar";
        if (head.StartsWith(SevenZip)) return "7z";
        if (head.StartsWith(Png)) return "png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpeg";
        if (head.StartsWith("AC10"u8) || head.StartsWith("AC1"u8) && head.Length >= 6) return "dwg";
        var trimmed = TrimLeading(head);
        if (trimmed.StartsWith("ISO-10303-21"u8)) return "step";
        return IsText(head) ? "text" : "binary";
    }

    private static bool IsExecutable(ReadOnlySpan<byte> head)
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

public enum ArchiveOutcome { NotArchive, Accepted, Encrypted, LimitExceeded, Corrupt }

public sealed record ArchiveVerdict(ArchiveOutcome Outcome, string? Reason)
{
    public static readonly ArchiveVerdict NotArchive = new(ArchiveOutcome.NotArchive, null);
    public static readonly ArchiveVerdict Accepted = new(ArchiveOutcome.Accepted, null);
}

/// <summary>
/// Engine-independent archive checks run before the malware engine: encrypted archives
/// are refused outright (they cannot be scanned), and ZIP containers are bounded by
/// entry count, nesting depth, expanded size and compression ratio. RAR and 7z are
/// checked for encryption markers in their headers; their other limits are enforced
/// by the scan engine. Encrypted OOXML documents (an OLE2 wrapper) are refused too.
/// </summary>
public static class ArchiveInspector
{
    private static readonly string[] NestedArchiveExtensions = [".zip", ".rar", ".7z"];
    private static readonly byte[] SevenZipAesCoder = [0x06, 0xF1, 0x07, 0x01];
    private static readonly byte[] RarSignature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    public static async Task<ArchiveVerdict> InspectAsync(string path, string extension, ArchiveLimits limits, string workDirectory, CancellationToken ct)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var head = new byte[64];
        int read;
        await using (var stream = File.OpenRead(path)) read = await stream.ReadAtLeastAsync(head, head.Length, false, ct);
        var detected = FileSignatureInspector.Detect(head.AsSpan(0, read));
        if (detected == "ole2" && ext is "docx" or "xlsx" or "pptx")
            return new ArchiveVerdict(ArchiveOutcome.Encrypted, "加密的 Office 文档无法扫描");
        return detected switch
        {
            "zip" when ext == "zip" => await InspectZipAsync(path, limits, depth: 1, workDirectory, new Budget(limits), ct),
            // OOXML containers: encrypted entries or zip bombs are refused, nested archives are not expected.
            "zip" => await InspectZipAsync(path, limits with { MaxDepth = 1 }, depth: 1, workDirectory, new Budget(limits), ct),
            "rar" => InspectRar(path),
            "7z" => InspectSevenZip(path),
            _ => ArchiveVerdict.NotArchive,
        };
    }

    private sealed class Budget(ArchiveLimits limits)
    {
        public long Entries;
        public long ExpandedBytes;
        public ArchiveLimits Limits { get; } = limits;
    }

    private static async Task<ArchiveVerdict> InspectZipAsync(string path, ArchiveLimits limits, int depth, string workDirectory, Budget budget, CancellationToken ct)
    {
        if (depth > limits.MaxDepth) return new(ArchiveOutcome.LimitExceeded, "压缩包嵌套层级超出限制");
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { return new(ArchiveOutcome.Corrupt, "压缩包已损坏或格式无效"); }
        using (archive)
        {
            var fileLength = Math.Max(1, new FileInfo(path).Length);
            long archiveExpanded = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
                if (++budget.Entries > limits.MaxEntries) return new(ArchiveOutcome.LimitExceeded, "压缩包文件数量超出限制");
                budget.ExpandedBytes += entry.Length;
                archiveExpanded += entry.Length;
                if (budget.ExpandedBytes > limits.MaxExpandedBytes) return new(ArchiveOutcome.LimitExceeded, "压缩包解压后总量超出限制");
                if (entry.Length > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > limits.MaxRatio)
                    return new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常");
                var nested = NestedArchiveExtensions.FirstOrDefault(suffix => entry.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (nested is null) continue;
                if (depth + 1 > limits.MaxDepth) return new(ArchiveOutcome.LimitExceeded, "压缩包嵌套层级超出限制");
                var temp = Path.Combine(workDirectory, $"nested-{Guid.NewGuid():N}{nested}");
                try
                {
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        await CopyBoundedAsync(input, output, entry.Length, ct);
                    var verdict = nested switch
                    {
                        ".zip" => await InspectZipAsync(temp, limits, depth + 1, workDirectory, budget, ct),
                        ".rar" => InspectRar(temp),
                        _ => InspectSevenZip(temp),
                    };
                    if (verdict.Outcome is not (ArchiveOutcome.Accepted or ArchiveOutcome.NotArchive)) return verdict;
                }
                finally { TryDelete(temp); }
            }
            if (archiveExpanded / fileLength > limits.MaxRatio) return new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常");
        }
        return ArchiveVerdict.Accepted;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long declared, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            // Never trust the declared size of an attacker-controlled entry.
            if (total > declared) throw new InvalidDataException("zip entry longer than declared");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>RAR4/RAR5 header walk looking for header or file encryption markers.</summary>
    internal static ArchiveVerdict InspectRar(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var signature = reader.ReadBytes(7);
            if (signature.Length != 7 || !signature.AsSpan(0, 6).SequenceEqual(RarSignature))
                return new(ArchiveOutcome.Corrupt, "RAR 文件头无效");
            if (signature[6] == 0x01)
            {
                if (reader.ReadByte() != 0x00) return new(ArchiveOutcome.Corrupt, "RAR 文件头无效");
                return InspectRar5(stream, reader);
            }
            return signature[6] == 0x00 ? InspectRar4(stream, reader) : new(ArchiveOutcome.Corrupt, "RAR 文件头无效");
        }
        catch (EndOfStreamException) { return new(ArchiveOutcome.Corrupt, "RAR 文件已截断"); }
    }

    private static ArchiveVerdict InspectRar4(Stream stream, BinaryReader reader)
    {
        for (var blocks = 0; blocks < 100_000 && stream.Position + 7 <= stream.Length; blocks++)
        {
            var start = stream.Position;
            reader.ReadUInt16(); // CRC
            var type = reader.ReadByte();
            var flags = reader.ReadUInt16();
            var size = reader.ReadUInt16();
            if (size < 7) return new(ArchiveOutcome.Corrupt, "RAR 文件头无效");
            long addSize = (flags & 0x8000) != 0 ? reader.ReadUInt32() : 0;
            if (type == 0x73 && (flags & 0x0080) != 0) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
            if (type == 0x74 && (flags & 0x0004) != 0) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
            if (type == 0x7B) break; // end of archive
            stream.Position = start + size + addSize;
        }
        return ArchiveVerdict.Accepted;
    }

    private static ArchiveVerdict InspectRar5(Stream stream, BinaryReader reader)
    {
        for (var headers = 0; headers < 100_000 && stream.Position + 4 < stream.Length; headers++)
        {
            reader.ReadUInt32(); // CRC32
            var headerSize = ReadVInt(reader);
            var headerStart = stream.Position;
            var type = ReadVInt(reader);
            var flags = ReadVInt(reader);
            ulong extraSize = (flags & 0x1) != 0 ? ReadVInt(reader) : 0;
            ulong dataSize = (flags & 0x2) != 0 ? ReadVInt(reader) : 0;
            if (type == 4) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
            if (type is 2 or 3 && extraSize > 0)
            {
                var extraStart = (long)((ulong)headerStart + headerSize - extraSize);
                stream.Position = extraStart;
                while (stream.Position < (long)((ulong)headerStart + headerSize))
                {
                    var recordSize = ReadVInt(reader);
                    var recordStart = stream.Position;
                    if (ReadVInt(reader) == 1) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
                    stream.Position = recordStart + (long)recordSize;
                }
            }
            if (type == 5) break; // end of archive
            stream.Position = (long)((ulong)headerStart + headerSize + dataSize);
        }
        return ArchiveVerdict.Accepted;
    }

    private static ulong ReadVInt(BinaryReader reader)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = reader.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("RAR vint too long");
    }

    /// <summary>7z: the AES-256 coder id appearing in the (possibly encoded) header marks encrypted content or headers.</summary>
    internal static ArchiveVerdict InspectSevenZip(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 32) return new(ArchiveOutcome.Corrupt, "7z 文件已截断");
        stream.Position = 12;
        var nextOffset = reader.ReadUInt64();
        var nextSize = reader.ReadUInt64();
        if (nextSize == 0) return ArchiveVerdict.Accepted;
        if (nextOffset > (ulong)stream.Length || nextSize > 64UL * 1024 * 1024 || 32 + nextOffset + nextSize > (ulong)stream.Length)
            return new(ArchiveOutcome.Corrupt, "7z 文件头无效");
        stream.Position = 32 + (long)nextOffset;
        var header = reader.ReadBytes((int)nextSize);
        return header.AsSpan().IndexOf(SevenZipAesCoder) >= 0
            ? new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描")
            : ArchiveVerdict.Accepted;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
