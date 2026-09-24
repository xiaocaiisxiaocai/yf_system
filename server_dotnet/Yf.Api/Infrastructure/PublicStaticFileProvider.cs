using System.Collections;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Yf.Api.Infrastructure;

internal sealed class PublicStaticFileProvider : IFileProvider
{
    private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "secrets.json", "web.config",
    };
    private static readonly string[] BlockedExtensions =
    [
        ".config", ".cs", ".deps.json", ".dll", ".key", ".p12", ".pdb", ".pfx", ".runtimeconfig.json",
    ];

    private readonly IFileProvider inner;
    private readonly string webRoot;
    private readonly string webRootPrefix;
    private readonly StringComparison pathComparison;

    private PublicStaticFileProvider(IFileProvider inner, string webRoot)
    {
        this.inner = inner;
        this.webRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot));
        webRootPrefix = this.webRoot.EndsWith(Path.DirectorySeparatorChar)
            ? this.webRoot
            : this.webRoot + Path.DirectorySeparatorChar;
        pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (Directory.Exists(this.webRoot) && IsReparsePoint(this.webRoot))
            throw new InvalidOperationException("The public web root cannot be a linked directory.");
    }

    public static IFileProvider Create(IWebHostEnvironment environment)
    {
        if (environment.WebRootFileProvider is PublicStaticFileProvider secured) return secured;
        var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        return new PublicStaticFileProvider(environment.WebRootFileProvider, root);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        if (!TryResolve(subpath, out var relativePath, out _))
            return new NotFoundFileInfo(Path.GetFileName(subpath));

        var file = inner.GetFileInfo(relativePath);
        if (!file.Exists) return file;
        if (file.PhysicalPath is null || !IsWithinRoot(file.PhysicalPath) || ContainsReparsePoint(file.PhysicalPath))
            return new NotFoundFileInfo(file.Name);
        return file;
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        if (!TryResolve(subpath, out var relativePath, out var physicalPath) || ContainsReparsePoint(physicalPath))
            return NotFoundDirectoryContents.Singleton;

        var contents = inner.GetDirectoryContents(relativePath);
        if (!contents.Exists) return contents;

        var prefix = relativePath.Length == 0 ? "" : relativePath + "/";
        var entries = contents.Where(entry => GetFileInfo(prefix + entry.Name).Exists).ToArray();
        return new FilteredDirectoryContents(entries);
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    internal static bool IsSafePath(string? subpath) => TryValidateSegments(subpath, out _);

    private bool TryResolve(string? subpath, out string relativePath, out string physicalPath)
    {
        relativePath = "";
        physicalPath = webRoot;
        if (!TryValidateSegments(subpath, out var segments)) return false;
        relativePath = string.Join('/', segments);

        try
        {
            physicalPath = Path.GetFullPath(Path.Combine(webRoot, Path.Combine(segments)));
            return IsWithinRoot(physicalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidateSegments(string? subpath, out string[] segments)
    {
        segments = [];
        if (subpath is null || subpath.IndexOfAny(['\\', '\0', ':']) >= 0
            || subpath.StartsWith("//", StringComparison.Ordinal))
            return false;

        segments = subpath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.StartsWith(".", StringComparison.Ordinal)))
            return false;
        if (segments.Length > 0 && (segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("health", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("privateuploads", StringComparison.OrdinalIgnoreCase)))
            return false;

        var fileName = segments.LastOrDefault();
        return fileName is null || (!BlockedFileNames.Contains(fileName)
            && !fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            && !BlockedExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsWithinRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.Equals(webRoot, pathComparison) || fullPath.StartsWith(webRootPrefix, pathComparison);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private bool ContainsReparsePoint(string path)
    {
        var relative = Path.GetRelativePath(webRoot, path);
        var cursor = webRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".") continue;
            cursor = Path.Combine(cursor, segment);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && IsReparsePoint(cursor)) return true;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private sealed class FilteredDirectoryContents(IReadOnlyList<IFileInfo> entries) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
