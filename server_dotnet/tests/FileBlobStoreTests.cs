using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

// MigratedTestDatabase changes process-wide bootstrap state, so it must not run beside other DB tests.
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class FileBlobStoreTests
{
    [Fact(Timeout = 60_000)]
    public async Task FreshlyStagedContentReplacesUnregisteredCanonicalButNeverOverwritesRegisteredSameSizeContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var root = Path.Combine(Path.GetTempPath(), "yf-blob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var conn = await database.Database.OpenAsync(ct);

            // New content: the caller already hashed its staging file, which is moved into place.
            var content = new byte[] { 1, 2, 3, 4 };
            var sha = Sha256Hex(content);
            var staging = Path.Combine(root, "staging-1.tmp");
            await File.WriteAllBytesAsync(staging, content, ct);
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            await using (var db = EfDb.Use(conn, tx))
            {
                var blob = await FileBlobStore.ResolveForReferenceAsync(db, root,
                    new FileBlobStore.PreparedBlob(sha, (ulong)content.Length, staging), null, DateTime.UtcNow, ct);
                Assert.Equal(sha, blob.Sha256);
                await tx.CommitAsync(ct);
            }
            Assert.False(File.Exists(staging));
            var canonical = FileBlobStore.AbsolutePath(root, sha);
            Assert.Equal(content, await File.ReadAllBytesAsync(canonical, ct));

            // A registered blob is immutable and may be streaming: a same-size canonical file is never
            // overwritten by a duplicate upload (even one held open by a reader), the staging copy is dropped.
            await File.WriteAllBytesAsync(canonical, [4, 3, 2, 1], ct);
            var corrupt = Assert.Throws<ApiException>(() =>
                FileBlobStore.VerifyBoundPhysicalFile(root, FileBlobStore.RelativePath(sha), sha,
                    (ulong)content.Length, ct));
            Assert.Equal(409, corrupt.Status);
            var duplicateStaging = Path.Combine(root, "staging-duplicate.tmp");
            await File.WriteAllBytesAsync(duplicateStaging, content, ct);
            await using (var reader = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            await using (var db = EfDb.Use(conn, tx))
            {
                await FileBlobStore.ResolveForReferenceAsync(db, root,
                    new FileBlobStore.PreparedBlob(sha, (ulong)content.Length, duplicateStaging),
                    expectedBlobId: null, DateTime.UtcNow, ct);
                await tx.CommitAsync(ct);
            }
            Assert.False(File.Exists(duplicateStaging));
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(canonical, ct));

            // A size mismatch is external damage and is repaired from the verified staging file.
            await File.WriteAllBytesAsync(canonical, [7, 7], ct);
            var repairStaging = Path.Combine(root, "staging-repair.tmp");
            await File.WriteAllBytesAsync(repairStaging, content, ct);
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            await using (var db = EfDb.Use(conn, tx))
            {
                await FileBlobStore.ResolveForReferenceAsync(db, root,
                    new FileBlobStore.PreparedBlob(sha, (ulong)content.Length, repairStaging),
                    expectedBlobId: null, DateTime.UtcNow, ct);
                await tx.CommitAsync(ct);
            }
            Assert.False(File.Exists(repairStaging));
            Assert.Equal(content, await File.ReadAllBytesAsync(canonical, ct));

            if (OperatingSystem.IsWindows())
            {
                // A reader without FILE_SHARE_DELETE blocks the repair: retried briefly, then a 409
                // conflict; the verified staging file is left for the caller to clean up or retry.
                await File.WriteAllBytesAsync(canonical, [7, 7], ct);
                var blockedStaging = Path.Combine(root, "staging-blocked.tmp");
                await File.WriteAllBytesAsync(blockedStaging, content, ct);
                await using (var reader = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
                await using (var db = EfDb.Use(conn, tx))
                {
                    var busy = await Assert.ThrowsAsync<ApiException>(() => FileBlobStore.ResolveForReferenceAsync(db, root,
                        new FileBlobStore.PreparedBlob(sha, (ulong)content.Length, blockedStaging),
                        expectedBlobId: null, DateTime.UtcNow, ct));
                    Assert.Equal(409, busy.Status);
                    Assert.Equal(40901, busy.Code);
                }
                Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(canonical, ct));

                // The service's own readers share delete access, so they never block a repair.
                await using (var reader = new FileStream(canonical, FileMode.Open, FileAccess.Read,
                                 FileShare.Read | FileShare.Delete))
                await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
                await using (var db = EfDb.Use(conn, tx))
                {
                    await FileBlobStore.ResolveForReferenceAsync(db, root,
                        new FileBlobStore.PreparedBlob(sha, (ulong)content.Length, blockedStaging),
                        expectedBlobId: null, DateTime.UtcNow, ct);
                    await tx.CommitAsync(ct);
                    // The open reader keeps streaming the previous content it opened.
                    var old = new byte[2];
                    Assert.Equal(2, await reader.ReadAsync(old, ct));
                    Assert.Equal(new byte[] { 7, 7 }, old);
                }
                Assert.Equal(content, await File.ReadAllBytesAsync(canonical, ct));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(canonical)!, "*.replaced-*"));
            }

            // A canonical file left without a database row (unknown commit outcome) is not trusted blindly.
            var claimed = new byte[] { 9, 8, 7, 6 };
            var claimedSha = Sha256Hex(claimed);
            var orphan = FileBlobStore.AbsolutePath(root, claimedSha);
            Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
            await File.WriteAllBytesAsync(orphan, [0, 0, 0, 0], ct);
            var secondStaging = Path.Combine(root, "staging-2.tmp");
            await File.WriteAllBytesAsync(secondStaging, claimed, ct);
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            await using (var db = EfDb.Use(conn, tx))
            {
                var blob = await FileBlobStore.ResolveForReferenceAsync(db, root,
                    new FileBlobStore.PreparedBlob(claimedSha, (ulong)claimed.Length, secondStaging), null, DateTime.UtcNow, ct);
                Assert.Equal(claimedSha, blob.Sha256);
                await tx.CommitAsync(ct);
            }
            await using (var db = EfDb.Use(conn))
                Assert.True(await db.Set<FileBlob>().AnyAsync(blob => blob.Sha256 == claimedSha, ct));
            Assert.Equal(claimed, await File.ReadAllBytesAsync(orphan, ct));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StartupRequiresExplicitConversionAndLegacyCleanupKeepsReferencedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var root = Path.Combine(Path.GetTempPath(), "yf-blob-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = "legacy-content"u8.ToArray();
            var sha = Sha256Hex(content);
            const string legacyRelative = "files/2026/09/legacy.txt";
            await WriteAsync(root, legacyRelative, content, ct);
            await WriteAsync(root, "files/2026/08/stray.bin", [1, 2, 3], ct);
            await WriteAsync(root, "message-images/2026/09/keep.png", [4, 5], ct);
            await database.ExecuteAsync($"""
                INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'S','ACTIVE',1);
                INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id) VALUES(1,'G',100,'IN_PROGRESS',1,1);
                INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
                  VALUES(1,1,'P',100,'IN_PROGRESS',1,1);
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,created_at)
                  VALUES(501,1,1,'C2S','legacy.txt','legacy.txt','txt',{content.Length},'text/plain','{sha}','{legacyRelative}','AVAILABLE',UTC_TIMESTAMP(3));
                """, null, ct);

            // Normal startup never copies content; it refuses to serve until the maintenance command ran.
            var gate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FileBlobBackfill.EnsureConvertedAsync(database.Database, root, ct));
            Assert.Contains("--convert-file-blobs", gate.Message, StringComparison.Ordinal);
            Assert.Contains("501", gate.Message, StringComparison.Ordinal);

            Assert.Equal(1, await FileBlobBackfill.RunAsync(database.Database, root, ct));
            Assert.Equal(0, await FileBlobBackfill.RunAsync(database.Database, root, ct));
            await FileBlobBackfill.EnsureConvertedAsync(database.Database, root, ct);
            Assert.True(File.Exists(Path.Combine(root, legacyRelative)));

            // Startup checks relational integrity only; the maintenance verifier owns physical scans.
            var canonical = FileBlobStore.AbsolutePath(root, sha);
            File.Delete(canonical);
            await FileBlobBackfill.EnsureConvertedAsync(database.Database, root, ct);
            await using (var conn = await database.Database.OpenAsync(ct))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    FileBlobBackfill.ValidateInvariantAsync(conn, FileStorage.Root(root), ct));
            await WriteAsync(root, FileBlobStore.RelativePath(sha), content, ct);

            var removed = await FileBlobBackfill.RemoveLegacyContentAsync(database.Database, root, ct);
            Assert.Equal(2, removed.Files);
            Assert.Equal((ulong)(content.Length + 3), removed.Bytes);
            Assert.False(Directory.Exists(Path.Combine(root, "files", "2026")));
            Assert.Equal(content, await File.ReadAllBytesAsync(FileBlobStore.AbsolutePath(root, sha), ct));
            Assert.True(File.Exists(Path.Combine(root, "message-images", "2026", "09", "keep.png")));
            await FileBlobBackfill.EnsureConvertedAsync(database.Database, root, ct);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task WriteAsync(string root, string relative, byte[] content, CancellationToken ct)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    private static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
