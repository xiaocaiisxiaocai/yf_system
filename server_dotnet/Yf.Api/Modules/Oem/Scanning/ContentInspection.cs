using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
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
/// are refused outright (they cannot be scanned), and ZIP, RAR and 7z containers are
/// bounded by entry count, nesting depth, expanded size and compression ratio. Entries
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
            return new ArchiveVerdict(ArchiveOutcome.Encrypted, "加密的 Office 文档无法扫描");
        return detected switch
        {
            "zip" when ext == "zip" => await InspectZipAsync(path, limits, depth: 1, workDirectory, new Budget(limits), ct),
            // OOXML containers: encrypted entries or zip bombs are refused, nested archives are not expected.
            "zip" => await InspectZipAsync(path, limits with { MaxDepth = 1 }, depth: 1, workDirectory, new Budget(limits), ct),
            "rar" or "7z" => await InspectSharpArchiveAsync(path, detected, limits, depth: 1, workDirectory, new Budget(limits), ct),
            _ => ArchiveVerdict.NotArchive,
        };
    }

    private sealed class Budget(ArchiveLimits limits)
    {
        public long Entries;
        public long DeclaredExpandedBytes;
        public long ActualExpandedBytes;
        public ArchiveLimits Limits { get; } = limits;
    }

    private sealed class ArchiveLimitException(string reason) : Exception(reason);

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
                var metadata = AccountMetadata(budget, entry.Length, entry.CompressedLength);
                if (metadata is not null) return metadata;
                archiveExpanded += entry.Length;

                if (entry.FullName.EndsWith('/'))
                {
                    if (entry.Length != 0) return new(ArchiveOutcome.Corrupt, "压缩包目录条目不能包含文件内容");
                    continue;
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
        }
        return ArchiveVerdict.Accepted;
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
                return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");

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
                    if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
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
        catch (SharpCompress.Common.CryptographicException) { return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描"); }
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
            return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");

        var fileLength = Math.Max(1, new FileInfo(path).Length);
        long archiveExpanded = 0;
        if (archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
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
                if (entry.IsEncrypted) return new(ArchiveOutcome.Encrypted, "压缩包已加密，无法扫描");
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
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(buffer.AsMemory(0, headLength), ct);
                total += await DrainAsync(input, output, buffer, budget, ct);
            }
            if (total != declaredLength)
                return new(ArchiveOutcome.Corrupt, "压缩包条目实际大小与记录不符");
            return detected == "zip"
                ? await InspectZipAsync(temp, limits, depth + 1, workDirectory, budget, ct)
                : await InspectSharpArchiveAsync(temp, detected, limits, depth + 1, workDirectory, budget, ct);
        }
        finally { TryDelete(temp); }
    }

    private static async Task<long> DrainAsync(Stream input, Stream output, byte[] buffer, Budget budget, CancellationToken ct)
    {
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            AccountActualBytes(budget, read);
            total += read;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
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
