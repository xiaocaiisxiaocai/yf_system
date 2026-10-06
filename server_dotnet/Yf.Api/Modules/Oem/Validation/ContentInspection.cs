using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Policies;

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
    internal static ReadOnlySpan<byte> SevenZipSignature => SevenZip;
    internal static ReadOnlySpan<byte> RarSignature => Rar;
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

public enum ArchiveOutcome { NotArchive, Accepted, Encrypted, LimitExceeded, Corrupt, Forbidden }

public sealed record ArchiveVerdict(ArchiveOutcome Outcome, string? Reason)
{
    public static readonly ArchiveVerdict NotArchive = new(ArchiveOutcome.NotArchive, null);
    public static readonly ArchiveVerdict Accepted = new(ArchiveOutcome.Accepted, null);
}

/// <summary>
/// The validation work directory (not the inspected content) failed, for example because
/// the volume is full. It is an environment problem and must stay retryable.
/// </summary>
public sealed class ArchiveWorkspaceException(Exception inner) : Exception("OEM validation work directory failed.", inner)
{
    internal static bool IsWorkspaceFault(Exception error) => error is IOException or UnauthorizedAccessException;
}

/// <summary>
/// Archive structure checks reject encrypted content and bound ZIP, RAR and 7z by
/// entry count, nesting depth, expanded size and compression ratio. Entries
/// are fully decompressed through bounded streams so corrupt payloads and false sizes
/// fail closed. Encrypted OOXML documents (an OLE2 wrapper) are refused too.
/// </summary>
public static class ArchiveInspector
{
    public static async Task<ArchiveVerdict> InspectAsync(string path, string extension, ArchiveLimits limits, string workDirectory, CancellationToken ct)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var head = new byte[64];
        int read;
        await using (var stream = File.OpenRead(path)) read = await stream.ReadAtLeastAsync(head, head.Length, false, ct);
        var detected = FileSignatureInspector.Detect(head.AsSpan(0, read));
        if (detected == "ole2" && ext is "docx" or "xlsx" or "pptx")
            return new ArchiveVerdict(ArchiveOutcome.Encrypted, "不允许传递加密的 Office 文档");
        var ooxml = OoxmlPackage.For(ext);
        return detected switch
        {
            "zip" when ext == "zip" => await InspectZipAsync(path, limits, depth: 1, workDirectory, new Budget(limits), null, ct),
            // OOXML containers: encrypted entries or zip bombs are refused, nested archives are not expected.
            "zip" => await InspectZipAsync(path, limits with { MaxDepth = 1 }, depth: 1, workDirectory, new Budget(limits), ooxml, ct),
            "rar" or "7z" => await InspectSharpArchiveAsync(path, detected, limits, depth: 1, workDirectory, new Budget(limits), ct),
            _ => await ContainsEmbeddedArchiveAsync(path, ct)
                ? new ArchiveVerdict(ArchiveOutcome.Forbidden, "文件中夹带了压缩包数据，禁止传递")
                : ArchiveVerdict.NotArchive,
        };
    }

    private const int EmbeddedSignatureScanBytes = 1024 * 1024;
    private const int ZipEndRecordLength = 22;
    private const int MaxZipCommentLength = ushort.MaxValue;

    /// <summary>
    /// Archive readers do not need the archive at offset zero: ZIP is located from its
    /// end record, and RAR/7z tools find a signature after a stub. A file that is not an
    /// archive by its leading bytes must therefore not carry a complete ZIP end record at its
    /// tail or a RAR/7z signature near its start; otherwise encryption and expansion limits
    /// could be bypassed by prepending a few bytes.
    /// </summary>
    private static async Task<bool> ContainsEmbeddedArchiveAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (ZipDirectory.FindEnd(stream, exactTail: true) is not null) return true;
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[(int)Math.Min(stream.Length, EmbeddedSignatureScanBytes)];
        await stream.ReadExactlyAsync(head, ct);
        return head.Length > 1 && (head.AsSpan(1).IndexOf(FileSignatureInspector.RarSignature) >= 0
            || head.AsSpan(1).IndexOf(FileSignatureInspector.SevenZipSignature) >= 0);
    }

    /// <summary>Unsafe names would let an extracting client write outside its target directory.</summary>
    internal static bool UnsafeEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains('\0')) return true;
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/')) return true;
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') return true;
        return normalized.Split('/').Any(segment => segment == "..");
    }

    private static readonly ArchiveVerdict UnsafeEntry = new(ArchiveOutcome.Forbidden, "压缩包条目路径不安全（包含 ..、绝对路径或盘符）");

    /// <summary>
    /// Minimal ZIP central-directory reader used before <see cref="ZipFile"/> opens a file:
    /// <c>ZipArchive</c> materialises every directory record before any limit applies, so a
    /// small file declaring millions of records could exhaust memory.
    /// </summary>
    internal static class ZipDirectory
    {
        public sealed record End(long EndOffset, long DirectoryOffset, long DirectorySize);

        public static End? FindEnd(Stream stream, bool exactTail)
        {
            var length = stream.Length;
            if (length < ZipEndRecordLength) return null;
            var tailLength = (int)Math.Min(length, ZipEndRecordLength + MaxZipCommentLength);
            var tail = new byte[tailLength];
            stream.Seek(length - tailLength, SeekOrigin.Begin);
            stream.ReadExactly(tail);
            for (var i = tailLength - ZipEndRecordLength; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054B50) continue;
                var commentEnd = i + ZipEndRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
                if (exactTail ? commentEnd != tailLength : commentEnd > tailLength) continue;
                var endOffset = length - tailLength + i;
                long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
                var zip64 = size == uint.MaxValue || offset == uint.MaxValue
                    || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10)) == ushort.MaxValue;
                if (zip64 && !TryReadZip64(stream, endOffset, ref offset, ref size)) continue;
                if (offset < 0 || size < 0 || offset > endOffset - size) continue;
                return new End(endOffset, offset, size);
            }
            return null;
        }

        private static bool TryReadZip64(Stream stream, long endOffset, ref long offset, ref long size)
        {
            if (endOffset < 20 + 56) return false;
            var locator = new byte[20];
            stream.Seek(endOffset - 20, SeekOrigin.Begin);
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064B50) return false;
            var recordOffset = BinaryPrimitives.ReadInt64LittleEndian(locator.AsSpan(8));
            if (recordOffset < 0 || recordOffset > endOffset - 20 - 56) return false;
            var record = new byte[56];
            stream.Seek(recordOffset, SeekOrigin.Begin);
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064B50) return false;
            size = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(40));
            offset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(48));
            return true;
        }

        /// <summary>Counts central-directory records, stopping after <paramref name="limit"/>; null when the directory is malformed.</summary>
        public static long? CountRecords(string path, long limit)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var end = FindEnd(stream, exactTail: false);
            if (end is null) return null;
            stream.Seek(end.DirectoryOffset, SeekOrigin.Begin);
            var header = new byte[46];
            long consumed = 0, count = 0;
            while (consumed < end.DirectorySize)
            {
                if (end.DirectorySize - consumed < header.Length) return null;
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50) return null;
                long variable = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                consumed += header.Length + variable;
                if (consumed > end.DirectorySize) return null;
                stream.Seek(variable, SeekOrigin.Current);
                if (++count > limit) return count;
            }
            return count;
        }
    }

    private sealed class Budget(ArchiveLimits limits)
    {
        public long Entries;
        public long DeclaredExpandedBytes;
        public long ActualExpandedBytes;
        public ArchiveLimits Limits { get; } = limits;
    }

    private sealed class ArchiveLimitException(string reason) : Exception(reason);

    private sealed record OoxmlPackage(
        string Extension,
        string MainPartPath,
        string MainContentType,
        string MainRootName,
        string MainRootNamespace)
    {
        public static OoxmlPackage? For(string extension) => extension switch
        {
            "docx" => new(extension, "word/document.xml",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                "document", "http://schemas.openxmlformats.org/wordprocessingml/2006/main"),
            "xlsx" => new(extension, "xl/workbook.xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            "pptx" => new(extension, "ppt/presentation.xml",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                "presentation", "http://schemas.openxmlformats.org/presentationml/2006/main"),
            _ => null,
        };
    }

    private static async Task<ArchiveVerdict> InspectZipAsync(
        string path,
        ArchiveLimits limits,
        int depth,
        string workDirectory,
        Budget budget,
        OoxmlPackage? ooxml,
        CancellationToken ct)
    {
        if (depth > limits.MaxDepth) return new(ArchiveOutcome.LimitExceeded, "压缩包嵌套层级超出限制");
        var remaining = Math.Max(0, limits.MaxEntries - budget.Entries);
        var records = ZipDirectory.CountRecords(path, remaining);
        if (records is null) return new(ArchiveOutcome.Corrupt, "压缩包目录已损坏或格式无效");
        if (records > remaining) return new(ArchiveOutcome.LimitExceeded, "压缩包文件数量超出限制");
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { return new(ArchiveOutcome.Corrupt, "压缩包已损坏或格式无效"); }
        using (archive)
        {
            var fileLength = Math.Max(1, new FileInfo(path).Length);
            long archiveExpanded = 0;
            var contentTypesSeen = false;
            var mainPartSeen = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");
                if (UnsafeEntryName(entry.FullName)) return UnsafeEntry;
                var metadata = AccountMetadata(budget, entry.Length, entry.CompressedLength);
                if (metadata is not null) return metadata;
                archiveExpanded += entry.Length;

                if (entry.FullName.EndsWith('/'))
                {
                    if (entry.Length != 0) return new(ArchiveOutcome.Corrupt, "压缩包目录条目不能包含文件内容");
                    continue;
                }
                if (ooxml is not null && entry.FullName == "[Content_Types].xml")
                {
                    if (contentTypesSeen || !await ValidateContentTypesAsync(entry, ooxml, ct))
                        return InvalidOoxml(ooxml);
                    contentTypesSeen = true;
                }
                if (ooxml is not null && entry.FullName == ooxml.MainPartPath)
                {
                    if (mainPartSeen || !await ValidateMainPartRootAsync(entry, ooxml, ct))
                        return InvalidOoxml(ooxml);
                    mainPartSeen = true;
                }
                try
                {
                    await using var input = entry.Open();
                    var verdict = await InspectEntryAsync(input, entry.Length, limits, depth, workDirectory, budget, ct);
                    if (verdict.Outcome is not ArchiveOutcome.Accepted) return verdict;
                }
                catch (ArchiveLimitException ex) { return new(ArchiveOutcome.LimitExceeded, ex.Message); }
                catch (InvalidDataException) { return new(ArchiveOutcome.Corrupt, "压缩包已损坏或内容无效"); }
            }
            if (archiveExpanded / fileLength > limits.MaxRatio) return new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常");
            if (ooxml is not null && (!contentTypesSeen || !mainPartSeen)) return InvalidOoxml(ooxml);
        }
        return ArchiveVerdict.Accepted;
    }

    private static ArchiveVerdict InvalidOoxml(OoxmlPackage package) =>
        new(ArchiveOutcome.Corrupt, $"文件不是有效的 .{package.Extension} Office 文档（缺少必要的 OOXML 包结构）");

    private static XmlReaderSettings SafeXmlSettings(long maximumCharacters) => new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maximumCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static async Task<bool> ValidateContentTypesAsync(ZipArchiveEntry entry, OoxmlPackage package, CancellationToken ct)
    {
        const string contentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
        if (entry.Length <= 0 || entry.Length > 4 * 1024 * 1024) return false;
        try
        {
            await using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, SafeXmlSettings(4 * 1024 * 1024));
            var rootSeen = false;
            var mainOverrideSeen = false;
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!rootSeen)
                {
                    if (reader.LocalName != "Types" || reader.NamespaceURI != contentTypesNamespace) return false;
                    rootSeen = true;
                    continue;
                }
                if (reader.LocalName == "Override" && reader.NamespaceURI == contentTypesNamespace
                    && reader.GetAttribute("PartName") == "/" + package.MainPartPath
                    && reader.GetAttribute("ContentType") == package.MainContentType)
                    mainOverrideSeen = true;
            }
            return rootSeen && mainOverrideSeen;
        }
        catch (XmlException) { return false; }
    }

    private static async Task<bool> ValidateMainPartRootAsync(ZipArchiveEntry entry, OoxmlPackage package, CancellationToken ct)
    {
        if (entry.Length <= 0) return false;
        try
        {
            await using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, SafeXmlSettings(1024 * 1024));
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                return reader.LocalName == package.MainRootName && reader.NamespaceURI == package.MainRootNamespace;
            }
            return false;
        }
        catch (XmlException) { return false; }
    }

    private static async Task<ArchiveVerdict> InspectSharpArchiveAsync(
        string path,
        string detectedType,
        ArchiveLimits limits,
        int depth,
        string workDirectory,
        Budget budget,
        CancellationToken ct)
    {
        if (depth > limits.MaxDepth) return new(ArchiveOutcome.LimitExceeded, "压缩包嵌套层级超出限制");
        try
        {
            if (detectedType == "rar")
                return await InspectRarArchiveAsync(path, limits, depth, workDirectory, budget, ct);

            await using var archive = await ArchiveFactory.OpenAsyncArchive(path, ReaderOptions.ForFilePath, ct);
            if (archive.Type != ArchiveType.SevenZip)
                return new(ArchiveOutcome.Corrupt, "压缩包内容格式无效");
            if (!await archive.IsCompleteAsync())
                return new(ArchiveOutcome.Corrupt, "压缩包不完整");
            if (await archive.IsEncryptedAsync())
                return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");

            var fileLength = Math.Max(1, new FileInfo(path).Length);
            long archiveExpanded = 0;
            // 7z entries can share a compressed folder. Read them once in archive
            // order so later entries do not repeatedly decompress and skip all
            // preceding data outside the actual-byte budget.
            await using (var reader = await archive.ExtractAllEntriesAsync())
            {
                while (await reader.MoveToNextEntryAsync(ct))
                {
                    var entry = reader.Entry;
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");
                    if (UnsafeEntryName(entry.Key)) return UnsafeEntry;
                    var metadata = AccountMetadata(budget, entry.Size, entry.CompressedSize);
                    if (metadata is not null) return metadata;
                    archiveExpanded += entry.Size;

                    if (entry.IsDirectory)
                    {
                        if (entry.Size != 0) return new(ArchiveOutcome.Corrupt, "压缩包目录条目不能包含文件内容");
                        continue;
                    }
                    await using var input = await reader.OpenEntryStreamAsync(ct);
                    var verdict = await InspectEntryAsync(input, entry.Size, limits, depth, workDirectory, budget, ct);
                    if (verdict.Outcome is not ArchiveOutcome.Accepted) return verdict;
                }
            }
            if (archiveExpanded / fileLength > limits.MaxRatio)
                return new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常");
            return ArchiveVerdict.Accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArchiveLimitException ex) { return new(ArchiveOutcome.LimitExceeded, ex.Message); }
        catch (SharpCompress.Common.CryptographicException) { return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包"); }
        catch (SharpCompressException) { return new(ArchiveOutcome.Corrupt, "压缩包已损坏或格式无效"); }
        catch (InvalidDataException) { return new(ArchiveOutcome.Corrupt, "压缩包已损坏或内容无效"); }
        catch (IOException) { return new(ArchiveOutcome.Corrupt, "压缩包无法完整读取"); }
    }

    private static async Task<ArchiveVerdict> InspectRarArchiveAsync(
        string path,
        ArchiveLimits limits,
        int depth,
        string workDirectory,
        Budget budget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // SharpCompress 0.50.4's asynchronous RAR metadata methods use separate
        // lazy enumerators over the same source stream. Calling more than one can
        // leave the next enumerator past the marker, so use its stable archive API
        // for headers and keep cancellation on every extracted read.
        using var archive = ArchiveFactory.OpenArchive(path, ReaderOptions.ForFilePath);
        if (archive.Type != ArchiveType.Rar)
            return new(ArchiveOutcome.Corrupt, "压缩包内容格式无效");
        if (!archive.IsComplete)
            return new(ArchiveOutcome.Corrupt, "压缩包不完整");
        if (archive.IsEncrypted)
            return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");

        var fileLength = Math.Max(1, new FileInfo(path).Length);
        long archiveExpanded = 0;
        if (archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");
                if (UnsafeEntryName(entry.Key)) return UnsafeEntry;
                var metadata = AccountMetadata(budget, entry.Size, entry.CompressedSize);
                if (metadata is not null) return metadata;
                archiveExpanded += entry.Size;

                if (entry.IsDirectory)
                {
                    if (entry.Size != 0) return new(ArchiveOutcome.Corrupt, "压缩包目录条目不能包含文件内容");
                    continue;
                }
                await using var input = reader.OpenEntryStream();
                var verdict = await InspectEntryAsync(input, entry.Size, limits, depth, workDirectory, budget, ct);
                if (verdict.Outcome is not ArchiveOutcome.Accepted) return verdict;
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "不允许传递加密的压缩包");
                if (UnsafeEntryName(entry.Key)) return UnsafeEntry;
                var metadata = AccountMetadata(budget, entry.Size, entry.CompressedSize);
                if (metadata is not null) return metadata;
                archiveExpanded += entry.Size;

                if (entry.IsDirectory)
                {
                    if (entry.Size != 0) return new(ArchiveOutcome.Corrupt, "压缩包目录条目不能包含文件内容");
                    continue;
                }
                await using var input = entry.OpenEntryStream();
                var verdict = await InspectEntryAsync(input, entry.Size, limits, depth, workDirectory, budget, ct);
                if (verdict.Outcome is not ArchiveOutcome.Accepted) return verdict;
            }
        }
        return archiveExpanded / fileLength > limits.MaxRatio
            ? new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常")
            : ArchiveVerdict.Accepted;
    }

    private static ArchiveVerdict? AccountMetadata(Budget budget, long expanded, long compressed)
    {
        if (expanded < 0 || compressed < 0) return new(ArchiveOutcome.Corrupt, "压缩包条目大小无效");
        if (++budget.Entries > budget.Limits.MaxEntries)
            return new(ArchiveOutcome.LimitExceeded, "压缩包文件数量超出限制");
        if (expanded > budget.Limits.MaxExpandedBytes - budget.DeclaredExpandedBytes)
            return new(ArchiveOutcome.LimitExceeded, "压缩包解压后总量超出限制");
        budget.DeclaredExpandedBytes += expanded;
        // SevenZipEntry.CompressedSize is always 0 in SharpCompress 0.50.4 because
        // files can share one compressed folder. In that case only the archive-wide
        // ratio is meaningful; declared and actual expanded-byte limits still apply.
        if (expanded > 0 && compressed > 0 && expanded / compressed > budget.Limits.MaxRatio)
            return new(ArchiveOutcome.LimitExceeded, "压缩包压缩比异常");
        return null;
    }

    private static long AddOrLimit(long current, long amount, long limit)
    {
        if (amount < 0 || current > limit - amount)
            throw new ArchiveLimitException("压缩包解压后总量超出限制");
        return current + amount;
    }

    private static async Task<ArchiveVerdict> InspectEntryAsync(
        Stream input,
        long declaredLength,
        ArchiveLimits limits,
        int depth,
        string workDirectory,
        Budget budget,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;
        var headLength = 0;
        while (headLength < 64)
        {
            var read = await input.ReadAsync(buffer.AsMemory(headLength, 64 - headLength), ct);
            if (read == 0) break;
            headLength += read;
            total += read;
            AccountActualBytes(budget, read);
        }
        if (FileSignatureInspector.IsExecutable(buffer.AsSpan(0, headLength)))
            return new(ArchiveOutcome.Forbidden, "压缩包内包含可执行程序，禁止传递");
        var detected = FileSignatureInspector.Detect(buffer.AsSpan(0, headLength));
        if (detected is not ("zip" or "rar" or "7z"))
        {
            total += await DrainAsync(input, Stream.Null, buffer, budget, ct);
            return total == declaredLength
                ? ArchiveVerdict.Accepted
                : new(ArchiveOutcome.Corrupt, "压缩包条目实际大小与记录不符");
        }
        if (depth + 1 > limits.MaxDepth)
            return new(ArchiveOutcome.LimitExceeded, "压缩包嵌套层级超出限制");

        // Never incorporate an attacker-controlled entry name into a filesystem path.
        var temp = Path.Combine(workDirectory, $"nested-{Guid.NewGuid():N}.tmp");
        EnsureWorkspaceSpace(workDirectory, declaredLength, budget);
        try
        {
            FileStream output;
            try { output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (Exception error) when (ArchiveWorkspaceException.IsWorkspaceFault(error)) { throw new ArchiveWorkspaceException(error); }
            await using (output)
            {
                await WriteWorkspaceAsync(output, buffer.AsMemory(0, headLength), ct);
                total += await DrainAsync(input, output, buffer, budget, ct);
                try { await output.FlushAsync(ct); }
                catch (Exception error) when (ArchiveWorkspaceException.IsWorkspaceFault(error)) { throw new ArchiveWorkspaceException(error); }
            }
            if (total != declaredLength)
                return new(ArchiveOutcome.Corrupt, "压缩包条目实际大小与记录不符");
            return detected == "zip"
                ? await InspectZipAsync(temp, limits, depth + 1, workDirectory, budget, null, ct)
                : await InspectSharpArchiveAsync(temp, detected, limits, depth + 1, workDirectory, budget, ct);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>
    /// Refuses to spill a nested archive when the work volume cannot hold it. The bound is the
    /// declared entry size, capped by what the expansion budget still allows (a forged size
    /// cannot demand more than the budget would ever let through). A shortfall is an
    /// environment problem, so it surfaces as a retryable <see cref="ArchiveWorkspaceException"/>.
    /// </summary>
    private static void EnsureWorkspaceSpace(string workDirectory, long declaredLength, Budget budget)
    {
        var remaining = Math.Max(0, budget.Limits.MaxExpandedBytes - budget.ActualExpandedBytes);
        var required = (ulong)Math.Min(Math.Max(0, declaredLength), remaining);
        // The parent (the uploads area) is a stable path on the same volume; the per-run
        // work directory itself is not used as the key of the resolved-root cache.
        var volumeProbe = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(workDirectory)) ?? workDirectory;
        try { FileStorage.EnsureFreeSpace(volumeProbe, required); }
        catch (Exception error) when (error is Infrastructure.ApiException or IOException or UnauthorizedAccessException)
        {
            throw new ArchiveWorkspaceException(error);
        }
    }

    private static async Task<long> DrainAsync(Stream input, Stream output, byte[] buffer, Budget budget, CancellationToken ct)
    {
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            AccountActualBytes(budget, read);
            total += read;
            await WriteWorkspaceAsync(output, buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    /// <summary>Only writes go to the work directory; reads come from the archive and describe its content.</summary>
    private static async ValueTask WriteWorkspaceAsync(Stream output, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try { await output.WriteAsync(data, ct); }
        catch (Exception error) when (ArchiveWorkspaceException.IsWorkspaceFault(error)) { throw new ArchiveWorkspaceException(error); }
    }

    private static void AccountActualBytes(Budget budget, int read)
    {
        budget.ActualExpandedBytes = AddOrLimit(budget.ActualExpandedBytes, read, budget.Limits.MaxExpandedBytes);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
