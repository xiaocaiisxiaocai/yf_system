using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using System.Diagnostics;
using System.Globalization;
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
    public async Task GarbageCollectionRespectsFreshMergeLeasesAndRecoversExpiredOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "MERGING", expired: false);
        Assert.False(await scope.IsExpiredByDatabaseAsync(sessionId));

        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        // A disconnected process is reclaimed only after its database-clock lease expires.
        await scope.ExecuteAsync(
            $"UPDATE upload_sessions SET updated_at=UTC_TIMESTAMP(6)-INTERVAL 3 MINUTE WHERE id='{sessionId}'", ct);
        await scope.Maintenance.RunGarbageCollectionAsync(ct);
        Assert.Equal("UPLOADING", await scope.StatusAsync(sessionId));
        Assert.True(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 90_000)]
    public async Task MergeHashReleasesSinglePoolConnectionAndRenewsItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct, ConfigureSingleConnectionPool);
        var content = new byte[] { 1, 2, 3, 4 };
        var sessionId = await CreateReadySessionAsync(scope, "pool.bin", content, ct);
        var gate = new GatedPayloadStream(content);
        scope.Upload.MergeChunkStreamFactory = _ => gate;

        var merge = scope.Upload.MergeAsync(FilesDatabaseScope.Context(), sessionId, ct);
        try
        {
            await gate.WaitUntilReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

            // The only connection-pool slot is available while the real merge read is gated.
            using var poolDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            poolDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using (var probe = await scope.Database.OpenAsync(poolDeadline.Token))
                Assert.Equal(1, await probe.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT 1", cancellationToken: poolDeadline.Token)));

            var before = await scope.UpdatedAtAsync(sessionId, ct);
            var renewed = before;
            var deadline = Stopwatch.StartNew();
            while (renewed == before && deadline.Elapsed < TimeSpan.FromSeconds(35))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                renewed = await scope.UpdatedAtAsync(sessionId, ct);
            }
            Assert.NotEqual(before, renewed);
            Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

            // Both the initializer and the GC recovery path must honor the active database-clock lease.
            var activeInit = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(
                FilesDatabaseScope.Context(), UploadRequest("pool.bin", content), ct));
            Assert.Equal(409, activeInit.Status);
            await scope.Maintenance.RunGarbageCollectionAsync(ct);
            Assert.Equal("MERGING", await scope.StatusAsync(sessionId));
        }
        finally { gate.Release(); }

        var merged = await merge.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(Sha256Hex(content), merged.Sha256);
        Assert.Equal("COMPLETED", await scope.StatusAsync(sessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task AbortDuringMergeHashPreventsTheFileRowAndKeepsAbortedState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct, ConfigureSingleConnectionPool);
        var content = new byte[] { 9, 8, 7, 6 };
        var sessionId = await CreateReadySessionAsync(scope, "abort-during-merge.bin", content, ct);
        var gate = new GatedPayloadStream(content);
        scope.Upload.MergeChunkStreamFactory = _ => gate;

        var merge = scope.Upload.MergeAsync(FilesDatabaseScope.Context(), sessionId, ct);
        try
        {
            await gate.WaitUntilReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), sessionId, ct);
            Assert.Equal("ABORTED", await scope.StatusAsync(sessionId));
        }
        finally { gate.Release(); }

        var conflict = await Assert.ThrowsAsync<ApiException>(() => merge);
        Assert.Equal(409, conflict.Status);
        Assert.Equal("ABORTED", await scope.StatusAsync(sessionId));
        await using var verify = await scope.Database.OpenAsync(ct);
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM files WHERE original_name='abort-during-merge.bin'", cancellationToken: ct)));
        Assert.False(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 30_000)]
    public async Task MergeReclaimsAnExpiredMergeLeaseAndCompletesFromExistingChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var content = new byte[] { 3, 1, 4, 1, 5 };
        var sessionId = await CreateReadySessionAsync(scope, "orphan-merge.bin", content, ct);
        await scope.ExecuteAsync($"""
            UPDATE upload_sessions
            SET status='MERGING', updated_at=UTC_TIMESTAMP(6)-INTERVAL 3 MINUTE
            WHERE id='{sessionId}'
            """, ct);

        var merged = await scope.Upload.MergeAsync(FilesDatabaseScope.Context(), sessionId, ct);

        Assert.Equal(Sha256Hex(content), merged.Sha256);
        Assert.Equal("COMPLETED", await scope.StatusAsync(sessionId));
    }

    private static void ConfigureSingleConnectionPool(AppOptions options)
    {
        options.ConnectionString = new MySqlConnectionStringBuilder(options.ConnectionString)
        {
            MaximumPoolSize = 1,
            MinimumPoolSize = 0,
        }.ConnectionString;
    }

    private static async Task<string> CreateReadySessionAsync(
        FilesDatabaseScope scope, string name, byte[] content, CancellationToken ct)
    {
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest(name, content), ct);
        using (var body = new MemoryStream(content))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(content), initialized.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(Md5Hex(content)), ct);
        return initialized.SessionId;
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
        var context = FilesDatabaseScope.ChunkContext([1]);
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
            FilesDatabaseScope.Context(), sessionId, ct)).UploadedChunks);
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
            scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct),
            scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct));
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
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);

        using (var body = new MemoryStream(original))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(original), initialized.SessionId, 0, body, ct);

        using (var body = new MemoryStream(replacement))
        {
            var mismatched = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.PutChunkAsync(
                FilesDatabaseScope.ChunkContext(replacement, Sha256Hex(original)), initialized.SessionId, 0, body, ct));
            Assert.Equal(400, mismatched.Status);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0), ct));

        var resumed = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
        Assert.Equal(initialized.SessionId, resumed.SessionId);
        Assert.True(resumed.Resumed);
        var originalDigest = Assert.Single(resumed.UploadedChunks);
        Assert.Equal(0U, originalDigest.Index);
        Assert.Equal(Sha256Hex(original), originalDigest.Sha256);

        using (var body = new MemoryStream(replacement))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(replacement), initialized.SessionId, 0, body, ct);
        var replacedDigest = Assert.Single((await scope.Upload.GetAsync(
            FilesDatabaseScope.Context(), initialized.SessionId, ct)).UploadedChunks);
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
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
        using (var body = new MemoryStream(original))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(original), initialized.SessionId, 0, body, ct);

        var chunkPath = FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0);
        var originalSidecar = await File.ReadAllTextAsync(chunkPath + ".sha256", ct);
        await File.WriteAllBytesAsync(chunkPath, tampered, ct);

        Assert.Equal(Sha256Hex(original), SidecarDigest(originalSidecar));
        Assert.Empty((await scope.Upload.GetAsync(
            FilesDatabaseScope.Context(), initialized.SessionId, ct)).UploadedChunks);
        var resumed = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
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
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
        using (var body = new MemoryStream(content))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(content), initialized.SessionId, 0, body, ct);

        var chunkPath = FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0);
        var fields = (await File.ReadAllTextAsync(chunkPath + ".sha256", ct)).Trim().Split(' ');
        var chunk = new FileInfo(chunkPath);
        Assert.Equal([Sha256Hex(content), chunk.Length.ToString(CultureInfo.InvariantCulture), chunk.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)], fields);

        // Status polling never rereads a legacy chunk. Merge still hashes it and accepts valid content.
        await File.WriteAllTextAsync(chunkPath + ".sha256", Sha256Hex(content), ct);
        Assert.Empty((await scope.Upload.GetAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct)).UploadedChunks);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(Md5Hex(content)), ct);
        var merged = await scope.Upload.MergeAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct);
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
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
        await using var firstBody = new GatedPayloadStream(firstContent);
        await using var secondBody = new GatedPayloadStream(secondContent);
        var firstPut = scope.Upload.PutChunkAsync(
            FilesDatabaseScope.ChunkContext(firstContent), initialized.SessionId, 0, firstBody, ct);
        var secondPut = scope.Upload.PutChunkAsync(
            FilesDatabaseScope.ChunkContext(secondContent), initialized.SessionId, 0, secondBody, ct);
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
            FilesDatabaseScope.Context(), initialized.SessionId, ct)).UploadedChunks);
        Assert.Equal(0U, uploaded.Index);
        Assert.Equal(actualSha256, uploaded.Sha256);

        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), initialized.SessionId,
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
        await scope.Upload.GetAsync(FilesDatabaseScope.Context(), activeId, ct);
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.GetAsync(FilesDatabaseScope.Context(), expiredId, ct));
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
        await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext([1]), activeId, 0,
            activeBody, ct);
        using var expiredBody = new MemoryStream([1]);
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext([1]), expiredId, 0,
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
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), activeId, ct));
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), expiredId, ct));
        var expiredMerging = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), expiredMergingId, ct));
        Assert.Equal(400, incomplete.Status);
        Assert.Equal(409, expired.Status);
        Assert.Equal(409, expiredMerging.Status);
        Assert.Equal("UPLOADING", await scope.StatusAsync(activeId));
        Assert.Equal("UPLOADING", await scope.StatusAsync(expiredId));
        Assert.Equal("MERGING", await scope.StatusAsync(expiredMergingId));
    }

    [Fact(Timeout = 60_000)]
    public async Task Md5SubmissionLocksTheProjectBeforeTheSessionSoItCannotDeadlockWithMerge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "UPLOADING", expired: false);
        var md5 = Md5Hex([1]);

        // Merge, claim and init lock the main project, the subproject and only then the session; hold the first
        // two the way a merge does, and let /md5 queue behind them.
        await using var holder = await scope.Database.OpenAsync(ct);
        await using var holdTx = await holder.BeginTransactionAsync(ct);
        await holder.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM project_groups WHERE id=1 FOR UPDATE", transaction: holdTx, cancellationToken: ct));
        await holder.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE id=1 FOR UPDATE", transaction: holdTx, cancellationToken: ct));
        var submit = scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), sessionId,
            new SubmitUploadMd5Request(md5), ct);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        Assert.False(submit.IsCompleted);

        // While queued on the project it must not hold the session row, or a merge that already owns the
        // project and wants the session would deadlock with it.
        await using (var probe = await scope.Database.OpenAsync(ct))
        {
            await probe.ExecuteAsync(new CommandDefinition("SET SESSION innodb_lock_wait_timeout=1", cancellationToken: ct));
            await using var probeTx = await probe.BeginTransactionAsync(ct);
            var locked = await probe.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT id FROM upload_sessions WHERE id=@Id FOR UPDATE",
                new { Id = sessionId }, probeTx, cancellationToken: ct));
            Assert.Equal(sessionId, locked);
            await probeTx.RollbackAsync(ct);
        }

        await holdTx.CommitAsync(ct);
        await submit.WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.Equal(md5, await scope.FileMd5Async(sessionId));

        // Authorization and state rules are unchanged: only the uploader's own UPLOADING session accepts a digest.
        var missing = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.SubmitMd5Async(
            FilesDatabaseScope.Context(), Guid.NewGuid().ToString("D"), new SubmitUploadMd5Request(md5), ct));
        Assert.Equal(404, missing.Status);
        var completedId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(completedId, "COMPLETED", expired: false);
        var completed = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.SubmitMd5Async(
            FilesDatabaseScope.Context(), completedId, new SubmitUploadMd5Request(md5), ct));
        Assert.Equal(409, completed.Status);
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
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), sessionId, ct));
        Assert.Equal(409, incomplete.Status);
        Assert.Equal("MERGING", await scope.StatusAsync(sessionId));

        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), sessionId, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(sessionId));
        Assert.False(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
    }

    [Fact(Timeout = 60_000)]
    public async Task ActiveUploadListHidesExpiredSessionsAndCrossUserAbortFollowsTheRules()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await scope.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status) VALUES (1,'Supplier','ACTIVE');
            INSERT INTO users(id,employee_no,user_type,supplier_id,status,must_change_password,email,real_name)
            VALUES
              (2,'T002','INTERNAL',NULL,'ACTIVE',0,NULL,'NoAbort'),
              (3,'S001','SUPPLIER',1,'ACTIVE',0,NULL,'SupplierOwner'),
              (4,'S002','SUPPLIER',1,'ACTIVE',0,NULL,'SupplierPeer'),
              (5,'T005','INTERNAL',NULL,'ACTIVE',0,NULL,'StatusUser');
            INSERT INTO permissions(id,code) VALUES
              (4,'file:delete'),(5,'project:status'),(6,'project:submit'),(7,'project:view_all');
            INSERT INTO roles(id,name,status,is_built_in) VALUES
              (2,'只读','ACTIVE',0),(3,'供应商提交','ACTIVE',0),(4,'状态管理','ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(2,2),(3,3),(4,3),(5,4);
            INSERT INTO role_permissions(role_id,permission_id) VALUES
              (2,2),(3,1),(3,2),(3,6),(4,2),(4,5),(4,7);
            UPDATE project_groups SET created_by=2 WHERE id=1;
            """, ct);
        var active = Guid.NewGuid().ToString("D");
        var expired = Guid.NewGuid().ToString("D");
        var merging = Guid.NewGuid().ToString("D");
        var peerSession = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(active, "UPLOADING", false, fileName: "live.bin");
        await scope.InsertSessionAsync(expired, "UPLOADING", true, fileName: "old.bin");
        await scope.InsertSessionAsync(merging, "MERGING", true, fileName: "merge.bin");
        await scope.InsertSessionAsync(peerSession, "UPLOADING", false, fileName: "peer.bin");
        await scope.ExecuteAsync($"UPDATE upload_sessions SET uploader_id=3 WHERE id='{peerSession}'", ct);
        var part = FileStorage.ChunkPath(scope.StorageRoot, active, 0);
        await File.WriteAllBytesAsync(part, [1], ct);

        var listed = await scope.Upload.ListActiveAsync(FilesDatabaseScope.Context(), 1, ct);
        Assert.Equal(3, listed.Total);
        Assert.Equal(["live.bin", "merge.bin", "peer.bin"], listed.Items.Select(item => item.FileName).Order().ToArray());
        Assert.DoesNotContain(listed.Items, item => item.FileName == "old.bin");
        var live = Assert.Single(listed.Items, item => item.SessionId == active);
        Assert.Equal(1, live.ReceivedChunks);
        Assert.True(live.CanResume);
        Assert.True(live.CanAbort);
        Assert.False(Assert.Single(listed.Items, item => item.SessionId == merging).CanResume);
        // The viewer is the subproject's responsible user, who may clear a supplier session blocking the review.
        Assert.True(Assert.Single(listed.Items, item => item.SessionId == peerSession).CanAbort);

        var denied = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.AbortAsync(
            Actor(2, "T002", "INTERNAL", null), active, ct));
        Assert.Equal(403, denied.Status);
        Assert.Equal("UPLOADING", await scope.StatusAsync(active));
        var supplierDenied = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.AbortAsync(
            Actor(4, "S002", "SUPPLIER", 1), active, ct));
        Assert.Equal(403, supplierDenied.Status);
        var missing = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.AbortAsync(
            FilesDatabaseScope.Context(), Guid.NewGuid().ToString("D"), ct));
        Assert.Equal(404, missing.Status);

        var notifier = new RecordingUploadNotifier();
        var upload = new UploadService(scope.Database, scope.Options,
            new AuditService(Array.Empty<IProjectAuditCapture>()), NullLogger<UploadService>.Instance, notifier);
        await upload.AbortAsync(Actor(5, "T005", "INTERNAL", null), active, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(active));
        Assert.Equal([(1UL, "project")], notifier.Calls);
        await upload.AbortAsync(Actor(5, "T005", "INTERNAL", null), active, ct);
        Assert.Single(notifier.Calls);
        await using var conn = await scope.Database.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='UPLOAD_ABORT' AND target_id=@Id", new { Id = active }));
        using (var detail = System.Text.Json.JsonDocument.Parse(await conn.ExecuteScalarAsync<string>(
            "SELECT detail FROM audit_logs WHERE action='UPLOAD_ABORT' AND target_id=@Id", new { Id = active }) ?? "{}"))
            Assert.True(detail.RootElement.GetProperty("crossUser").GetBoolean());

        await scope.Upload.AbortAsync(Actor(4, "S002", "SUPPLIER", 1), peerSession, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(peerSession));
    }

    private static DefaultHttpContext Actor(ulong id, string employeeNo, string userType, ulong? supplierId)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(CurrentUser)] = new CurrentUser(id, employeeNo, userType, supplierId);
        return context;
    }

    private sealed class RecordingUploadNotifier : IStoredUploadNotifier
    {
        public List<(ulong ProjectId, string Kind)> Calls { get; } = [];
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        {
            Calls.Add((projectId, kind));
            return Task.CompletedTask;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MergeRejectsSubmittedMd5ThatDoesNotMatchUploadedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var payload = new byte[] { 1 };
        var initialized = await scope.Upload.InitAsync(
            FilesDatabaseScope.Context(), UploadRequest("wrong-md5.bin", payload), ct);
        using (var body = new MemoryStream(payload))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(payload), initialized.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(new string('0', 32)), ct);

        var mismatch = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct));

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
        await scope.ExecuteAsync(
            $"UPDATE upload_sessions SET updated_at=UTC_TIMESTAMP(6)-INTERVAL 3 MINUTE WHERE id='{sessionId}'", ct);
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
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,
                                  mime_type,sha256,storage_path,status,deleted_at,created_at)
                VALUES(1,1,1,'C2S','private-file-name.pdf','stored.pdf','pdf',1,'application/pdf',NULL,
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

    [Fact(Timeout = 30_000)]
    public async Task InternalUploadsRequireWorkbookAndStepOrArchiveMaterial()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await scope.ExecuteAsync("""
            UPDATE system_configs SET cfg_value='bin,step,stp,zip,rar,7z,xlsx,xls,xlsm,xlsb' WHERE cfg_key='upload.allowed_exts';
            UPDATE files SET status='DELETED', deleted_at=UTC_TIMESTAMP(6) WHERE id IN (900,901);
            """, ct);

        // 必需资料本身（Excel、STEP、压缩文件）可以先建立会话；其它文件要等两类资料都具备。
        var notes = UploadRequest("notes.bin", [6]);
        var none = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct));
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, none.Message);
        var workbook = await scope.Upload.InitAsync(FilesDatabaseScope.Context(),
            UploadRequest("CSLR-605 六軸雙工位放板機 2105931-1 動作流程.xlsx", [2]), ct);
        var workbookOnly = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct));
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, workbookOnly.Message);

        // 同一批次：Excel 与 STEP 或压缩文件的会话都建立后，其余资料即可并发初始化。
        byte fill = 10;
        foreach (var model in new[] { "CSLR-605 放板機2105931-1.STEP", "assembly.stp", "models.zip", "models.RAR", "models.7z" })
        {
            var modelSession = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest(model, [fill++]), ct);
            var accepted = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct);
            Assert.NotNull(accepted.SessionId);
            await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), accepted.SessionId!, ct);
            await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), modelSession.SessionId!, ct);
        }

        // 只有 3D 资料、没有 Excel 时，其它文件仍被拒绝；各种 Excel 扩展名都计入。
        var modelOnly = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("models.7z", [fill++]), ct);
        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), workbook.SessionId!, ct);
        var noWorkbook = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct));
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, noWorkbook.Message);
        foreach (var extension in new[] { "xls", "xlsm", "xlsb" })
        {
            var other = await scope.Upload.InitAsync(FilesDatabaseScope.Context(),
                UploadRequest($"动作流程.{extension.ToUpperInvariant()}", [fill++]), ct);
            var accepted = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct);
            await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), accepted.SessionId!, ct);
            await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), other.SessionId!, ct);
        }
        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), modelOnly.SessionId!, ct);

        // 已合并的 Excel 与 STEP 文件满足规则；只剩其中一类时仍拒绝。
        await scope.ExecuteAsync("UPDATE files SET status='AVAILABLE', deleted_at=NULL WHERE id=900", ct);
        await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct));
        await scope.ExecuteAsync("UPDATE files SET status='AVAILABLE', deleted_at=NULL WHERE id=901", ct);
        Assert.NotNull((await scope.Upload.InitAsync(FilesDatabaseScope.Context(), notes, ct)).SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task MergeRechecksMaterialAndKeepsRejectedSessionResumable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await scope.ExecuteAsync("UPDATE system_configs SET cfg_value='bin,step,xlsx' WHERE cfg_key='upload.allowed_exts'", ct);
        var payload = new byte[] { 42 };
        var initialized = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("notes.bin", payload), ct);
        using (var body = new MemoryStream(payload))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(payload), initialized.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), initialized.SessionId,
            new SubmitUploadMd5Request(Md5Hex(payload)), ct);

        // The workbook that satisfied the rule at init is gone by merge time.
        await scope.ExecuteAsync("UPDATE files SET status='DELETED', deleted_at=UTC_TIMESTAMP(6) WHERE id=901", ct);
        var rejected = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct));
        Assert.Equal(400, rejected.Status);
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, rejected.Message);
        Assert.Equal("UPLOADING", await scope.StatusAsync(initialized.SessionId));
        Assert.Equal(payload, await File.ReadAllBytesAsync(
            FileStorage.ChunkPath(scope.StorageRoot, initialized.SessionId, 0), ct));

        // An in-flight workbook session admits a same-batch file at init only: it may still be cancelled or
        // expire, so merge keeps rejecting until a workbook is actually available.
        var workbook = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("动作流程.xlsx", [43]), ct);
        var stillRejected = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.MergeAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct));
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, stillRejected.Message);
        Assert.Equal("UPLOADING", await scope.StatusAsync(initialized.SessionId));
        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), workbook.SessionId!, ct);

        // Once both kinds of material are available the same session merges.
        await scope.ExecuteAsync("UPDATE files SET status='AVAILABLE', deleted_at=NULL WHERE id=901", ct);
        var merged = await scope.Upload.MergeAsync(FilesDatabaseScope.Context(), initialized.SessionId, ct);
        Assert.Equal(Sha256Hex(payload), merged.Sha256);

        // Same-batch file admitted by an in-flight STEP session whose STEP is later abandoned: merge is
        // rejected and the session can still be cancelled.
        await scope.ExecuteAsync("UPDATE files SET status='DELETED', deleted_at=UTC_TIMESTAMP(6) WHERE id=900", ct);
        var second = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("assembly.step", [44]), ct);
        var other = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("other.bin", [45]), ct);
        using (var body = new MemoryStream(new byte[] { 45 }))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext([45]), other.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), other.SessionId, new SubmitUploadMd5Request(Md5Hex([45])), ct);
        await Assert.ThrowsAsync<ApiException>(() => scope.Upload.MergeAsync(FilesDatabaseScope.Context(), other.SessionId, ct));
        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), second.SessionId!, ct);
        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), other.SessionId, ct);
        Assert.Equal("ABORTED", await scope.StatusAsync(other.SessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task RequiredMaterialMergesWithoutTheOtherMaterialKind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await scope.ExecuteAsync("""
            UPDATE system_configs SET cfg_value='bin,step,7z,xlsx' WHERE cfg_key='upload.allowed_exts';
            UPDATE files SET status='DELETED', deleted_at=UTC_TIMESTAMP(6) WHERE id IN (900,901);
            """, ct);
        // The same-batch rule is enforced by the upload dialog; the server lets each required file merge on its own.
        byte fill = 50;
        foreach (var name in new[] { "models.7z", "动作流程.xlsx" })
        {
            var payload = new[] { fill++ };
            var session = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest(name, payload), ct);
            using (var body = new MemoryStream(payload))
                await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(payload), session.SessionId, 0, body, ct);
            await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), session.SessionId,
                new SubmitUploadMd5Request(Md5Hex(payload)), ct);
            Assert.Equal(Sha256Hex(payload), (await scope.Upload.MergeAsync(FilesDatabaseScope.Context(), session.SessionId, ct)).Sha256);
        }
        Assert.NotNull((await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("notes.bin", [9]), ct)).SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ResumingAnExistingSessionSkipsTheNewSessionMaterialCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        await scope.ExecuteAsync("""
            UPDATE system_configs SET cfg_value='bin,step,xlsx' WHERE cfg_key='upload.allowed_exts';
            UPDATE files SET status='DELETED', deleted_at=UTC_TIMESTAMP(6) WHERE id IN (900,901);
            """, ct);
        var request = UploadRequest("notes.bin", [3]);
        var fresh = await Assert.ThrowsAsync<ApiException>(() => scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct));
        Assert.Equal(UploadMaterialRules.MaterialRequiredMessage, fresh.Message);

        // 续传已有会话，不再按新建会话检查资料。
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "UPLOADING", expired: false,
            fileName: "notes.bin", fingerprint: Sha256Hex([3]));
        var resumed = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), request, ct);
        Assert.True(resumed.Resumed);
        Assert.Equal(sessionId, resumed.SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task UploadQuotaLimitsActiveSessionsAndPendingBytesPerAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct, options =>
        {
            options.UploadMaxActiveSessionsPerUser = 2;
            options.UploadMaxPendingBytesPerUser = 5;
        });
        var first = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("a.bin", [1, 1]), ct);
        var second = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("b.bin", [2, 2]), ct);

        var tooMany = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("c.bin", [3]), ct));
        Assert.Equal(409, tooMany.Status);
        Assert.Equal(40901, tooMany.Code);
        Assert.Contains("上限", tooMany.Message, StringComparison.Ordinal);

        // Resuming an existing session never counts against the quota.
        var resumed = await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("a.bin", [1, 1]), ct);
        Assert.Equal(first.SessionId, resumed.SessionId);

        await scope.Upload.AbortAsync(FilesDatabaseScope.Context(), second.SessionId, ct);
        var tooLarge = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("d.bin", [4, 4, 4, 4]), ct));
        Assert.Equal(409, tooLarge.Status);
        Assert.Contains("总大小", tooLarge.Message, StringComparison.Ordinal);
        Assert.NotNull((await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("e.bin", [5, 5, 5]), ct)).SessionId);

        // Expired sessions no longer occupy the quota.
        await scope.ExecuteAsync("UPDATE upload_sessions SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE", ct);
        Assert.NotNull((await scope.Upload.InitAsync(FilesDatabaseScope.Context(), UploadRequest("f.bin", [6, 6, 6, 6, 6]), ct)).SessionId);
    }

    [Fact(Timeout = 60_000)]
    public async Task OrphanBlobScanCursorAdvancesPastDeletedCursorFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        // 150 recent (grace period) files: the first cycle scans 100 and parks the cursor on the 100th.
        var paths = new List<string>();
        for (var index = 0; index < 150; index++)
        {
            var sha = Sha256Hex(BitConverter.GetBytes(index));
            var path = FileBlobStore.AbsolutePath(scope.StorageRoot, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1], ct);
            paths.Add(path);
        }

        await scope.Maintenance.PurgeOrphanBlobFilesAsync(ct);
        var cursor = scope.Maintenance.OrphanScanCursor;
        Assert.NotNull(cursor);
        File.Delete(cursor);

        // The next cycle resumes strictly after the (now missing) cursor instead of restarting.
        await scope.Maintenance.PurgeOrphanBlobFilesAsync(ct);
        var next = scope.Maintenance.OrphanScanCursor;
        Assert.NotNull(next);
        Assert.NotEqual(cursor, next);
        var ordered = paths.Where(File.Exists)
            .OrderBy(path => path.Replace(Path.DirectorySeparatorChar, '\u0001'), StringComparer.Ordinal).ToList();
        Assert.Equal(ordered[^1], next);

        // After the tail, the scan wraps around to the beginning.
        await scope.Maintenance.PurgeOrphanBlobFilesAsync(ct);
        Assert.Null(scope.Maintenance.OrphanScanCursor);
        await scope.Maintenance.PurgeOrphanBlobFilesAsync(ct);
        Assert.Equal(ordered[99], scope.Maintenance.OrphanScanCursor);
    }

    [Fact(Timeout = 30_000)]
    public async Task ReferencedBlobVerificationRespectsPerCycleByteBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct,
            options => options.BlobVerifyBytesPerCycle = 10);
        var ids = new List<ulong>();
        await using (var connection = await scope.Database.OpenAsync(ct))
        {
            // The fixture's STEP record has no file on disk; leave it out of the referenced set.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE files SET status='PURGED' WHERE id=900", cancellationToken: ct));
            for (var index = 0; index < 4; index++)
            {
                var content = Enumerable.Repeat((byte)(index + 1), 4).ToArray();
                var sha = Sha256Hex(content);
                var path = FileBlobStore.AbsolutePath(scope.StorageRoot, sha);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content, ct);
                var id = await connection.ExecuteScalarAsync<ulong>(new CommandDefinition("""
                    INSERT INTO file_blobs(sha256,size_bytes,storage_path,state,created_at)
                    VALUES(@Sha,4,@Path,'READY',UTC_TIMESTAMP(6));
                    SELECT LAST_INSERT_ID();
                    """, new { Sha = sha, Path = FileBlobStore.RelativePath(sha) }, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO files(project_id,uploader_id,blob_id,direction,original_name,stored_name,ext,
                                      size_bytes,sha256,storage_path,status,created_at)
                    VALUES(1,1,@Id,'C2S','v.bin','v.bin','bin',4,@Sha,@Path,'AVAILABLE',UTC_TIMESTAMP(6))
                    """, new { Id = id, Sha = sha, Path = FileBlobStore.RelativePath(sha) }, cancellationToken: ct));
                ids.Add(id);
            }
        }

        // Budget 10 bytes with 4-byte blobs: two blobs per cycle, then the cursor wraps.
        await scope.Maintenance.VerifyReferencedBlobFilesAsync(ct);
        Assert.Equal(ids[1], scope.Maintenance.VerifyCursor);
        await scope.Maintenance.VerifyReferencedBlobFilesAsync(ct);
        Assert.Equal(ids[3], scope.Maintenance.VerifyCursor);
        await scope.Maintenance.VerifyReferencedBlobFilesAsync(ct);
        Assert.Equal(0UL, scope.Maintenance.VerifyCursor);
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncBlobVerificationDetectsSameSizeCorruption()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "yf-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = new byte[] { 1, 2, 3, 4 };
            var sha = Sha256Hex(content);
            var path = FileBlobStore.AbsolutePath(root, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, ct);
            await FileBlobStore.VerifyBoundPhysicalFileAsync(root, FileBlobStore.RelativePath(sha), sha, 4, ct);
            await File.WriteAllBytesAsync(path, [4, 3, 2, 1], ct);
            var corrupt = await Assert.ThrowsAsync<ApiException>(() =>
                FileBlobStore.VerifyBoundPhysicalFileAsync(root, FileBlobStore.RelativePath(sha), sha, 4, ct));
            Assert.Equal(409, corrupt.Status);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact(Timeout = 60_000)]
    public async Task StorageMigrationPausesPhysicalCleanupAndTheCleanupResumesAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var old = DateTime.UtcNow.AddHours(-25);

        // One candidate for each physical step of the cycle: orphan blob scan, pending blob collection,
        // stale batch archive and orphan upload directory.
        var orphanContent = new byte[] { 71, 72, 73 };
        var orphanPath = FileBlobStore.AbsolutePath(scope.StorageRoot, Sha256Hex(orphanContent));
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllBytesAsync(orphanPath, orphanContent, ct);
        File.SetLastWriteTimeUtc(orphanPath, old);

        var pendingContent = new byte[] { 81, 82, 83, 84 };
        var pendingPath = FileBlobStore.AbsolutePath(scope.StorageRoot, Sha256Hex(pendingContent));
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        await File.WriteAllBytesAsync(pendingPath, pendingContent, ct);
        await InsertBlobAsync(scope, pendingContent, Yf.Api.Infrastructure.Entities.FileBlobStates.GarbageCollectionPending, ct);

        var temp = Path.Combine(scope.StorageRoot, "tmp");
        var staleArchive = Path.Combine(temp, "yf_files_stale.zip");
        var orphanSession = Path.Combine(temp, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(orphanSession);
        await File.WriteAllBytesAsync(staleArchive, [1], ct);
        await File.WriteAllBytesAsync(Path.Combine(orphanSession, "partial.bin"), [2], ct);
        File.SetLastWriteTimeUtc(staleArchive, old);
        Directory.SetLastWriteTimeUtc(orphanSession, old);

        var previousRoot = Path.Combine(Path.GetDirectoryName(scope.StorageRoot)!, "previous-storage");
        Directory.CreateDirectory(previousRoot);
        scope.Options.StorageFallbackRoot = previousRoot;

        await scope.Maintenance.RunGarbageCollectionAsync(ct);

        // A migration is running: nothing was deleted and no scan cursor moved.
        Assert.True(File.Exists(orphanPath));
        Assert.True(File.Exists(pendingPath));
        Assert.True(await BlobRowExistsAsync(scope, pendingContent, ct));
        Assert.True(File.Exists(staleArchive));
        Assert.True(Directory.Exists(orphanSession));
        Assert.Equal(0UL, scope.Maintenance.VerifyCursor);
        Assert.Null(scope.Maintenance.OrphanScanCursor);

        scope.Options.StorageFallbackRoot = null;
        await scope.Maintenance.RunGarbageCollectionAsync(ct);

        // Without a fallback root the same cycle cleans everything.
        Assert.False(File.Exists(orphanPath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(await BlobRowExistsAsync(scope, pendingContent, ct));
        Assert.False(File.Exists(staleArchive));
        Assert.False(Directory.Exists(orphanSession));
        Assert.NotEqual(0UL, scope.Maintenance.VerifyCursor);
        Assert.NotNull(scope.Maintenance.OrphanScanCursor);
    }

    [Fact(Timeout = 60_000)]
    public async Task BlobDeletionStartedBeforeAMigrationRemovesCopiesFromEveryRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var content = new byte[] { 91, 92, 93, 94, 95 };
        var sha = Sha256Hex(content);
        var previousRoot = Path.Combine(Path.GetDirectoryName(scope.StorageRoot)!, "previous-storage");
        var primaryPath = FileBlobStore.AbsolutePath(scope.StorageRoot, sha);
        var previousPath = FileBlobStore.AbsolutePath(previousRoot, sha);
        foreach (var path in new[] { primaryPath, previousPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, ct);
        }
        await InsertBlobAsync(scope, content, Yf.Api.Infrastructure.Entities.FileBlobStates.GarbageCollectionPending, ct);

        // Hold the blob lease so the cycle (which began while no migration was running) waits for it; then start
        // the migration, as the settings page does, and let the cycle finish.
        await using var owner = await scope.Database.OpenAsync(ct);
        await using var lease = await MySqlNamedLock.TryAcquireAsync(owner, FileBlobStore.LockName(owner, sha), 0, ct);
        Assert.NotNull(lease);
        var cycle = Task.Run(() => scope.Maintenance.RunGarbageCollectionAsync(ct), ct);
        await LockWaitProbe.WaitForNamedLockWaiterAsync(scope.Options.ConnectionString, ct, cycle);
        Assert.False(cycle.IsCompleted, "The cycle must be waiting for the blob lease.");
        scope.Options.StorageFallbackRoot = previousRoot;
        await lease.DisposeAsync();
        await cycle;

        Assert.False(File.Exists(primaryPath));
        Assert.False(File.Exists(previousPath));
        Assert.False(await BlobRowExistsAsync(scope, content, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task ExpiredUploadCleanupStillRunsDuringMigrationInEveryRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var sessionId = Guid.NewGuid().ToString("D");
        await scope.InsertSessionAsync(sessionId, "UPLOADING", expired: true);
        var previousRoot = Path.Combine(Path.GetDirectoryName(scope.StorageRoot)!, "previous-storage");
        var previousDirectory = FileStorage.SessionDirectory(previousRoot, sessionId);
        Directory.CreateDirectory(previousDirectory);
        await File.WriteAllBytesAsync(Path.Combine(previousDirectory, "0.part"), [1], ct);
        scope.Options.StorageFallbackRoot = previousRoot;

        await scope.Maintenance.RunGarbageCollectionAsync(ct);

        Assert.Equal("EXPIRED", await scope.StatusAsync(sessionId));
        Assert.False(Directory.Exists(FileStorage.SessionDirectory(scope.StorageRoot, sessionId)));
        Assert.False(Directory.Exists(previousDirectory));
    }

    [Fact(Timeout = 60_000)]
    public async Task MergeRechecksFileUploadPermissionInsideItsWriteTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await FilesDatabaseScope.CreateOrSkipAsync(ct);
        var payload = new byte[] { 61, 62 };
        var session = await scope.Upload.InitAsync(
            FilesDatabaseScope.Context(), UploadRequest("revoked-merge.bin", payload), ct);
        using (var body = new MemoryStream(payload))
            await scope.Upload.PutChunkAsync(FilesDatabaseScope.ChunkContext(payload), session.SessionId, 0, body, ct);
        await scope.Upload.SubmitMd5Async(FilesDatabaseScope.Context(), session.SessionId,
            new SubmitUploadMd5Request(Md5Hex(payload)), ct);

        // An administrator revoking file:upload holds the management gate exclusively; the merge passes its early
        // unlocked checks, then waits for the gate in its write transaction and must see the revocation.
        await using var management = await scope.Database.OpenAsync(ct);
        await using var managementTx = await AppDb.BeginTransactionAsync(management, ct);
        await AccessService.LockManagementAsync(management, managementTx, ct);
        var merge = Task.Run(() => scope.Upload.MergeAsync(FilesDatabaseScope.Context(), session.SessionId, ct), ct);
        try
        {
            await LockWaitProbe.WaitForLockWaitAsync(scope.Options.ConnectionString, ct, merge);
            Assert.False(merge.IsCompleted, "The merge must be waiting for the business gate.");
            Assert.Equal(1, await management.ExecuteAsync(new CommandDefinition(
                "DELETE FROM role_permissions WHERE role_id=1 AND permission_id=1",
                transaction: managementTx, cancellationToken: ct)));
            await managementTx.CommitAsync(ct);
        }
        catch
        {
            await managementTx.RollbackAsync(CancellationToken.None);
            try { await merge; } catch { }
            throw;
        }

        var denied = await Assert.ThrowsAsync<ApiException>(() => merge);

        Assert.Equal((403, 40301), (denied.Status, denied.Code));
        Assert.Equal("UPLOADING", await scope.StatusAsync(session.SessionId));
        await using var verify = await scope.Database.OpenAsync(ct);
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM files WHERE original_name='revoked-merge.bin'", cancellationToken: ct)));
    }

    private static async Task InsertBlobAsync(FilesDatabaseScope scope, byte[] content, string state, CancellationToken ct)
    {
        var sha = Sha256Hex(content);
        await using var connection = await scope.Database.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO file_blobs(sha256,size_bytes,storage_path,state,gc_started_at,created_at)
            VALUES(@Sha,@Size,@Path,@State,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """, new { Sha = sha, Size = (ulong)content.Length, Path = FileBlobStore.RelativePath(sha), State = state },
            cancellationToken: ct));
    }

    private static async Task<bool> BlobRowExistsAsync(FilesDatabaseScope scope, byte[] content, CancellationToken ct)
    {
        await using var connection = await scope.Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM file_blobs WHERE sha256=@Sha)", new { Sha = Sha256Hex(content) },
            cancellationToken: ct));
    }

    private static InitUploadRequest UploadRequest(string fileName, byte[] content) =>
        new(1, fileName, (ulong)content.LongLength, 1_700_000_000_000, Sha256Hex(content));

    private static string SidecarDigest(string sidecar) => sidecar.Trim().Split(' ')[0];

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    // MD5 is required here: these tests exercise the legacy client-submitted MD5 digest contract
    // (SubmitMd5Async / deferred legacy digests), not a security decision made by the test.
#pragma warning disable CA5351
    private static string Md5Hex(byte[] content) =>
        Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
#pragma warning restore CA5351

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
            FileService files,
            AppOptions options)
        {
            this.administration = administration;
            this.databaseName = databaseName;
            this.disposableRoot = disposableRoot;
            Database = database;
            Maintenance = maintenance;
            Upload = upload;
            Files = files;
            Options = options;
            StorageRoot = Path.Combine(disposableRoot, "storage");
        }

        public AppDb Database { get; }
        public FilesMaintenanceService Maintenance { get; }
        public UploadService Upload { get; }
        public FileService Files { get; }
        public AppOptions Options { get; }
        public string StorageRoot { get; }

        public static async Task<FilesDatabaseScope> CreateOrSkipAsync(
            CancellationToken ct, Action<AppOptions>? configure = null)
        {
            var adminOptions = LocalMySqlTestServer.AdministrationOrSkip("File recovery tests");
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
                configure?.Invoke(options);
                var database = new AppDb(options);
                await using (var connection = await database.OpenAsync(ct))
                {
                    await connection.ExecuteAsync(new CommandDefinition(HandwrittenTestSchemas.FilesRecovery + Environment.NewLine + """
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
                        INSERT INTO projects(id,project_group_id,supplier_id,created_by,status,name,responsible_user_id)
                            VALUES(1,1,1,1,'IN_PROGRESS','File Test',1);
                        -- 内部账号发给供应商的其它文件要求子项目已有 STEP 3D 图与动作流程 Excel；这两条记录只满足
                        -- 资料规则，其 blob 不在磁盘上，回收测试不会触及仍被引用的可用文件。
                        INSERT INTO file_blobs(id,sha256,size_bytes,storage_path,state,created_at)
                            VALUES(900,REPEAT('f',64),1,'blobs/sha256/ff/fixture-step','READY',UTC_TIMESTAMP(6)),
                                  (901,REPEAT('e',64),1,'blobs/sha256/ee/fixture-xlsx','READY',UTC_TIMESTAMP(6));
                        INSERT INTO files
                            (id,project_id,uploader_id,blob_id,direction,original_name,stored_name,ext,size_bytes,
                             storage_path,status,created_at)
                            VALUES(900,1,1,900,'C2S','fixture-assembly.step','fixture-step','step',1,
                                   'blobs/sha256/ff/fixture-step','AVAILABLE',UTC_TIMESTAMP(6)),
                                  (901,1,1,901,'C2S','fixture-motion-flow.xlsx','fixture-xlsx','xlsx',1,
                                   'blobs/sha256/ee/fixture-xlsx','AVAILABLE',UTC_TIMESTAMP(6));
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
                    administration, databaseName, disposableRoot, database, maintenance, upload, files, options);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async Task InsertSessionAsync(string id, string status, bool expired, string? fileMd5 = null,
            string fileName = "sample.bin", string? fingerprint = null)
        {
            Directory.CreateDirectory(FileStorage.SessionDirectory(StorageRoot, id));
            await using var connection = await Database.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO upload_sessions
                    (id,project_id,uploader_id,file_name,file_size,file_last_modified,file_fingerprint,
                     file_md5,chunk_size,total_chunks,
                     temp_dir,status,result_file_id,expires_at,created_at,updated_at)
                VALUES(@Id,1,1,@FileName,1,1700000000000,@FileFingerprint,
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
                FileFingerprint = fingerprint ?? Sha256Hex([1]),
                FileMd5 = fileMd5,
                FileName = fileName
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

        public static DefaultHttpContext Context(long? contentLength = null)
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(CurrentUser)] = new CurrentUser(1, "T001", "INTERNAL", null);
            context.Request.ContentLength = contentLength;
            return context;
        }

        public static DefaultHttpContext ChunkContext(byte[] content, string? declaredDigest = null)
        {
            var context = Context(content.LongLength);
            context.Request.Headers["X-Chunk-SHA256"] = declaredDigest ?? Sha256Hex(content);
            return context;
        }

        public Task<int> KillConnectionAsync(int serverThread, CancellationToken ct) =>
            administration.ExecuteAsync(new CommandDefinition(
                $"KILL CONNECTION {serverThread}", cancellationToken: ct));

        public async Task<string> StatusAsync(string id)
        {
            await using var connection = await Database.OpenAsync();
            return await connection.QuerySingleAsync<string>(
                "SELECT status FROM upload_sessions WHERE id=@Id", new { Id = id });
        }

        public async Task<DateTime> UpdatedAtAsync(string id, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            return await connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                "SELECT updated_at FROM upload_sessions WHERE id=@Id", new { Id = id }, cancellationToken: ct));
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
