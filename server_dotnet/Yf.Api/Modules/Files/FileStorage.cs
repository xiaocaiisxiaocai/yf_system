using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Yf.Api.Modules.Files;

internal static class FileStorage
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly ConcurrentDictionary<string, string> ResolvedRoots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string Root(string configuredRoot)
    {
        var full = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ResolvedRoots.GetOrAdd(full, static path =>
        {
            Directory.CreateDirectory(path);
            return ResolveFinalTarget(new DirectoryInfo(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        });
    }

    public static string SessionDirectory(string root, string sessionId)
    {
        ValidateSessionId(sessionId);
        return EnsureLexicallyWithin(root, Path.Combine(root, "tmp", sessionId), allowRoot: false);
    }

    public static string ChunkPath(string root, string sessionId, uint index) =>
        EnsureLexicallyWithin(root, Path.Combine(SessionDirectory(root, sessionId), $"{index}.part"), false);

    public static string CreateDirectoryWithin(string root, string candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rootFull = Path.GetFullPath(root);
        var resolvedRoot = Root(rootFull);
        var lexical = EnsureLexicallyWithin(rootFull, candidate, allowRoot: false);
        var relative = Path.GetRelativePath(rootFull, lexical);
        var current = resolvedRoot;
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var next = Path.Combine(current, component);
            FileSystemInfo info;
            if (Directory.Exists(next)) info = new DirectoryInfo(next);
            else if (File.Exists(next)) info = new FileInfo(next);
            else
            {
                Directory.CreateDirectory(next);
                info = new DirectoryInfo(next);
            }

            current = ResolveFinalTarget(info);
            if (!IsWithin(resolvedRoot, current) || PathsEqual(resolvedRoot, current))
                throw new InvalidOperationException("非法存储路径");
            if (!Directory.Exists(current))
                throw new InvalidOperationException("存储目录路径包含文件");
        }
        return current;
    }

    public static string EnsureLexicallyWithin(string root, string candidate, bool allowRoot)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = Path.GetFullPath(candidate);
        if ((!allowRoot && PathsEqual(normalizedRoot, normalized)) || !IsWithin(normalizedRoot, normalized))
            throw new InvalidOperationException("非法存储路径");
        return normalized;
    }

    public static string ResolveExistingFile(string root, string candidate, CancellationToken ct) =>
        ResolveExisting(root, candidate, requireFile: true, ct);

    public static string ResolveExisting(string root, string candidate, bool requireFile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolvedRoot = Root(root);
        var lexical = EnsureLexicallyWithin(root, candidate, allowRoot: false);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), lexical);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(component => component == ".."))
            throw new InvalidOperationException("非法存储路径");

        var current = resolvedRoot;
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) throw new FileNotFoundException();
            current = ResolveFinalTarget(info);
            if (!IsWithin(resolvedRoot, current) || PathsEqual(resolvedRoot, current))
                throw new InvalidOperationException("非法存储路径");
        }

        if (requireFile && !File.Exists(current)) throw new FileNotFoundException();
        return current;
    }

    public static bool DeleteDirectoryTree(
        string root, string candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(candidate)) return false;

        var resolvedRoot = Root(root);
        var lexical = EnsureLexicallyWithin(root, candidate, allowRoot: false);
        var resolved = ResolveExisting(root, lexical, requireFile: false, ct);
        if (!IsWithin(resolvedRoot, resolved) || PathsEqual(resolvedRoot, resolved))
            throw new InvalidOperationException("递归删除目录位于存储根目录之外");

        // Validate the whole tree before deleting anything. Reparse points are rejected even
        // when they currently resolve inside the root: their targets can change between the
        // validation and deletion passes, and recursive deletion must never follow them.
        ValidateDirectoryTreeWithoutReparsePoints(resolvedRoot, resolved, ct);
        DeleteDirectoryTreeWithoutReparsePoints(resolved, ct);
        return true;
    }

    public static void EnsureFreeSpace(string root, ulong requiredBytes)
    {
        var resolvedRoot = Root(root);
        var driveRoot = Path.GetPathRoot(resolvedRoot) ?? throw new InvalidOperationException("无法解析存储卷");
        var available = new DriveInfo(driveRoot).AvailableFreeSpace;
        const ulong reserve = 64UL * 1024 * 1024;
        if (requiredBytes > ulong.MaxValue - reserve || (ulong)Math.Max(0, available) < requiredBytes + reserve)
            throw Infrastructure.ApiException.Conflict("存储空间不足，请联系管理员清理或扩容");
    }

    public static async Task<(string Sha256, string Md5, ulong Bytes)> HashAndCopyAsync(
        IEnumerable<string> chunks, string outputPath, ulong maximumBytes, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            ulong total = 0;
            foreach (var chunk in chunks)
            {
                await using var input = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) != 0)
                {
                    total = checked(total + (uint)read);
                    if (total > maximumBytes) throw Infrastructure.ApiException.BadRequest($"合并文件大小不符：期望 {maximumBytes}，实际超过上限");
                    sha.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(), total);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static string MimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".step" or ".stp" => "model/step",
        ".iges" or ".igs" => "model/iges",
        ".stl" => "model/stl",
        ".obj" => "model/obj",
        ".dwg" => "image/vnd.dwg",
        ".dxf" => "image/vnd.dxf",
        ".zip" => "application/zip",
        ".rar" => "application/vnd.rar",
        ".7z" => "application/x-7z-compressed",
        _ => "application/octet-stream"
    };

    private static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _)) throw Infrastructure.ApiException.NotFound();
    }

    private static string ResolveFinalTarget(FileSystemInfo info)
    {
        var full = Path.GetFullPath(info.FullName);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return full;
        return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? throw new InvalidOperationException("无法解析存储符号链接"));
    }

    private static void ValidateDirectoryTreeWithoutReparsePoints(
        string resolvedRoot, string directory, CancellationToken ct)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            RejectReparsePoint(current, resolvedRoot);
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                RejectReparsePoint(entry, resolvedRoot);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    private static void DeleteDirectoryTreeWithoutReparsePoints(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = new DirectoryInfo(directory);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("递归删除目录包含重解析点");
        foreach (var entry in current.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("递归删除目录包含重解析点");
            if (entry is DirectoryInfo child) DeleteDirectoryTreeWithoutReparsePoints(child.FullName, ct);
            else entry.Delete();
        }
        current.Delete(recursive: false);
    }

    private static void RejectReparsePoint(FileSystemInfo entry, string resolvedRoot)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return;
        var target = ResolveFinalTarget(entry);
        if (!IsWithin(resolvedRoot, target) || PathsEqual(resolvedRoot, target))
            throw new InvalidOperationException("递归删除目录包含越出存储根目录的重解析点");
        throw new InvalidOperationException("递归删除目录包含重解析点");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison);

    private static bool IsWithin(string root, string candidate)
    {
        if (PathsEqual(root, candidate)) return true;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }
}
