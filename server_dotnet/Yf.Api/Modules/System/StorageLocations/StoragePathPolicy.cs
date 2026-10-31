using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Checks a new storage path entered on the settings page. The page writes to the server's file system, so the path
/// must be a local absolute directory outside the site and system folders, apart from every other storage location,
/// empty, writable by the site's own account and large enough. See 存储路径页面配置与在线迁移方案 §4.
/// </summary>
internal static class StoragePathPolicy
{
    internal const long MinimumFreeMarginBytes = 1L << 30;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Format-only checks shared with the offline <c>--set-storage-path</c>; returns the normalized path.</summary>
    internal static string NormalizeFormat(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length is 0 or > 400) throw ApiException.BadRequest("请填写 1–400 个字符的目录路径");
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw ApiException.BadRequest("不支持网络共享路径，请使用本机磁盘目录");
        if (!Path.IsPathFullyQualified(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || value.Split('/', '\\').Any(part => part is "." or ".."))
            throw ApiException.BadRequest("请填写完整的本机绝对路径，例如 D:\\YfData\\storage");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0 || string.Equals(full + Path.DirectorySeparatorChar, Path.GetPathRoot(full), PathComparison)
            || string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            throw ApiException.BadRequest("不能使用磁盘根目录");
        return full;
    }

    /// <summary>
    /// Full validation of a migration target. <paramref name="others"/> are the current and previous paths of every
    /// location (including this kind's current one); <paramref name="requiredBytes"/> is the content to be copied.
    /// </summary>
    internal static string ValidateTarget(
        string? input, string applicationRoot, string? webRoot, IEnumerable<string> others, long requiredBytes)
    {
        var path = NormalizeFormat(input);
        string resolved;
        try { resolved = AppOptions.ResolveComparisonPath(path); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw ApiException.BadRequest("目录路径包含无法解析的链接"); }

        foreach (var forbidden in ForbiddenRoots(applicationRoot, webRoot))
        {
            if (Within(resolved, forbidden))
                throw ApiException.BadRequest("不能使用网站目录或系统目录");
        }
        foreach (var other in others.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string existing;
            try { existing = AppOptions.ResolveComparisonPath(other); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { existing = Path.GetFullPath(other); }
            if (AppOptions.Overlaps(resolved, existing, PathComparison))
                throw ApiException.BadRequest("新目录不能与当前或其他存储位置相同，也不能互相包含");
        }

        if (File.Exists(path)) throw ApiException.BadRequest("该路径是一个文件，请填写目录");
        try
        {
            if (Directory.Exists(path))
            {
                if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw ApiException.BadRequest("不能使用符号链接或目录联接");
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    throw ApiException.BadRequest("新目录必须为空");
            }
            else
            {
                Directory.CreateDirectory(path);
            }
            Probe(path);
        }
        catch (UnauthorizedAccessException)
        {
            throw ApiException.BadRequest("网站运行账号无法读写该目录，请先为 IIS 应用池账号授予该目录的修改权限");
        }
        catch (IOException)
        {
            throw ApiException.BadRequest("无法在该目录读写文件，请检查磁盘与权限");
        }

        var drive = Path.GetPathRoot(path) ?? throw ApiException.BadRequest("无法解析目录所在磁盘");
        var available = new DriveInfo(drive).AvailableFreeSpace;
        var required = requiredBytes + requiredBytes / 10 + MinimumFreeMarginBytes;
        if (available < required)
            throw ApiException.BadRequest(
                $"目标磁盘剩余空间不足：需要约 {required / (1L << 20)} MiB，可用 {available / (1L << 20)} MiB");
        return path;
    }

    /// <summary>Writes, reads back and deletes a probe file as the site's own account.</summary>
    private static void Probe(string directory)
    {
        var probe = Path.Combine(directory, $".yf-storage-probe-{Guid.NewGuid():N}");
        var payload = Guid.NewGuid().ToByteArray();
        try
        {
            File.WriteAllBytes(probe, payload);
            if (!File.ReadAllBytes(probe).AsSpan().SequenceEqual(payload))
                throw new IOException("Probe content mismatch.");
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private static IEnumerable<string> ForbiddenRoots(string applicationRoot, string? webRoot)
    {
        yield return AppOptions.ResolveComparisonPath(applicationRoot);
        if (!string.IsNullOrWhiteSpace(webRoot)) yield return AppOptions.ResolveComparisonPath(webRoot);
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) yield return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    private static bool Within(string path, string root) =>
        path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>Total bytes of the regular files under <paramref name="root"/>, skipping links.</summary>
    internal static (long Files, long Bytes) Measure(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return (0, 0);
        long files = 0, bytes = 0;
        foreach (var file in StorageTree.Files(root, ct))
        {
            files++;
            bytes += file.Length;
        }
        return (files, bytes);
    }
}

/// <summary>Depth-first walk of regular files that never follows reparse points.</summary>
internal static class StorageTree
{
    internal static IEnumerable<FileInfo> Files(string root, CancellationToken ct)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = directory.GetFileSystemInfos(); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file) yield return file;
            }
        }
    }

    /// <summary>Removes empty directories below <paramref name="root"/> (never the root itself), deepest first.</summary>
    internal static void RemoveEmptyDirectories(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                 }).OrderByDescending(path => path.Length))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
