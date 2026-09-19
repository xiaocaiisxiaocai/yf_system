using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Oem.Storage;

/// <summary>
/// Layout of the dedicated, never-backed-up OEM storage root:
/// <c>uploads/&lt;session&gt;/</c> for chunks, <c>quarantine/</c> for unscanned files and
/// <c>available/</c> for files that passed scanning. All three live on one volume so
/// promotion is an atomic rename. Path safety (lexical containment, reparse points,
/// free space) reuses the collaboration storage helpers.
/// </summary>
public sealed class OemStorage(AppOptions options)
{
    public const string UploadsArea = "uploads";
    /// <summary>Scratch area for the on-access antivirus canary; never holds business files.</summary>
    public const string ScanProbeArea = "scan-probe";
    public const string QuarantineArea = "quarantine";
    public const string AvailableArea = "available";

    public bool IsConfigured => options.OemStorageRoot.Length > 0;

    public string Root() => IsConfigured
        ? FileStorage.Root(options.OemStorageRoot)
        : throw new ApiException(503, 50301, "OEM 文件存储未配置，请联系管理员");

    public string SessionDirectory(string sessionId, bool create, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _)) throw ApiException.NotFound();
        var root = Root();
        var directory = FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, UploadsArea, sessionId), allowRoot: false);
        return create ? FileStorage.CreateDirectoryWithin(root, directory, ct) : directory;
    }

    public string ChunkPath(string sessionId, uint index) =>
        FileStorage.EnsureLexicallyWithin(Root(), Path.Combine(SessionDirectory(sessionId, false, CancellationToken.None), $"{index}.part"), false);

    public static string QuarantineRelative(string storedName) => Relative(QuarantineArea, storedName);
    public static string AvailableRelative(string storedName) => Relative(AvailableArea, storedName);

    /// <summary>Absolute path for a stored relative path; rejects anything escaping the root.</summary>
    public string Absolute(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part == ".."))
            throw new InvalidOperationException("非法 OEM 存储路径");
        var root = Root();
        return FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), false);
    }

    /// <summary>Creates the parent directory of <paramref name="absolutePath"/> inside the root (no reparse-point escapes).</summary>
    public void EnsureParent(string absolutePath, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(absolutePath) ?? throw new InvalidOperationException("非法 OEM 存储路径");
        FileStorage.CreateDirectoryWithin(Root(), parent, ct);
    }

    /// <summary>Resolves an existing file through reparse points and confirms it is still inside the root.</summary>
    public Task<string> ResolveExistingFileAsync(string relative, CancellationToken ct) =>
        FileStorage.ResolveExistingFileAsync(Root(), Absolute(relative), ct);

    public bool Exists(string relative) => File.Exists(Absolute(relative));

    public void EnsureFreeSpace(ulong bytes) => FileStorage.EnsureFreeSpace(Root(), bytes);

    public Task<bool> DeleteDirectoryAsync(string absoluteDirectory, CancellationToken ct) =>
        FileStorage.DeleteDirectoryTreeAsync(Root(), absoluteDirectory, ct);

    public static string NewStoredName() => Guid.NewGuid().ToString("D");

    // Two-character fan-out keeps directories small without leaking anything about the file.
    private static string Relative(string area, string storedName)
    {
        if (!Guid.TryParseExact(storedName, "D", out _)) throw new InvalidOperationException("非法 OEM 文件标识");
        return $"{area}/{storedName[..2]}/{storedName}";
    }
}
