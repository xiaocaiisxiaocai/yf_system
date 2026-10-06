using Dapper;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Background-job resilience: shutdown release, poisoned-item isolation, storage debris and session-table cleanup.</summary>
public sealed class OemWorkerResilienceTests
{
    [Fact]
    public void ItemBackoffSuppressesAFailingItemAndForgetsItAfterSuccess()
    {
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var backoff = new OemItemBackoff<ulong>(() => now);
        backoff.Failed(7);
        Assert.Equal([7UL], backoff.Suppressed());
        now = now.AddSeconds(31);
        Assert.Empty(backoff.Suppressed());
        backoff.Failed(7);
        now = now.AddSeconds(45);
        Assert.Equal([7UL], backoff.Suppressed()); // the second failure doubles the delay to 60 s
        backoff.Succeeded(7);
        Assert.Empty(backoff.Suppressed());
    }

    [Fact(Timeout = 240_000)]
    public async Task ReleasedValidationClaimIsImmediatelyClaimableAndStaleReleasesAreFenced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var transferId = TransferId(await CreateOutboundAsync(world, "停机释放", ct));
        var fileId = (await host.UploadAsync(world.Sender, transferId, "release.pdf", OemTestHost.Pdf("release"), ct)).Id();
        ulong jobId;
        await using (var conn = await host.OpenAsync(ct))
            jobId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM oem_file_scan_jobs WHERE file_id=@fileId", new { fileId });

        var validation = host.Service<OemFileValidationService>();
        var claim = await validation.ClaimAsync(jobId, ct);
        Assert.NotNull(claim);
        var factory = host.Service<IDbContextFactory<YfDbContext>>();
        await using (var context = await factory.CreateDbContextAsync(ct))
        {
            var running = await context.OemFileScanJobs.AsNoTracking().SingleAsync(job => job.Id == jobId, ct);
            Assert.Equal(ValidationJobStatuses.Running, running.Status);
            Assert.NotNull(running.LeaseUntil);
            Assert.Equal(ValidationStatuses.Validating,
                await context.OemTransferFiles.Where(file => file.Id == fileId).Select(file => file.ScanStatus).SingleAsync(ct));
        }

        await validation.ReleaseAsync(claim!);
        await using (var context = await factory.CreateDbContextAsync(ct))
        {
            var released = await context.OemFileScanJobs.AsNoTracking().SingleAsync(job => job.Id == jobId, ct);
            Assert.Equal(ValidationJobStatuses.Pending, released.Status);
            Assert.Null(released.LeaseOwner);
            Assert.Null(released.LeaseUntil);
            Assert.Null(released.NextAttemptAt);
            Assert.Equal(0, released.AttemptCount);
            Assert.Equal(claim!.Version + 1, released.ConcurrencyVersion);
            Assert.Equal(ValidationStatuses.Pending,
                await context.OemTransferFiles.Where(file => file.Id == fileId).Select(file => file.ScanStatus).SingleAsync(ct));
        }

        // A second claim wins; the first (stale) claim can neither release nor complete it.
        var second = await validation.ClaimAsync(jobId, ct);
        Assert.NotNull(second);
        await validation.ReleaseAsync(claim!);
        await using (var context = await factory.CreateDbContextAsync(ct))
        {
            var job = await context.OemFileScanJobs.AsNoTracking().SingleAsync(item => item.Id == jobId, ct);
            Assert.Equal(ValidationJobStatuses.Running, job.Status);
            Assert.Equal(second!.Version, job.ConcurrencyVersion);
        }
        await validation.ReleaseAsync(second!);

        await host.RunOemJobsAsync(ct);
        await using (var conn = await host.OpenAsync(ct))
            Assert.Equal("AVAILABLE", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId }));
    }

    [Fact(Timeout = 240_000)]
    public async Task APoisonedDraftDoesNotBlockTheOtherDraftsFromExpiring()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var poisoned = TransferId(await CreateOutboundAsync(world, "坏草稿", ct));
        var healthy = TransferId(await CreateOutboundAsync(world, "好草稿", ct));
        Assert.True(poisoned < healthy);
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE oem_transfers SET updated_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY WHERE id IN @ids",
                new { ids = new[] { poisoned, healthy } });
            await conn.ExecuteAsync($"""
                CREATE TRIGGER trg_test_poison_draft BEFORE UPDATE ON oem_transfers FOR EACH ROW
                BEGIN
                  IF OLD.id = {poisoned} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'poisoned draft'; END IF;
                END
                """);
        }

        var purge = host.Service<OemPurgeService>();
        var transfers = host.Service<OemTransferService>();
        Assert.Equal(1, await purge.ExpireDraftsAsync(transfers, ct));
        // The poisoned row is backed off instead of failing the batch again.
        Assert.Equal(0, await purge.ExpireDraftsAsync(transfers, ct));

        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal("DRAFT", await conn.ExecuteScalarAsync<string>("SELECT lifecycle_status FROM oem_transfers WHERE id=@poisoned", new { poisoned }));
            Assert.Equal("ABANDONED", await conn.ExecuteScalarAsync<string>("SELECT lifecycle_status FROM oem_transfers WHERE id=@healthy", new { healthy }));
            await conn.ExecuteAsync("DROP TRIGGER trg_test_poison_draft");
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task AStrayFileInTheUploadsAreaDoesNotStopUploadCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var uploads = Path.Combine(host.StorageRoot, "oem", "uploads");
        Directory.CreateDirectory(uploads);
        var stray = Path.Combine(uploads, "stray.txt");
        var freshStray = Path.Combine(uploads, "fresh.txt");
        var staleSession = Path.Combine(uploads, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(staleSession);
        await File.WriteAllTextAsync(Path.Combine(staleSession, "0.part"), "chunk", ct);
        await File.WriteAllTextAsync(stray, "debris", ct);
        await File.WriteAllTextAsync(freshStray, "recent", ct);
        var old = DateTime.UtcNow.AddDays(-7);
        File.SetLastWriteTimeUtc(stray, old);
        File.SetLastWriteTimeUtc(Path.Combine(staleSession, "0.part"), old);
        Directory.SetLastWriteTimeUtc(staleSession, old);

        await host.Service<OemReconcileService>().RoutineCheckAsync(ct);

        Assert.False(File.Exists(stray));
        Assert.False(Directory.Exists(staleSession));
        Assert.True(File.Exists(freshStray));
    }

    [Fact(Timeout = 240_000)]
    public async Task SessionCleanupRemovesOnlyLongExpiredTokensAndFinishedDownloadSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await host.LoginOemAsync("vendor_out", "Vendor#2026", ct);

        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "清理会话", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var fileId = (await host.UploadAsync(world.Vendor, TransferId(draft), "session.pdf", OemTestHost.Pdf("session"), ct)).Id();
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var oldSession = (await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok())["downloadSessionId"]!.GetValue<string>();
        var liveSession = (await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok())["downloadSessionId"]!.GetValue<string>();

        ulong accountId;
        ulong[] tokenIds;
        await using (var conn = await host.OpenAsync(ct))
        {
            accountId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM oem_accounts WHERE employee_no='vendor_out'");
            tokenIds = (await conn.QueryAsync<ulong>("SELECT id FROM oem_refresh_tokens WHERE account_id=@accountId ORDER BY id", new { accountId })).ToArray();
            Assert.True(tokenIds.Length >= 2);
            await conn.ExecuteAsync("""
                UPDATE oem_refresh_tokens SET expires_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY,
                    session_expires_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY, revoked=1 WHERE id=@id
                """, new { id = tokenIds[0] });
            await conn.ExecuteAsync("""
                UPDATE oem_download_sessions SET status='COMPLETED', completed_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY,
                    absolute_deadline=UTC_TIMESTAMP(3) - INTERVAL 40 DAY WHERE id=@oldSession
                """, new { oldSession });
            await conn.ExecuteAsync("""
                INSERT INTO oem_download_ranges(session_id, start_offset, end_offset) VALUES(@oldSession, 0, 9)
                """, new { oldSession });
        }

        var removed = await host.Service<OemSessionCleanupService>().RunOnceAsync(ct);
        Assert.Equal(2, removed);
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_refresh_tokens WHERE id=@id", new { id = tokenIds[0] }));
            Assert.Equal(tokenIds.Length - 1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM oem_refresh_tokens WHERE account_id=@accountId", new { accountId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_download_sessions WHERE id=@oldSession", new { oldSession }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_download_ranges WHERE session_id=@oldSession", new { oldSession }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_download_sessions WHERE id=@liveSession", new { liveSession }));
        }
        Assert.Equal(0, await host.Service<OemSessionCleanupService>().RunOnceAsync(ct));
    }
}
