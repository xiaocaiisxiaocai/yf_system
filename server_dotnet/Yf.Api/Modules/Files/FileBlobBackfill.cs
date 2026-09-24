using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Files;

/// <summary>
/// One-time, fail-closed conversion of readable legacy file rows to immutable blobs.
/// Legacy physical files are deliberately retained; only database references move.
/// Run after EF migrations and before the host starts accepting requests.
/// </summary>
internal static class FileBlobBackfill
{
    internal static async Task RunAsync(
        AppDb database, string configuredRoot, CancellationToken ct = default)
    {
        var root = FileStorage.Root(configuredRoot);
        await using var conn = await database.OpenAsync(ct);
        await using var migrationLease = await MySqlNamedLock.TryAcquireAsync(conn,
            MySqlNamedLock.Name("file-blob-backfill", conn.Database), 60, ct)
            ?? throw new InvalidOperationException("Another file blob conversion is running. Retry startup after it completes.");

        while (true)
        {
            LegacyFile? candidate;
            await using (var scan = EfDb.Use(conn))
                candidate = await scan.Files.AsNoTracking()
                    .Where(file => file.BlobId == null && file.Status != "PURGED")
                    .OrderBy(file => file.Id)
                    .Select(file => new LegacyFile(file.Id, file.OriginalName, file.Sha256,
                        file.SizeBytes, file.StoragePath, file.Status))
                    .FirstOrDefaultAsync(ct);
            if (candidate is null) break;
            await ConvertOneAsync(conn, root, candidate, ct);
        }

        await ValidateInvariantAsync(conn, root, ct);
    }

    internal static async Task ValidateInvariantAsync(MySqlConnection conn, string root, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn);
        var unbound = await db.Files.AsNoTracking()
            .Where(file => file.BlobId == null && file.Status != "PURGED")
            .Select(file => new { file.Id, file.Status }).FirstOrDefaultAsync(ct);
        if (unbound is not null)
            throw new InvalidOperationException(
                $"File blob conversion is incomplete: file {unbound.Id} ({unbound.Status}) has no blob reference.");
        var invalid = await (from file in db.Files.AsNoTracking()
            join blob in db.FileBlobs.AsNoTracking() on file.BlobId equals (ulong?)blob.Id
            where file.Status != "PURGED" && (blob.State != FileBlobStates.Ready
                || file.Sha256 != blob.Sha256 || file.SizeBytes != blob.SizeBytes
                || file.StoragePath != blob.StoragePath)
            select new { file.Id, BlobId = blob.Id }).FirstOrDefaultAsync(ct);
        if (invalid is not null)
            throw new InvalidOperationException(
                $"File blob invariant failed: file {invalid.Id} does not match ready blob {invalid.BlobId}.");
        var referenced = await (from blob in db.FileBlobs.AsNoTracking()
            where db.Files.Any(file => file.BlobId == blob.Id && file.Status != "PURGED")
            orderby blob.Id
            select new { blob.Id, blob.Sha256, blob.SizeBytes, blob.StoragePath }).ToArrayAsync(ct);
        foreach (var blob in referenced)
        {
            try
            {
                FileBlobStore.VerifyBoundPhysicalFile(
                    root, blob.StoragePath, blob.Sha256, blob.SizeBytes, ct);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"File blob invariant failed: referenced blob {blob.Id} is missing or invalid.", error);
            }
        }
    }

    private static async Task ConvertOneAsync(
        MySqlConnection conn, string root, LegacyFile candidate, CancellationToken ct)
    {
        string sha256;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate.Sha256))
                throw new InvalidOperationException("SHA-256 is missing");
            sha256 = FileBlobStore.NormalizeSha256(candidate.Sha256);
        }
        catch (Exception error)
        {
            throw ConversionError(candidate, "SHA-256 metadata is invalid", error);
        }

        string sourcePath;
        try
        {
            sourcePath = FileStorage.ResolveExistingFile(root, Path.Combine(root, candidate.StoragePath), ct);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw ConversionError(candidate, "physical content is missing or its path is unsafe", error);
        }

        var stagingDirectory = FileStorage.CreateDirectoryWithin(root,
            FileStorage.EnsureLexicallyWithin(root, Path.Combine(root, "blob-staging", "backfill"), false), ct);
        var stagingPath = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(stagingDirectory, $"{candidate.Id}-{Guid.NewGuid():N}.blob.tmp"), false);
        FileBlobStore.PreparedBlob prepared;
        try
        {
            prepared = await FileBlobStore.CopyAndVerifyAsync(sourcePath, stagingPath, sha256,
                candidate.SizeBytes, reportProgress: null, ct);
        }
        catch (Exception error)
        {
            TryDelete(stagingPath);
            throw ConversionError(candidate, "physical content does not match its recorded size and SHA-256", error);
        }

        try
        {
            await using var blobLease = await FileBlobStore.AcquireAsync(conn, [sha256], ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var db = EfDb.Use(conn, tx);
            var file = await db.Files
                .FromSqlInterpolated($"SELECT * FROM files WHERE id={candidate.Id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (file is null || file.Status == "PURGED" || file.BlobId is not null)
            {
                await tx.CommitAsync(ct);
                return;
            }
            if (!string.Equals(file.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                || file.SizeBytes != candidate.SizeBytes || file.StoragePath != candidate.StoragePath)
                throw ConversionError(candidate, "metadata changed while conversion was in progress");
            var now = await DbClock.UtcNowAsync(db, ct);
            var blob = await FileBlobStore.ResolveForReferenceAsync(
                db, root, prepared, expectedBlobId: null, now, ct);
            file.BlobId = blob.Id;
            file.Sha256 = blob.Sha256;
            file.StoragePath = blob.StoragePath;
            await db.SaveChangesAsync(ct);
            // A lost COMMIT acknowledgement intentionally fails startup. Both the old file and
            // immutable canonical content remain, so the next startup can safely retry.
            await tx.CommitAsync(ct);
        }
        finally { TryDelete(stagingPath); }
    }

    private static InvalidOperationException ConversionError(
        LegacyFile file, string reason, Exception? inner = null) => new(
        $"File blob conversion failed for file {file.Id} ({file.OriginalName}, status {file.Status}): {reason}.", inner);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private sealed record LegacyFile(
        ulong Id, string OriginalName, string? Sha256, ulong SizeBytes, string StoragePath, string Status);
}
