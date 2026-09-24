using MySqlConnector;
using System.Buffers;
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
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            ulong total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 1024 * 1024), ct)) != 0)
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
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string ChunkDigestPath(string chunkPath) => chunkPath + ".sha256";

    private static async Task<string> HashFileSha256Async(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, 1024 * 1024), ct)) != 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Returns the verified digest of a stored chunk. The sidecar records the digest together with the
    /// chunk's length and last-write time captured when the server hashed the upload itself; while both
    /// still match, the digest is reused without rereading the chunk. Any other state (older sidecar
    /// format or rewritten chunk is treated as missing by status/resume checks. Merge is the only path
    /// that rereads such chunks, and repairs the sidecar only after the content digest is verified.
    /// </summary>
    private async Task<string?> ValidChunkDigestAsync(
        string root, string chunkPath, ulong expectedBytes, bool repairMetadata, CancellationToken ct)
    {
        if (!File.Exists(chunkPath) || !File.Exists(ChunkDigestPath(chunkPath))) return null;
        try
        {
            var resolvedChunk = FileStorage.ResolveExistingFile(root, chunkPath, ct);
            var chunk = new FileInfo(resolvedChunk);
            if ((ulong)chunk.Length != expectedBytes) return null;
            var resolvedDigest = FileStorage.ResolveExistingFile(root, ChunkDigestPath(chunkPath), ct);
            var record = (await File.ReadAllTextAsync(resolvedDigest, ct)).Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (record.Length is not (1 or 3) || !Sha256Pattern().IsMatch(record[0])) return null;
            var declared = record[0];
            if (record.Length == 3
                && ulong.TryParse(record[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var recordedLength)
                && long.TryParse(record[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var recordedTicks)
                && recordedLength == (ulong)chunk.Length
                && recordedTicks == chunk.LastWriteTimeUtc.Ticks)
                return declared;
            if (!repairMetadata) return null;
            var actual = await HashFileSha256Async(resolvedChunk, ct);
            if (!actual.Equals(declared, StringComparison.Ordinal)) return null;
            await WriteChunkDigestAsync(root, chunkPath, actual, ct);
            return actual;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task WriteChunkDigestAsync(
        string root, string chunkPath, string digest, CancellationToken ct)
    {
        var destination = FileStorage.EnsureLexicallyWithin(root, ChunkDigestPath(chunkPath), false);
        var staging = FileStorage.EnsureLexicallyWithin(root,
            destination + $".{Guid.NewGuid():D}.writing", false);
        // Bind the digest to the exact chunk file state this server hashed (see ValidChunkDigestAsync).
        var chunk = new FileInfo(FileStorage.ResolveExistingFile(root, chunkPath, ct));
        var bytes = Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{digest} {chunk.Length} {chunk.LastWriteTimeUtc.Ticks}"));
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
        finally { TryDeleteFile(staging, "chunk-sidecar-staging"); }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // The maintenance worker retries the owned session directory.
            logger.LogWarning("清理上传会话临时目录失败 {SessionId} {Failure}",
                sessionId, SafeFailureCode(error));
        }
        return Task.CompletedTask;
    }

    private void TryDeleteFile(string path, string operation)
    {
        try { File.Delete(path); }
        catch (Exception error)
        {
            logger.LogWarning("清理文件模块临时文件失败 {Operation} {Failure}",
                operation, SafeFailureCode(error));
        }
    }

    private void TryDeleteDirectory(string root, string path, string sessionId, CancellationToken ct)
    {
        try { FileStorage.DeleteDirectoryTree(root, path, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("回滚上传初始化目录失败 {SessionId} {Failure}",
                sessionId, SafeFailureCode(error));
        }
    }
}
