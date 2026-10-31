using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Oem.Storage;

/// <summary>
/// Layout of the dedicated, never-backed-up OEM storage root:
/// <c>uploads/&lt;session&gt;/</c> for chunks, <c>quarantine/</c> for files awaiting validation and
/// <c>available/</c> for validated files. All three live on one volume so
/// promotion is an atomic rename. Path safety (lexical containment, reparse points,
/// free space) reuses the collaboration storage helpers.
/// While a storage migration runs (存储路径页面配置与在线迁移方案), new content goes to the primary root and existing
/// content keeps being read, moved and deleted in whichever root currently holds it, so a quarantine → available
/// promotion stays a same-volume rename.
/// </summary>
public sealed class OemStorage(AppOptions options)
{
    public const string UploadsArea = "uploads";
    public const string QuarantineArea = "quarantine";
    public const string AvailableArea = "available";

    public bool IsConfigured => options.OemStorageRoot.Length > 0;

    /// <summary>True while a storage migration copies OEM content to a new root.</summary>
    public bool Migrating => options.OemStorageFallbackRoot is not null;

    public string Root()
    {
        if (!IsConfigured) throw new ApiException(503, 50301, "OEM 文件存储未配置，请联系管理员");
        return ResolveRoot(options.OemStorageRoot);
    }

    /// <summary>The primary root, then the old root of a running migration.</summary>
    public IReadOnlyList<string> Roots()
    {
        var primary = Root();
        return options.OemStorageFallbackRoot is { } fallback ? [primary, ResolveRoot(fallback)] : [primary];
    }

    private static string ResolveRoot(string configuredRoot)
    {
        var configured = Path.GetFullPath(configuredRoot);
        if (Directory.Exists(configured)
            && (new DirectoryInfo(configured).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("OEM storage root cannot be a reparse point");
        return FileStorage.Root(configured);
    }

    /// <summary>The root holding an existing upload session directory; new sessions use the primary root.</summary>
    private string SessionRoot(string sessionId)
    {
        var roots = Roots();
        return roots.FirstOrDefault(root => Directory.Exists(Path.Combine(root, UploadsArea, sessionId))) ?? roots[0];
    }

    public string SessionDirectory(string sessionId, bool create, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _)) throw ApiException.NotFound();
        var root = SessionRoot(sessionId);
        var directory = FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, UploadsArea, sessionId), allowRoot: false);
        return create ? FileStorage.CreateDirectoryWithin(root, directory, ct) : directory;
    }

    public string ChunkPath(string sessionId, uint index) =>
        FileStorage.EnsureLexicallyWithin(SessionRoot(sessionId),
            Path.Combine(SessionDirectory(sessionId, false, CancellationToken.None), $"{index}.part"), false);

    /// <summary>The root containing <paramref name="absolutePath"/>.</summary>
    public string RootOf(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Roots().FirstOrDefault(root => full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            ?? throw new InvalidOperationException("非法 OEM 存储路径");
    }

    public static string QuarantineRelative(string storedName) => Relative(QuarantineArea, storedName);
    public static string AvailableRelative(string storedName) => Relative(AvailableArea, storedName);

    /// <summary>
    /// Absolute path for a stored relative path in the root that holds it (the primary root when none does);
    /// rejects anything escaping the root.
    /// </summary>
    public string Absolute(string relative)
    {
        var roots = Roots();
        if (roots.Count > 1)
        {
            foreach (var root in roots)
            {
                var candidate = AbsoluteIn(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return AbsoluteIn(roots[0], relative);
    }

    /// <summary>Absolute path for <paramref name="relative"/> in the same root as <paramref name="sibling"/>, so a move stays a rename.</summary>
    public string AbsoluteBeside(string relative, string sibling) => AbsoluteIn(RootOf(sibling), relative);

    /// <summary>Every location <paramref name="relative"/> may occupy (one per root).</summary>
    public IEnumerable<string> AbsoluteEverywhere(string relative) => Roots().Select(root => AbsoluteIn(root, relative));

    private static string AbsoluteIn(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part == ".."))
            throw new InvalidOperationException("非法 OEM 存储路径");
        return FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), false);
    }

    /// <summary>Creates the parent directory of <paramref name="absolutePath"/> inside the root (no reparse-point escapes).</summary>
    public void EnsureParent(string absolutePath, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(absolutePath) ?? throw new InvalidOperationException("非法 OEM 存储路径");
        FileStorage.CreateDirectoryWithin(RootOf(absolutePath), parent, ct);
    }

    /// <summary>Resolves an existing file through reparse points and confirms it is still inside the root.</summary>
    public Task<string> ResolveExistingFileAsync(string relative, CancellationToken ct)
    {
        var path = Absolute(relative);
        return Task.FromResult(FileStorage.ResolveExistingFile(RootOf(path), path, ct));
    }

    public bool Exists(string relative) => AbsoluteEverywhere(relative).Any(File.Exists);

    public void EnsureFreeSpace(ulong bytes) => FileStorage.EnsureFreeSpace(Root(), bytes);

    public Task<bool> DeleteDirectoryAsync(string absoluteDirectory, CancellationToken ct) =>
        Task.FromResult(FileStorage.DeleteDirectoryTree(RootOf(absoluteDirectory), absoluteDirectory, ct));

    public static string NewStoredName() => Guid.NewGuid().ToString("D");

    // Two-character fan-out keeps directories small without leaking anything about the file.
    private static string Relative(string area, string storedName)
    {
        if (!Guid.TryParseExact(storedName, "D", out _)) throw new InvalidOperationException("非法 OEM 文件标识");
        return $"{area}/{storedName[..2]}/{storedName}";
    }
}
