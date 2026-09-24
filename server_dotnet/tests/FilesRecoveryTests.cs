using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using System.Security.Cryptography;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class FilesRecoveryTests
{
    [Fact]
    public void SessionCommitConfirmationControlsSuccessAndDirectoryCleanup()
    {
        Assert.Equal(new SessionCommitRecoveryDecision(true, false),
            UploadService.SessionCommitRecovery(true));
        Assert.Equal(new SessionCommitRecoveryDecision(false, false),
            UploadService.SessionCommitRecovery(false));
        Assert.Equal(new SessionCommitRecoveryDecision(false, false),
            UploadService.SessionCommitRecovery(null));
    }

    [Fact(Timeout = 30_000)]
    public async Task GarbageCollectionDoesNotResetALiveMergeButRecoversAfterConnectionLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", expired: false);
        Assert.False(await scope.IsExpiredByDatabaseAsync(sessionId));

        await using var owner = await scope.Database.OpenAsync(ct);
        await using var lease = await MySqlNamedLock.TryAcquireAsync(
            owner, UploadService.MergeLockName(owner, sessionId), 0, ct);
        Assert.NotNull(lease);

        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        // Simulate an IIS worker/database connection loss. Do not release the
        // named lease in application code: MySQL must release connection-owned
        // locks when the owning session disappears.
        await scope.KillConnectionAsync(owner.ServerThread, ct);
        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("UPLOADING", await scope.StatusAsync(sessionId));
        Assert.True(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 30_000)]
    public async Task ChunkBodyIsReceivedWithoutHoldingManagementOrProjectRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "UPLOADING", expired: false);
        Assert.False(await scope.IsExpiredByDatabaseAsync(sessionId));
        var body = new BlockingOneByteStream();
        var context = scope.ChunkContext([1]);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var put = scope.Upload.PutChunkAsync(context, sessionId, 0, body, operation.Token);
        var released = false;

        try
        {
            var entered = body.WaitUntilReadAsync(operation.Token);
            var first = await Task.WhenAny(entered, put).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (first == put) await put; // Surface an early pre-read failure.
            await entered;

            using var lockProbe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lockProbe.CancelAfter(TimeSpan.FromSeconds(3));
            await using var observer = await scope.Database.OpenAsync(lockProbe.Token);
            await using var tx = await AppDb.BeginTransactionAsync(observer, lockProbe.Token);
            Assert.Equal("security.management_lock", await observer.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT cfg_key FROM system_configs WHERE cfg_key='security.management_lock' FOR UPDATE",
                    transaction: tx, cancellationToken: lockProbe.Token)));
            Assert.Equal(1UL, await observer.QuerySingleAsync<ulong>(new CommandDefinition(
                "SELECT id FROM projects WHERE id=1 FOR UPDATE", transaction: tx,
                cancellationToken: lockProbe.Token)));
            Assert.Equal(1UL, await observer.QuerySingleAsync<ulong>(new CommandDefinition(
                "SELECT id FROM project_groups WHERE id=1 FOR UPDATE", transaction: tx,
                cancellationToken: lockProbe.Token)));
            await tx.CommitAsync(lockProbe.Token);

            body.Release();
            released = true;
            await put.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            if (!released) body.Release();
            if (!put.IsCompleted)
            {
                operation.Cancel();
                try { await put.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch { }
            }
        }

        Assert.Equal([1], await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, sessionId, 0), ct));
        var uploaded = Assert.Single((await scope.Upload.GetAsync(
            scope.Context(), sessionId, ct)).UploadedChunks);
        Assert.Equal(0U, uploaded.Index);
        Assert.Equal(Sha256Hex([1]), uploaded.Sha256);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentIdenticalFingerprintInitializationReturnsOneSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var request = UploadRequest("same.bin", [1]);

        var calls = await Task.WhenAll(
            scope.Upload.InitAsync(scope.Context(), request, ct),
            scope.Upload.InitAsync(scope.Context(), request, ct));
        var ids = calls.Select(result => result.SessionId).ToArray();

        Assert.NotNull(ids[0]);
        Assert.Equal(ids[0], ids[1]);
        Assert.Single(calls, response => response.Resumed is null);
        Assert.Single(calls, response => response.Resumed is true);
        Assert.All(calls, response => Assert.Empty(response.UploadedChunks));
        await using var connection = await scope.Database.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM upload_sessions
            WHERE project_id=1 AND uploader_id=1 AND file_name='same.bin'
              AND file_size=1 AND file_last_modified=@FileLastModified
              AND file_fingerprint=@FileFingerprint AND file_md5 IS NULL
            """, new { request.FileLastModified, request.FileFingerprint }, cancellationToken: ct)));
        var timing = await scope.TimingAsync(ids[0]!);
        Assert.True(timing.CreatedEqualsUpdated);
        Assert.True(timing.HasTwentyFourHourLifetime);
        Assert.True(timing.IsActive);
    }

    [Fact(Timeout = 30_000)]
    public async Task ResumeReportsVerifiedChunkDigestsAndRejectsCorruptReplacement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var original = new byte[] { 1 };
        var replacement = new byte[] { 2 };
        var request = UploadRequest("resume.bin", original);
        var initialized = await scope.Upload.InitAsync(scope.Context(), request, ct);

        using (var body = new MemoryStream(original))
            await scope.Upload.PutChunkAsync(scope.ChunkContext(original), initialized.SessionId, 0, body, ct);

        using (var body = new MemoryStream(replacement))
        {
            var mismatched = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.PutChunkAsync(
                scope.ChunkContext(replacement, Sha256Hex(original)), initialized.SessionId, 0, body, ct));
            Assert.Equal(400, mismatched.Status);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0), ct));

        var resumed = await scope.Upload.InitAsync(scope.Context(), request, ct);
        Assert.Equal(initialized.SessionId, resumed.SessionId);
        Assert.True(resumed.Resumed);
        var originalDigest = Assert.Single(resumed.UploadedChunks);
        Assert.Equal(0U, originalDigest.Index);
        Assert.Equal(Sha256Hex(original), originalDigest.Sha256);

        using (var body = new MemoryStream(replacement))
            await scope.Upload.PutChunkAsync(scope.ChunkContext(replacement), initialized.SessionId, 0, body, ct);
        var replacedDigest = Assert.Single((await scope.Upload.GetAsync(
            scope.Context(), initialized.SessionId, ct)).UploadedChunks);
        Assert.Equal(Sha256Hex(replacement), replacedDigest.Sha256);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0), ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task SameLengthChunkTamperingIsExcludedFromGetAndResume()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var original = new byte[] { 1, 2, 3, 4 };
        var tampered = new byte[] { 4, 3, 2, 1 };
        var request = UploadRequest("tampered.bin", original);
        var initialized = await scope.Upload.InitAsync(scope.Context(), request, ct);
        using (var body = new MemoryStream(original))
            await scope.Upload.PutChunkAsync(scope.ChunkContext(original), initialized.SessionId, 0, body, ct);

        var chunkPath = FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0);
        var originalSidecar = await File.ReadAllTextAsync(chunkPath + ".sha256", ct);
        await File.WriteAllBytesAsync(chunkPath, tampered, ct);

        Assert.Equal(Sha256Hex(original), SidecarDigest(originalSidecar));
        Assert.Empty((await scope.Upload.GetAsync(
            scope.Context(), initialized.SessionId, ct)).UploadedChunks);
        var resumed = await scope.Upload.InitAsync(scope.Context(), request, ct);
        Assert.Equal(initialized.SessionId, resumed.SessionId);
        Assert.True(resumed.Resumed);
        Assert.Empty(resumed.UploadedChunks);
    }

    [Fact(Timeout = 30_000)]
    public async Task ChunkSidecarBindsDigestToFileStateAndLegacyDigestsAreDeferredUntilMerge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var content = new byte[] { 5, 6, 7, 8 };
        var request = UploadRequest("sidecar.bin", content);
        var initialized = await scope.Upload.InitAsync(scope.Context(), request, ct);
        using (var body = new MemoryStream(content))
            await scope.Upload.PutChunkAsync(scope.ChunkContext(content), initialized.SessionId, 0, body, ct);

        var chunkPath = FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0);
        var fields = (await File.ReadAllTextAsync(chunkPath + ".sha256", ct)).Trim().Split(' ');
        var chunk = new FileInfo(chunkPath);
        Assert.Equal([Sha256Hex(content), chunk.Length.ToString(), chunk.LastWriteTimeUtc.Ticks.ToString()], fields);

        // Status polling never rereads a legacy chunk. Merge still hashes it and accepts valid content.
        await File.WriteAllTextAsync(chunkPath + ".sha256", Sha256Hex(content), ct);
        Assert.Empty((await scope.Upload.GetAsync(scope.Context(), initialized.SessionId, ct)).UploadedChunks);
        await scope.Upload.SubmitMd5Async(scope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(Md5Hex(content)), ct);
        var merged = await scope.Upload.MergeAsync(scope.Context(), initialized.SessionId, ct);
        Assert.Equal(Sha256Hex(content), merged.Sha256);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentOverlappingChunkReplacementsLeaveOneConsistentPayloadAndDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var firstContent = Enumerable.Repeat((byte)0x11, 4096).ToArray();
        var secondContent = Enumerable.Repeat((byte)0x22, 4096).ToArray();
        var request = UploadRequest("overlap.bin", firstContent);
        var initialized = await scope.Upload.InitAsync(scope.Context(), request, ct);
        await using var firstBody = new GatedPayloadStream(firstContent);
        await using var secondBody = new GatedPayloadStream(secondContent);
        var firstPut = scope.Upload.PutChunkAsync(
            scope.ChunkContext(firstContent), initialized.SessionId, 0, firstBody, ct);
        var secondPut = scope.Upload.PutChunkAsync(
            scope.ChunkContext(secondContent), initialized.SessionId, 0, secondBody, ct);
        var puts = Task.WhenAll(firstPut, secondPut);

        try
        {
            var bothEntered = Task.WhenAll(
                firstBody.WaitUntilReadAsync(ct), secondBody.WaitUntilReadAsync(ct));
            var first = await Task.WhenAny(bothEntered, puts).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (first == puts) await puts;
            await bothEntered;
        }
        finally
        {
            firstBody.Release();
            secondBody.Release();
        }
        await puts.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var chunkPath = FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0);
        var actual = await File.ReadAllBytesAsync(chunkPath, ct);
        Assert.True(actual.SequenceEqual(firstContent) || actual.SequenceEqual(secondContent));
        var actualSha256 = Sha256Hex(actual);
        Assert.Equal(actualSha256, SidecarDigest(await File.ReadAllTextAsync(chunkPath + ".sha256", ct)));
        var uploaded = Assert.Single((await scope.Upload.GetAsync(
            scope.Context(), initialized.SessionId, ct)).UploadedChunks);
        Assert.Equal(0U, uploaded.Index);
        Assert.Equal(actualSha256, uploaded.Sha256);

        await scope.Upload.SubmitMd5Async(scope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(Md5Hex(actual)), ct);
        Assert.Equal(Md5Hex(actual), await scope.FileMd5Async(initialized.SessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task GetUsesDatabaseExpiryForActiveAndExpiredSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var activeId = Guid.NewGuid().ToString("D");
        var expiredId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(activeId, "UPLOADING", expired: false);
        await scope.InsertSessionAsync(expiredId, "UPLOADING", expired: true);

        Assert.False(await scope.IsExpiredByDatabaseAsync(activeId));
        Assert.True(await scope.IsExpiredByDatabaseAsync(expiredId));
        await scope.Upload.GetAsync(scope.Context(), activeId, ct);
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.GetAsync(scope.Context(), expiredId, ct));
        Assert.Equal(409, expired.Status);
    }

    [Fact(Timeout = 30_000)]
    public async Task ChunkUsesDatabaseExpiryForActiveAndExpiredSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var activeId = Guid.NewGuid().ToString("D");
        var expiredId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(activeId, "UPLOADING", expired: false);
        await scope.InsertSessionAsync(expiredId, "UPLOADING", expired: true);

        Assert.False(await scope.IsExpiredByDatabaseAsync(activeId));
        Assert.True(await scope.IsExpiredByDatabaseAsync(expiredId));
        using var activeBody = new MemoryStream([1]);
        await scope.Upload.PutChunkAsync(scope.ChunkContext([1]), activeId, 0,
            activeBody, ct);
        using var expiredBody = new MemoryStream([1]);
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.PutChunkAsync(scope.ChunkContext([1]), expiredId, 0,
                expiredBody, ct));
        Assert.Equal(409, expired.Status);
        Assert.True(File.Exists(FileStorage.ChunkPath(scope.StorageRoot, activeId, 0)));
        Assert.False(File.Exists(FileStorage.ChunkPath(scope.StorageRoot, expiredId, 0)));
    }

    [Fact(Timeout = 30_000)]
    public async Task MergeUsesDatabaseExpiryForActiveAndExpiredSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var activeId = Guid.NewGuid().ToString("D");
        var expiredId = Guid.NewGuid().ToString("D");
        var expiredMergingId = Guid.NewGuid().ToString("D");
        var fileMd5 = Md5Hex([1]);
        await scope.InsertSessionAsync(activeId, "UPLOADING", expired: false, fileMd5);
        await scope.InsertSessionAsync(expiredId, "UPLOADING", expired: true, fileMd5);
        await scope.InsertSessionAsync(expiredMergingId, "MERGING", expired: true, fileMd5);

        Assert.False(await scope.IsExpiredByDatabaseAsync(activeId));
        Assert.True(await scope.IsExpiredByDatabaseAsync(expiredId));
        Assert.True(await scope.IsExpiredByDatabaseAsync(expiredMergingId));
        var incomplete = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), activeId, ct));
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), expiredId, ct));
        var expiredMerging = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), expiredMergingId, ct));
        Assert.Equal(400, incomplete.Status);
        Assert.Equal(409, expired.Status);
        Assert.Equal(409, expiredMerging.Status);
        Assert.Equal("UPLOADING", await scope.StatusAsync(activeId));
        Assert.Equal("UPLOADING", await scope.StatusAsync(expiredId));
        Assert.Equal("MERGING", await scope.StatusAsync(expiredMergingId));
    }

    [Fact(Timeout = 30_000)]
    public async Task Md5LessAbandonedMergeRequiresDigestThenCanBeAborted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", expired: false);
        Assert.False(await scope.IsExpiredByDatabaseAsync(sessionId));

        var incomplete = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), sessionId, ct));
        Assert.Equal(409, incomplete.Status);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        await scope.Upload.AbortAsync(scope.Context(), sessionId, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(sessionId));
        Assert.False(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 30_000)]
    public async Task MergeRejectsSubmittedMd5ThatDoesNotMatchUploadedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var payload = new byte[] { 1 };
        var initialized = await scope.Upload.InitAsync(
            scope.Context(), UploadRequest("wrong-md5.bin", payload), ct);
        using (var body = new MemoryStream(payload))
            await scope.Upload.PutChunkAsync(scope.ChunkContext(payload), initialized.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(scope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(new string('0', 32)), ct);

        var mismatch = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(scope.Context(), initialized.SessionId, ct));

        Assert.Equal(400, mismatch.Status);
        Assert.Equal("UPLOADING", await scope.StatusAsync(initialized.SessionId));
        Assert.Equal(payload, await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0), ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task ExpiredAbandonedMergeRemovesOnlyItsOwnedSessionDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", expired: true);
        Assert.True(await scope.IsExpiredByDatabaseAsync(sessionId));

        var sessionDirectory = FileStorage.SessionDirectory(scope.StorageRoot, sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "interrupted.part"), "partial", ct);
        var outside = Path.Combine(Path.GetDirectoryName(scope.StorageRoot)!,
            $"outside-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(outside, [2], ct);
            await scope.Maintenance.RunGarbageCollectionAsync(ct);

            Assert.Equal("EXPIRED", await scope.StatusAsync(sessionId));
            Assert.Equal([2], await File.ReadAllBytesAsync(outside, ct));
            Assert.False(Directory.Exists(sessionDirectory));
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact(Timeout = 30_000)]
    public async Task OrphanUploadCleanupDeletesOnlyOldGuidSessionDirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var temp = Path.Combine(scope.StorageRoot, "tmp");
        var orphanSession = Path.Combine(temp, Guid.NewGuid().ToString("D"));
        var unknownDirectory = Path.Combine(temp, "private-maintenance-data");
        Directory.CreateDirectory(orphanSession);
        Directory.CreateDirectory(unknownDirectory);
        await File.WriteAllTextAsync(Path.Combine(orphanSession, "partial.bin"), "orphan", ct);
        await File.WriteAllTextAsync(Path.Combine(unknownDirectory, "keep.bin"), "private", ct);
        var old = DateTime.UtcNow.AddHours(-25);
        Directory.SetLastWriteTimeUtc(orphanSession, old);
        Directory.SetLastWriteTimeUtc(unknownDirectory, old);

        await scope.Maintenance.RunGarbageCollectionAsync(ct);

        Assert.False(Directory.Exists(orphanSession));
        Assert.True(File.Exists(Path.Combine(unknownDirectory, "keep.bin")));
    }

    [Fact(Timeout = 30_000)]
    public async Task OrphanBlobCollectionUsesGracePeriodAndKeepsRegisteredContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var orphanContent = new byte[] { 10, 20, 30 };
        var registeredContent = new byte[] { 40, 50, 60 };
        var orphanSha = Sha256Hex(orphanContent);
        var registeredSha = Sha256Hex(registeredContent);
        var orphanPath = FileBlobStore.AbsolutePath(scope.StorageRoot, orphanSha);
        var registeredPath = FileBlobStore.AbsolutePath(scope.StorageRoot, registeredSha);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(registeredPath)!);
        await File.WriteAllBytesAsync(orphanPath, orphanContent, ct);
        await File.WriteAllBytesAsync(registeredPath, registeredContent, ct);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddHours(-25));
        File.SetLastWriteTimeUtc(registeredPath, DateTime.UtcNow.AddHours(-25));
        await using (var connection = await scope.Database.OpenAsync(ct))
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO file_blobs(sha256,size_bytes,storage_path,state,created_at)
                VALUES(@Sha,@Size,@Path,'READY',UTC_TIMESTAMP(6))
                """, new
                {
                    Sha = registeredSha,
                    Size = (ulong)registeredContent.Length,
                    Path = FileBlobStore.RelativePath(registeredSha)
                }, cancellationToken: ct));

        await scope.Maintenance.PurgeOrphanBlobFilesAsync(ct);

        Assert.False(File.Exists(orphanPath));
        Assert.True(File.Exists(registeredPath));
    }

    [Fact(Timeout = 30_000)]
    public async Task BatchDownloadChecksProjectScopeBeforeRevealingUnavailableFileName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await using (var connection = await scope.Database.OpenAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO users(id,employee_no,user_type,supplier_id,status,must_change_password,email,real_name)
                VALUES(2,'T002','INTERNAL',NULL,'ACTIVE',FALSE,NULL,'Outsider');
                INSERT INTO user_roles(user_id,role_id) VALUES(2,1);
                INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,
                                  mime_type,sha256,storage_path,status,deleted_at,created_at)
                VALUES(1,1,'C2S','private-file-name.pdf','stored.pdf','pdf',1,'application/pdf',NULL,
                       'files/2026/09/stored.pdf','DELETED',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, cancellationToken: ct));
        }

        var context = new DefaultHttpContext();
        context.Items[typeof(CurrentUser)] = new CurrentUser(2, "T002", "INTERNAL", null);
        var error = await Assert.ThrowsAsync<ApiException>(() => scope.Files.BatchDownloadAsync(
            context, new BatchDownloadRequest([1]), ct));

        Assert.Equal(404, error.Status);
        Assert.Equal(40401, error.Code);
        Assert.DoesNotContain("private-file-name.pdf", error.Message, StringComparison.Ordinal);
    }

    private static InitUploadRequest UploadRequest(string fileName, byte[] content) =>
        new(1, fileName, (ulong)content.LongLength, 1_700_000_000_000, Sha256Hex(content));

    private static string SidecarDigest(string sidecar) => sidecar.Trim().Split(' ')[0];

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string Md5Hex(byte[] content) =>
        Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

    private sealed class FilesDatabaseScope : IAsyncDisposable
    {
        private readonly MySqlConnection administration;
        private readonly string databaseName;
        private readonly string disposableRoot;

        private FilesDatabaseScope(
            MySqlConnection administration,
            string databaseName,
            string disposableRoot,
            AppDb database,
            FilesMaintenanceService maintenance,
            UploadService upload,
            FileService files)
        {
            this.administration = administration;
            this.databaseName = databaseName;
            this.disposableRoot = disposableRoot;
            Database = database;
            Maintenance = maintenance;
            Upload = upload;
            Files = files;
            StorageRoot = Path.Combine(disposableRoot, "storage");
        }

        public AppDb Database { get; }
        public FilesMaintenanceService Maintenance { get; }
        public UploadService Upload { get; }
        public FileService Files { get; }
        public string StorageRoot { get; }

        public static async Task<FilesDatabaseScope> CreateOrSkipAsync(CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("File recovery tests only allow local MySQL");

            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
                Password = uri.UserInfo.Contains(':')
                    ? Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1])
                    : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = "yf_test_dotnet_files_" + Guid.NewGuid().ToString("N");
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var appBuilder = new MySqlConnectionStringBuilder(adminOptions.ConnectionString) { Database = databaseName };
                var disposableRoot = Path.Combine(Path.GetTempPath(), "yf_dotnet_files_" + Guid.NewGuid().ToString("N"));
                var storageRoot = Path.Combine(disposableRoot, "storage");
                Directory.CreateDirectory(storageRoot);
                var options = new AppOptions
                {
                    ConnectionString = appBuilder.ConnectionString,
                    StorageRoot = storageRoot,
                    WorkerEnabled = false,
                    JwtSecret = "files-recovery-test-secret-at-least-32-bytes"
                };
                var database = new AppDb(options);
                await using (var connection = await database.OpenAsync(ct))
                {
                    await connection.ExecuteAsync(new CommandDefinition("""
                        CREATE TABLE system_configs(
                            cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL
                        );
                        CREATE TABLE users(
                            id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(50) NOT NULL,
                            user_type VARCHAR(20) NOT NULL, supplier_id BIGINT UNSIGNED NULL,
                            status VARCHAR(20) NOT NULL, must_change_password BOOLEAN NOT NULL,
                            email VARCHAR(255) NULL, real_name VARCHAR(100) NOT NULL,
                            password_hash VARCHAR(255) NOT NULL DEFAULT '', department_id BIGINT UNSIGNED NULL,
                            last_login_at DATETIME(6) NULL, created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                        );
                        CREATE TABLE roles(
                            id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(100) NOT NULL,
                            status VARCHAR(20) NOT NULL, is_built_in BOOLEAN NOT NULL
                        );
                        CREATE TABLE permissions(
                            id BIGINT UNSIGNED PRIMARY KEY,
                            code VARCHAR(100) NOT NULL,
                            type VARCHAR(16) NOT NULL DEFAULT 'BUTTON',
                            sort_no INT NOT NULL DEFAULT 0
                        );
                        CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
                        CREATE TABLE role_permissions(role_id BIGINT UNSIGNED NOT NULL, permission_id BIGINT UNSIGNED NOT NULL);
                        CREATE TABLE project_groups(id BIGINT UNSIGNED PRIMARY KEY, created_by BIGINT UNSIGNED NOT NULL DEFAULT 0);
                        CREATE TABLE projects(
                            id BIGINT UNSIGNED PRIMARY KEY, project_group_id BIGINT UNSIGNED NOT NULL,
                            supplier_id BIGINT UNSIGNED NOT NULL,
                            created_by BIGINT UNSIGNED NOT NULL, status VARCHAR(30) NOT NULL,
                            confirm_side VARCHAR(20) NULL, name VARCHAR(100) NOT NULL,
                            responsible_user_id BIGINT UNSIGNED NULL
                        );
                        CREATE TABLE audit_logs(
                            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                            user_id BIGINT UNSIGNED NULL, employee_no VARCHAR(50) NULL,
                            action VARCHAR(100) NOT NULL, target_type VARCHAR(100) NULL,
                            target_id VARCHAR(100) NULL, detail JSON NULL, ip VARCHAR(100) NULL,
                            created_at DATETIME(6) NOT NULL,
                            actor_realm VARCHAR(16) NULL, actor_account_id BIGINT UNSIGNED NULL
                        );
                        CREATE TABLE upload_sessions(
                            id VARCHAR(36) PRIMARY KEY, project_id BIGINT UNSIGNED NOT NULL,
                            uploader_id BIGINT UNSIGNED NOT NULL, file_name VARCHAR(255) NOT NULL,
                            file_size BIGINT UNSIGNED NOT NULL, file_last_modified BIGINT NOT NULL,
                            file_fingerprint VARCHAR(64) NOT NULL, file_md5 VARCHAR(32) NULL,
                            chunk_size INT UNSIGNED NOT NULL, total_chunks INT UNSIGNED NOT NULL,
                            temp_dir VARCHAR(512) NOT NULL, status VARCHAR(16) NOT NULL,
                            result_file_id BIGINT UNSIGNED NULL, expires_at DATETIME NOT NULL,
                            created_at DATETIME NOT NULL, updated_at DATETIME NOT NULL
                        );
                        CREATE TABLE file_blobs(
                            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                            sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                            size_bytes BIGINT UNSIGNED NOT NULL,
                            storage_path VARCHAR(512) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                            state VARCHAR(16) NOT NULL DEFAULT 'READY',
                            gc_started_at DATETIME(6) NULL,
                            created_at DATETIME(6) NOT NULL,
                            UNIQUE KEY uk_file_blobs_sha256(sha256),
                            UNIQUE KEY uk_file_blobs_storage_path(storage_path),
                            KEY idx_file_blobs_state(state,id)
                        );
                        CREATE TABLE files(
                            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                            project_id BIGINT UNSIGNED NOT NULL, uploader_id BIGINT UNSIGNED NOT NULL,
                            blob_id BIGINT UNSIGNED NULL,
                            direction VARCHAR(10) NOT NULL, original_name VARCHAR(255) NOT NULL,
                            stored_name VARCHAR(255) NOT NULL, ext VARCHAR(32) NOT NULL,
                            size_bytes BIGINT UNSIGNED NOT NULL, mime_type VARCHAR(255) NULL,
                            sha256 VARCHAR(64) NULL, storage_path VARCHAR(1024) NOT NULL,
                            status VARCHAR(20) NOT NULL, deleted_at DATETIME(6) NULL,
                            created_at DATETIME(6) NOT NULL,
                            KEY idx_files_blob(blob_id),
                            CONSTRAINT fk_files_blob FOREIGN KEY(blob_id) REFERENCES file_blobs(id) ON DELETE RESTRICT
                        );
                        -- The maintenance entry point also sweeps soft-deleted message images.
                        -- Keep these empty in file-recovery fixtures; image retention has dedicated tests.
                        CREATE TABLE messages(
                            id BIGINT UNSIGNED PRIMARY KEY, project_id BIGINT UNSIGNED NOT NULL,
                            sender_id BIGINT UNSIGNED NOT NULL, content TEXT NOT NULL,
                            status VARCHAR(16) NOT NULL, deleted_by BIGINT UNSIGNED NULL,
                            deleted_at DATETIME NULL, created_at DATETIME NOT NULL
                        );
                        CREATE TABLE message_images(
                            id BIGINT UNSIGNED PRIMARY KEY, message_id BIGINT UNSIGNED NOT NULL,
                            original_name VARCHAR(255) NOT NULL, stored_name VARCHAR(64) NOT NULL,
                            ext VARCHAR(8) NOT NULL, size_bytes BIGINT UNSIGNED NOT NULL,
                            mime_type VARCHAR(32) NOT NULL, storage_path VARCHAR(512) NOT NULL,
                            created_at DATETIME(3) NOT NULL,
                            UNIQUE KEY uk_message_images_stored_name(stored_name),
                            KEY idx_message_images_message(message_id,id)
                        );
                        INSERT INTO system_configs(cfg_key,cfg_value) VALUES
                            ('security.management_lock','1'),
                            ('upload.chunk_size','262144'),
                            ('upload.max_file_size','1048576'),
                            ('upload.allowed_exts','bin'),
                            ('notify.enabled','false');
                        INSERT INTO users
                            (id,employee_no,user_type,supplier_id,status,must_change_password,email,real_name)
                            VALUES(1,'T001','INTERNAL',NULL,'ACTIVE',FALSE,NULL,'Tester');
                        INSERT INTO roles(id,name,status,is_built_in) VALUES(1,'文件测试','ACTIVE',FALSE);
                        INSERT INTO permissions(id,code) VALUES(1,'file:upload'),(2,'project:list'),(3,'file:download');
                        INSERT INTO user_roles(user_id,role_id) VALUES(1,1);
                        INSERT INTO role_permissions(role_id,permission_id) VALUES(1,1),(1,2),(1,3);
                        INSERT INTO project_groups(id) VALUES(1);
                        INSERT INTO projects(id,project_group_id,supplier_id,created_by,status,confirm_side,name,responsible_user_id)
                            VALUES(1,1,1,1,'IN_PROGRESS',NULL,'File Test',1);
                        """, cancellationToken: ct));
                }
                var maintenance = new FilesMaintenanceService(
                    database, options, NullLogger<FilesMaintenanceService>.Instance);
                var upload = new UploadService(database, options,
                    new AuditService(Array.Empty<IProjectAuditCapture>()), NullLogger<UploadService>.Instance);
                var audit = new AuditService(Array.Empty<IProjectAuditCapture>());
                var identity = new IdentityService(EfTestSupport.DbContextFactory(options), options, new LoginRateLimiter(),
                    new TokenService(options), new PermissionService(), audit);
                var files = new FileService(database, options, audit, new BatchDownloadLimiter(),
                    new MediaGrantService(options), new DownloadGrantService(), identity);
                return new FilesDatabaseScope(
                    administration, databaseName, disposableRoot, database, maintenance, upload, files);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task InsertSessionAsync(string id, string status, bool expired, string? fileMd5 = null)
        {
            Directory.CreateDirectory(FileStorage.SessionDirectory(StorageRoot, id));
            await using var connection = await Database.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO upload_sessions
                    (id,project_id,uploader_id,file_name,file_size,file_last_modified,file_fingerprint,
                     file_md5,chunk_size,total_chunks,
                     temp_dir,status,result_file_id,expires_at,created_at,updated_at)
                VALUES(@Id,1,1,'sample.bin',1,1700000000000,@FileFingerprint,
                       @FileMd5,1,1,@TempDir,@Status,NULL,
                       CASE WHEN @Expired THEN UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE
                            ELSE UTC_TIMESTAMP(6)+INTERVAL 1 HOUR END,
                       UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                """, new
            {
                Id = id,
                TempDir = FileStorage.SessionDirectory(StorageRoot, id),
                Status = status,
                Expired = expired,
                FileFingerprint = Sha256Hex([1]),
                FileMd5 = fileMd5
            });
        }

        public async Task<bool> IsExpiredByDatabaseAsync(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<bool>(
                "SELECT expires_at<=UTC_TIMESTAMP(6) FROM upload_sessions WHERE id=@Id",
                new { Id = id });
        }

        public async Task<SessionTiming> TimingAsync(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<SessionTiming>("""
                SELECT created_at=updated_at AS CreatedEqualsUpdated,
                       expires_at=DATE_ADD(created_at,INTERVAL 24 HOUR) AS HasTwentyFourHourLifetime,
                       expires_at>UTC_TIMESTAMP(6) AS IsActive
                FROM upload_sessions WHERE id=@Id
                """, new { Id = id });
        }

        public DefaultHttpContext Context(long? contentLength = null)
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(CurrentUser)] = new CurrentUser(1, "T001", "INTERNAL", null);
            context.Request.ContentLength = contentLength;
            return context;
        }

        public DefaultHttpContext ChunkContext(byte[] content, string? declaredDigest = null)
        {
            var context = Context(content.LongLength);
            context.Request.Headers["X-Chunk-SHA256"] = declaredDigest ?? Sha256Hex(content);
            return context;
        }

        public Task KillConnectionAsync(int serverThread, CancellationToken ct) =>
            administration.ExecuteAsync(new CommandDefinition(
                $"KILL CONNECTION {serverThread}", cancellationToken: ct));

        public async Task<string> StatusAsync(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<string>(
                "SELECT status FROM upload_sessions WHERE id=@Id", new { Id = id });
        }

        public async Task<string?> FileMd5Async(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<string?>(
                "SELECT file_md5 FROM upload_sessions WHERE id=@Id", new { Id = id });
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally
            {
                await administration.DisposeAsync();
                try { Directory.Delete(disposableRoot, recursive: true); } catch { }
            }
        }
    }

    private sealed class SessionTiming
    {
        public bool CreatedEqualsUpdated { get; init; }
        public bool HasTwentyFourHourLifetime { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class BlockingOneByteStream : Stream
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int emitted;

        public Task WaitUntilReadAsync(CancellationToken ct) => entered.Task.WaitAsync(ct);
        public void Release() => released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await released.Task.WaitAsync(ct);
            if (Interlocked.Exchange(ref emitted, 1) != 0) return 0;
            buffer.Span[0] = 1;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GatedPayloadStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int firstRead;

        public Task WaitUntilReadAsync(CancellationToken ct) => entered.Task.WaitAsync(ct);
        public void Release() => released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref firstRead, 1) == 0)
            {
                entered.TrySetResult();
                await released.Task.WaitAsync(ct);
            }
            return await inner.ReadAsync(buffer, ct);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
