using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Files;

/// <summary>
/// Owns immutable, content-addressed files. Callers must keep the returned SHA leases
/// until the database transaction that adds or removes references has reached a known outcome.
/// </summary>
internal static class FileBlobStore
{
    private const int BufferSize = 1024 * 1024;

    internal sealed record PreparedBlob(string Sha256, ulong SizeBytes, string? StagingPath);

    internal static string RelativePath(string sha256)
    {
        var hash = NormalizeSha256(sha256);
        return $"blobs/sha256/{hash[..2]}/{hash.Substring(2, 2)}/{hash}";
    }

    internal static string AbsolutePath(string root, string sha256) =>
        FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(root, RelativePath(sha256).Replace('/', Path.DirectorySeparatorChar)), allowRoot: false);

    internal static string LockName(MySqlConnection connection, string sha256) =>
        MySqlNamedLock.Name("file-blob", connection.Database, NormalizeSha256(sha256));

    internal static async Task<BlobLeaseSet> AcquireAsync(
        MySqlConnection connection, IEnumerable<string> sha256Values, CancellationToken ct)
    {
        var leases = new List<MySqlNamedLock>();
        try
        {
            foreach (var sha256 in sha256Values.Select(NormalizeSha256)
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var lease = await MySqlNamedLock.TryAcquireAsync(connection,
                    LockName(connection, sha256), 30, ct)
                    ?? throw ApiException.Busy("文件内容正在写入或清理，请稍后重试");
                leases.Add(lease);
            }
            return new BlobLeaseSet(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync();
            throw;
        }
    }

    internal static async Task<PreparedBlob> CopyAndVerifyAsync(
        string sourcePath, string stagingPath, string expectedSha256, ulong expectedBytes,
        Func<ulong, Task>? reportProgress, CancellationToken ct)
    {
        var normalizedHash = NormalizeSha256(expectedSha256);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var interval = Stopwatch.StartNew();
            ulong total = 0;
            ulong lastReported = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) != 0)
            {
                total = checked(total + (uint)read);
                if (total > expectedBytes) throw ApiException.Conflict("源文件大小在复制期间发生变化");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                if (reportProgress is not null && interval.Elapsed >= TimeSpan.FromSeconds(1)
                    && total - lastReported >= 8UL * 1024 * 1024)
                {
                    await reportProgress(total);
                    lastReported = total;
                    interval.Restart();
                }
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            var actualHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (total != expectedBytes || !actualHash.Equals(normalizedHash, StringComparison.Ordinal))
                throw ApiException.Conflict("源文件完整性校验失败，项目未复制");
            return new(normalizedHash, total, stagingPath);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static void VerifyBoundPhysicalFile(string root, string storagePath, string sha256, ulong sizeBytes,
        CancellationToken ct)
    {
        var expectedRelative = RelativePath(sha256);
        if (!storagePath.Equals(expectedRelative, StringComparison.Ordinal))
            throw ApiException.Conflict("文件内容引用路径异常，无法复制");
        string path;
        try { path = FileStorage.ResolveExistingFile(root, AbsolutePath(root, sha256), ct); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        { throw ApiException.Conflict("文件内容缺失或存储路径异常，无法复制"); }
        if ((ulong)new FileInfo(path).Length != sizeBytes)
            throw ApiException.Conflict("文件内容大小校验失败，无法复制");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(NormalizeSha256(sha256), StringComparison.Ordinal))
            throw ApiException.Conflict("文件内容 SHA-256 校验失败，无法复制");
    }

    /// <summary>
    /// Resolves or publishes a blob while its SHA named lock and the caller's transaction are held.
    /// A published canonical file is never removed on an unknown/rolled-back commit.
    /// </summary>
    internal static async Task<FileBlob> ResolveForReferenceAsync(
        YfDbContext db, string root, PreparedBlob prepared, ulong? expectedBlobId,
        DateTime createdAt, CancellationToken ct)
    {
        var sha256 = NormalizeSha256(prepared.Sha256);
        var relativePath = RelativePath(sha256);
        var canonicalPath = AbsolutePath(root, sha256);
        var blob = await db.Set<FileBlob>()
            .FromSqlInterpolated($"SELECT * FROM file_blobs WHERE sha256={sha256} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (expectedBlobId is ulong requiredId && blob?.Id != requiredId)
            throw ApiException.Conflict("文件内容引用已变化，请刷新后重试");
        if (blob is not null)
        {
            if (blob.SizeBytes != prepared.SizeBytes
                || !blob.StoragePath.Equals(relativePath, StringComparison.Ordinal))
                throw new InvalidOperationException("SHA-256 内容记录与存储元数据冲突");
            EnsureCanonicalAvailable(root, canonicalPath, prepared,
                replaceExisting: !string.IsNullOrWhiteSpace(prepared.StagingPath), ct);
            blob.State = FileBlobStates.Ready;
            blob.GarbageCollectionStartedAt = null;
            await db.SaveChangesAsync(ct);
            TryDelete(prepared.StagingPath);
            return blob;
        }

        EnsureCanonicalAvailable(root, canonicalPath, prepared, replaceExisting: true, ct);
        blob = new FileBlob
        {
            Sha256 = sha256,
            SizeBytes = prepared.SizeBytes,
            StoragePath = relativePath,
            State = FileBlobStates.Ready,
            GarbageCollectionStartedAt = null,
            CreatedAt = createdAt,
        };
        db.Add(blob);
        await db.SaveChangesAsync(ct);
        TryDelete(prepared.StagingPath);
        return blob;
    }

    internal static void DeleteCanonicalFile(string root, FileBlob blob, CancellationToken ct)
    {
        var expected = RelativePath(blob.Sha256);
        if (!blob.StoragePath.Equals(expected, StringComparison.Ordinal))
            throw new InvalidOperationException("内容记录不是受管的全局 blob 路径，拒绝清理");
        var path = AbsolutePath(root, blob.Sha256);
        try { path = FileStorage.ResolveExistingFile(root, path, ct); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((ulong)new FileInfo(path).Length != blob.SizeBytes)
            throw new InvalidOperationException("blob 大小与数据库记录不一致，拒绝清理");
        File.Delete(path);
    }

    internal static string NormalizeSha256(string sha256)
    {
        var normalized = sha256.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("SHA-256 格式无效");
        return normalized;
    }

    /// <param name="replaceExisting">
    /// Replace canonical content with the caller's already verified staging file. This repairs unknown
    /// commit or external-corruption remnants without hashing disk content while database locks are held.
    /// </param>
    private static void EnsureCanonicalAvailable(string root, string canonicalPath, PreparedBlob prepared,
        bool replaceExisting, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException("blob 存储目录无效");
        FileStorage.CreateDirectoryWithin(root, directory, ct);
        if (!File.Exists(canonicalPath))
        {
            if (string.IsNullOrWhiteSpace(prepared.StagingPath) || !File.Exists(prepared.StagingPath))
                throw ApiException.Conflict("文件内容缺失，无法建立引用");
            try
            {
                File.Move(prepared.StagingPath, canonicalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(canonicalPath))
            {
                if (replaceExisting)
                    File.Move(prepared.StagingPath, canonicalPath, overwrite: true);
            }
        }
        else if (replaceExisting)
        {
            if (string.IsNullOrWhiteSpace(prepared.StagingPath) || !File.Exists(prepared.StagingPath))
                throw ApiException.Conflict("发现已有文件内容，但缺少已校验暂存文件，无法建立引用");
            File.Move(prepared.StagingPath, canonicalPath, overwrite: true);
        }

        var resolved = FileStorage.ResolveExistingFile(root, canonicalPath, ct);
        if ((ulong)new FileInfo(resolved).Length != prepared.SizeBytes)
            throw new InvalidOperationException("全局 blob 路径存在不同大小的内容");
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal sealed class BlobLeaseSet(IReadOnlyList<MySqlNamedLock> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync();
        }
    }
}
