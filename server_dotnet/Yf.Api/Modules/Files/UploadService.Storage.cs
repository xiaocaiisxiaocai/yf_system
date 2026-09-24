using MySqlConnector;
using System.Security.Cryptography;
using System.Text;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

// Chunk storage on disk: exact writes, persisted digests and owned session cleanup.
public sealed partial class UploadService
{
    private static async Task<string> WriteExactAndHashAsync(
        Stream source, string destination, ulong expected, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        ulong total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total = checked(total + (uint)read);
            if (total > expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际超过上限");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {total}");
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ChunkDigestPath(string chunkPath) => chunkPath + ".sha256";

    private static async Task<string> HashFileSha256Async(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string?> ValidChunkDigestAsync(
        string root, string chunkPath, ulong expectedBytes, CancellationToken ct)
    {
        if (!File.Exists(chunkPath) || !File.Exists(ChunkDigestPath(chunkPath))) return null;
        try
        {
            var resolvedChunk = FileStorage.ResolveExistingFile(root, chunkPath, ct);
            if ((ulong)new FileInfo(resolvedChunk).Length != expectedBytes) return null;
            var resolvedDigest = FileStorage.ResolveExistingFile(root, ChunkDigestPath(chunkPath), ct);
            var declared = (await File.ReadAllTextAsync(resolvedDigest, ct)).Trim().ToLowerInvariant();
            if (!Sha256Pattern().IsMatch(declared)) return null;
            var actual = await HashFileSha256Async(resolvedChunk, ct);
            return actual.Equals(declared, StringComparison.Ordinal) ? actual : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task WriteChunkDigestAsync(
        string root, string chunkPath, string digest, CancellationToken ct)
    {
        var destination = FileStorage.EnsureLexicallyWithin(root, ChunkDigestPath(chunkPath), false);
        var staging = FileStorage.EnsureLexicallyWithin(root,
            destination + $".{Guid.NewGuid():D}.writing", false);
        var bytes = Encoding.ASCII.GetBytes(digest);
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, destination, overwrite: true);
        }
        finally { TryDeleteFile(staging); }
    }

    internal static string MergeLockName(MySqlConnection conn, string sessionId) =>
        MySqlNamedLock.Name("upload-merge", conn.Database, sessionId);

    private Task TryCleanupSessionArtifactsAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            FileStorage.DeleteDirectoryTree(options.StorageRoot,
                FileStorage.SessionDirectory(FileStorage.Root(options.StorageRoot), sessionId), ct);
        }
        catch
        {
            // The maintenance worker retries the owned session directory.
        }
        return Task.CompletedTask;
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }

    private static void TryDeleteDirectory(string root, string path, CancellationToken ct)
    {
        try { FileStorage.DeleteDirectoryTree(root, path, ct); }
        catch { }
    }
}
