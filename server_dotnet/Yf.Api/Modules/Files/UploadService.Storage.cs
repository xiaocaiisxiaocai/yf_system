using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Files;

// Chunk and pending-file storage on disk: exact writes, crash markers and cleanup of session artifacts.
public sealed partial class UploadService
{
    private static async Task WriteExactAsync(Stream source, string destination, ulong expected, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        ulong total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total = checked(total + (uint)read);
            if (total > expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际超过上限");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {total}");
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    internal static string MergeLockName(MySqlConnection conn, string sessionId) =>
        MySqlNamedLock.Name("upload-merge", conn.Database, sessionId);

    internal static async Task WritePendingFinalMarkerAsync(
        string root, string sessionId, string relativePath, CancellationToken ct)
    {
        var directory = FileStorage.SessionDirectory(root, sessionId);
        directory = FileStorage.CreateDirectoryWithin(root, directory, ct);
        var markerId = Guid.NewGuid().ToString("D");
        var marker = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, PendingFinalMarkerPrefix + markerId), false);
        var staging = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, PendingFinalStagingPrefix + markerId), false);
        var payload = Encoding.UTF8.GetBytes(relativePath);
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(payload, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, marker, overwrite: false);
        }
        finally { TryDeleteFile(staging); }
    }

    internal static async Task CleanupPendingFinalsAsync(
        MySqlConnection conn,
        string configuredRoot,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        var root = FileStorage.Root(configuredRoot);
        var directory = FileStorage.SessionDirectory(root, sessionId);
        if (!Directory.Exists(directory)) return;
        directory = FileStorage.ResolveExisting(root, directory, requireFile: false, ct);
        foreach (var candidate in Directory.EnumerateFiles(
                     directory, PendingFinalMarkerPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var marker = FileStorage.ResolveExistingFile(root, candidate, ct);
            if (new FileInfo(marker).Length is <= 0 or > 2048)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记长度无效");
                continue;
            }

            string relativePath;
            try
            {
                relativePath = await File.ReadAllTextAsync(marker, new UTF8Encoding(false, true), ct);
            }
            catch (DecoderFallbackException)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记不是有效 UTF-8");
                continue;
            }

            // Check the durable database reference before classifying legacy marker text. A committed
            // file must always win, even if a historical marker does not match today's filename rules.
            await using var context = EfDb.Use(conn);
            var referenced = await context.Files.AnyAsync(file => file.StoragePath == relativePath, ct);
            if (referenced)
            {
                File.Delete(marker);
                continue;
            }

            if (!IsPendingFinalPath(relativePath))
            {
                // A truncated marker cannot prove which file was published. Keep every possible
                // target untouched, move the marker out of the active pattern, and let the owned
                // session directory cleanup remove the quarantined evidence.
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记路径格式无效");
                continue;
            }

            string finalPath;
            try { finalPath = FileStorage.ResolveForCleanup(root, relativePath, ct); }
            catch (InvalidOperationException)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记路径越出存储根目录");
                continue;
            }

            if (Directory.Exists(finalPath))
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记目标不是文件");
                continue;
            }
            if (File.Exists(finalPath))
            {
                finalPath = FileStorage.ResolveExistingFile(root, finalPath, ct);
                File.Delete(finalPath);
            }
            File.Delete(marker);
        }
    }

    private static bool IsPendingFinalPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath != relativePath.Trim()
            || relativePath.Contains('\\')
            || Path.IsPathFullyQualified(relativePath)) return false;
        var parts = relativePath.Split('/');
        if (parts.Length != 4 || parts[0] != "files"
            || parts[1].Length != 4 || !parts[1].All(char.IsAsciiDigit)
            || parts[2].Length != 2 || !int.TryParse(parts[2], out var month) || month is < 1 or > 12)
            return false;
        var extension = Path.GetExtension(parts[3]);
        return extension.Length is >= 2 and <= 17
               && extension.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit)
               && Guid.TryParseExact(Path.GetFileNameWithoutExtension(parts[3]), "D", out _);
    }

    private static void QuarantinePendingFinalMarker(
        string root,
        string directory,
        string marker,
        string sessionId,
        ILogger logger,
        string reason)
    {
        var quarantined = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, InvalidPendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")), false);
        try
        {
            File.Move(marker, quarantined, overwrite: false);
            logger.LogWarning("已隔离无效待提交文件标记 {SessionId} {MarkerName}: {Reason}",
                sessionId, Path.GetFileName(marker), reason);
        }
        catch (Exception error)
        {
            // Do not interpret or delete a target from invalid marker text. The caller can still
            // safely delete the owned session directory, including the marker itself.
            logger.LogWarning(error, "无法隔离无效待提交文件标记 {SessionId} {MarkerName}: {Reason}",
                sessionId, Path.GetFileName(marker), reason);
        }
    }

    private async Task TryCleanupSessionArtifactsAsync(
        MySqlConnection conn, string sessionId, CancellationToken ct)
    {
        try
        {
            await CleanupPendingFinalsAsync(conn, options.StorageRoot, sessionId, logger, ct);
            FileStorage.DeleteDirectoryTree(options.StorageRoot,
                FileStorage.SessionDirectory(FileStorage.Root(options.StorageRoot), sessionId), ct);
        }
        catch
        {
            // The maintenance worker retries durable markers and session directories.
        }
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }

    private static void TryDeleteDirectory(string root, string path, CancellationToken ct)
    {
        try { FileStorage.DeleteDirectoryTree(root, path, ct); }
        catch { }
    }
}
