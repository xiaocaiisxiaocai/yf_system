using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Downloads, receipts, purge, draft expiry, restore reconciliation and the OEM audit view.</summary>
public sealed class OemDeliveryTests
{
    [Fact]
    public async Task DedicatedOemStorageRootRejectsAReparsePoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = Path.Combine(Path.GetTempPath(), "yf_oem_root_link_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(sandbox, "outside");
        var link = Path.Combine(sandbox, "oem");
        var sentinel = Path.Combine(target, "keep.bin");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(sentinel, "keep", ct);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            var storage = new OemStorage(new AppOptions { OemStorageRoot = link });

            Assert.Throws<InvalidOperationException>(() => storage.Root());
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, ct));
        }
        finally
        {
            if (Directory.Exists(link)
                && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link, recursive: false);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: false);
        }
    }

    [Fact]
    public void RangesMergeWithoutDoubleCountingAndParseStrictly()
    {
        var set = new ByteRangeSet();
        set.Add(0, 9);
        set.Add(0, 9);
        set.Add(20, 29);
        Assert.False(set.Covers(30));
        set.Add(10, 19);
        Assert.True(set.Covers(30));
        Assert.Single(set.Ranges);

        Assert.Equal(new ByteRange(0, 99), ByteRange.Parse(null, 100, out _));
        Assert.Equal(new ByteRange(90, 99), ByteRange.Parse("bytes=-10", 100, out _));
        Assert.Equal(new ByteRange(50, 99), ByteRange.Parse("bytes=50-", 100, out _));
        Assert.Equal(new ByteRange(10, 99), ByteRange.Parse("bytes=10-500", 100, out _));
        ByteRange.Parse("bytes=0-1,5-6", 100, out var multi);
        Assert.True(multi);
        ByteRange.Parse("bytes=100-", 100, out var beyond);
        Assert.True(beyond);
    }

    private static async Task<(ulong TransferId, ulong FileId, byte[] Content)> ReleasedInboundAsync(OutboundWorld world, CancellationToken ct)
    {
        var content = OemTestHost.Pdf(new string('d', 4000));
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传工装" }, ct).Ok();
        var file = await world.Host.UploadAsync(world.Vendor, TransferId(draft), "fixture.pdf", content, ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await world.Host.RunOemJobsAsync(ct);
        return (TransferId(draft), file.Id(), content);
    }

    private static async Task<HttpResponseMessage> DownloadAsync(ApiClient client, ulong fileId, string? range, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/oem/files/{fileId}/download");
        if (range is not null) request.Headers.Range = RangeHeaderValue.Parse(range);
        return await client.Http.SendAsync(request, ct);
    }

    [Fact(Timeout = 240_000)]
    public async Task OnlyACompleteRecipientDownloadCountsAsReceiptAndStartsTheGracePeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        // A newly created policy becomes the unified active policy for new transfers.
        await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "下载后一小时", mode = "AFTER_FIRST_RECEIPT", receiptGraceMinutes = 60 }, ct).Ok();
        var (transferId, fileId, content) = await ReleasedInboundAsync(world, ct);

        // Sender-side reads (the vendor itself) and previews never count.
        await world.Vendor.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok();
        using (var own = await DownloadAsync(world.Vendor, fileId, null, ct)) Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var preview = await world.Viewer.GetAsync($"/api/v1/oem/files/{fileId}/content", ct);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        await using (var conn = await host.OpenAsync(ct))
            Assert.Null(await conn.ExecuteScalarAsync<DateTime?>("SELECT first_recipient_download_at FROM oem_transfer_files WHERE id=@fileId", new { fileId }));

        // A download without the grant cookie is refused; the grant is scoped to the file path.
        using (var anonymous = await DownloadAsync(host.Anonymous(), fileId, null, ct)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var started = await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok();
        Assert.Equal("DOWNLOAD", started["purpose"]!.GetValue<string>());
        using (var first = await DownloadAsync(world.Viewer, fileId, "bytes=0-99", ct))
        {
            Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
            Assert.Equal(content[..100], await first.Content.ReadAsByteArrayAsync(ct));
        }
        using (var repeat = await DownloadAsync(world.Viewer, fileId, "bytes=0-99", ct)) Assert.Equal(HttpStatusCode.PartialContent, repeat.StatusCode);
        using (var invalid = await DownloadAsync(world.Viewer, fileId, "bytes=0-1,5-6", ct)) Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        var status = (await world.Viewer.GetAsync($"/api/v1/oem/files/{fileId}/download-status", ct).Ok()).AsArray()[0]!;
        Assert.Equal("STARTED", status["status"]!.GetValue<string>());
        Assert.Equal(100UL, status["deliveredBytes"]!.GetValue<ulong>());

        using (var rest = await DownloadAsync(world.Viewer, fileId, $"bytes=100-{content.Length - 1}", ct))
            Assert.Equal(content[100..], await rest.Content.ReadAsByteArrayAsync(ct));
        status = (await world.Viewer.GetAsync($"/api/v1/oem/files/{fileId}/download-status", ct).Ok()).AsArray()[0]!;
        Assert.Equal("COMPLETED", status["status"]!.GetValue<string>());

        await using var db = await host.OpenAsync(ct);
        var row = await db.QuerySingleAsync<(DateTime? Receipt, DateTime? Due, string Realm)>(
            "SELECT first_recipient_download_at, purge_due_at, first_recipient_realm FROM oem_transfer_files WHERE id=@fileId", new { fileId });
        Assert.NotNull(row.Receipt);
        Assert.Equal("internal", row.Realm);
        Assert.InRange((row.Due!.Value - row.Receipt!.Value).TotalMinutes, 59.9, 60.1);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_FILE_FIRST_RECEIPT'"));
        Assert.Contains(await db.QueryAsync<string>("SELECT event_type FROM email_outbox"), type => type == "OEM_TRANSFER_RELEASED");
        _ = transferId;
    }

    [Fact(Timeout = 240_000)]
    public async Task ExpiredFilesArePurgedFromDiskAndStayRecordedForAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        // A newly created policy becomes the unified active policy for new transfers.
        await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "发布后一天", mode = "AFTER_RELEASE", releaseTtlMinutes = 1440 }, ct).Ok();
        var (_, fileId, _) = await ReleasedInboundAsync(world, ct);
        string path;
        await using (var conn = await host.OpenAsync(ct))
        {
            path = Path.Combine(host.StorageRoot, "oem", (await conn.ExecuteScalarAsync<string>("SELECT storage_path FROM oem_transfer_files WHERE id=@fileId", new { fileId }))!);
            Assert.True(File.Exists(path));
            await conn.ExecuteAsync("UPDATE oem_transfer_files SET purge_due_at=UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE id=@fileId", new { fileId });
        }
        // Past its purge time the file is refused immediately, before the purge worker runs.
        await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Status(HttpStatusCode.Gone);
        Assert.Equal(1, await host.Service<OemPurgeService>().RunOnceAsync(ct));
        Assert.False(File.Exists(path));
        ulong transferId;
        await using (var conn = await host.OpenAsync(ct))
            transferId = await conn.ExecuteScalarAsync<ulong>("SELECT transfer_id FROM oem_transfer_files WHERE id=@fileId", new { fileId });
        var detail = await world.Viewer.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();
        Assert.Equal("RELEASED", detail["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal(1, detail["summary"]!["purgedCount"]!.GetValue<int>());
        Assert.Equal("PURGED", detail["files"]![0]!["payloadStatus"]!.GetValue<string>());
        Assert.False(detail["files"]![0]!["downloadable"]!.GetValue<bool>());
    }

    [Fact(Timeout = 240_000)]
    public async Task RestoreReconciliationClosesContentMarksMissingFilesAndDeletesOrphans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var (_, lostId, _) = await ReleasedInboundAsync(world, ct);
        var (_, keptId, _) = await ReleasedInboundAsync(world, ct);
        var oemRoot = Path.Combine(host.StorageRoot, "oem");
        await using (var conn = await host.OpenAsync(ct))
            File.Delete(Path.Combine(oemRoot, (await conn.ExecuteScalarAsync<string>("SELECT storage_path FROM oem_transfer_files WHERE id=@lostId", new { lostId }))!));
        var orphan = Path.Combine(oemRoot, "available", "ab", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllTextAsync(orphan, "uploaded after the backup", ct);

        await OemReconcileService.MarkRestoredAsync(new AppDb(host.Service<AppOptions>()), ct);
        await world.Viewer.PostAsync($"/api/v1/oem/files/{keptId}/download-sessions", null, ct).Status(HttpStatusCode.ServiceUnavailable);
        await host.Service<OemReconcileService>().RunOnceAsync(ct);

        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal("MISSING_UNVERIFIED", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@lostId", new { lostId }));
            Assert.Equal("AVAILABLE", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@keptId", new { keptId }));
            Assert.Equal("", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='oem.storage.reconcile_required'"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_RECONCILE_ORPHAN_PURGED'"));
            Assert.Contains(await conn.QueryAsync<string>("SELECT event_type FROM email_outbox"), type => type == "OEM_FILE_MISSING");
        }
        Assert.False(File.Exists(orphan));
        await world.Viewer.PostAsync($"/api/v1/oem/files/{keptId}/download-sessions", null, ct).Ok();
        await world.Viewer.PostAsync($"/api/v1/oem/files/{lostId}/download-sessions", null, ct).Status(HttpStatusCode.Conflict);

        // In normal operation an unexpected loss is STORAGE_LOST.
        await using (var conn = await host.OpenAsync(ct))
            File.Delete(Path.Combine(oemRoot, (await conn.ExecuteScalarAsync<string>("SELECT storage_path FROM oem_transfer_files WHERE id=@keptId", new { keptId }))!));
        await host.Service<OemReconcileService>().RunOnceAsync(ct);
        await using (var conn = await host.OpenAsync(ct))
            Assert.Equal("STORAGE_LOST", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@keptId", new { keptId }));
    }

    [Fact(Timeout = 240_000)]
    public async Task OrphanCleanupNeverFollowsAReparsePointIntoCollaborationStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var oemRoot = Path.Combine(host.StorageRoot, "oem");
        var collaborationDirectory = Path.Combine(host.StorageRoot, "collab", "oem-reparse-regression");
        var sentinel = Path.Combine(collaborationDirectory, Guid.NewGuid().ToString("D"));
        var link = Path.Combine(oemRoot, "available", "external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(collaborationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        await File.WriteAllTextAsync(sentinel, "collaboration content must survive", ct);
        File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-7));

        try
        {
            Directory.CreateSymbolicLink(link, collaborationDirectory);

            await host.Service<OemReconcileService>().RoutineCheckAsync(ct);

            Assert.True(File.Exists(sentinel));
            Assert.Equal("collaboration content must survive", await File.ReadAllTextAsync(sentinel, ct));
        }
        finally
        {
            if (Directory.Exists(link)
                && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link, recursive: false);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task StaleDraftsExpireAndTheOemAuditViewIsSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "久未发送", ct);
        await host.UploadAsync(world.Sender, TransferId(draft), "old.pdf", OemTestHost.Pdf("old"), ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_transfers SET updated_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY WHERE id=@id", new { id = TransferId(draft) });
        Assert.Equal(1, await host.Service<OemPurgeService>().ExpireDraftsAsync(host.Service<OemTransferService>(), ct));
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal("ABANDONED", await conn.ExecuteScalarAsync<string>("SELECT lifecycle_status FROM oem_transfers WHERE id=@id", new { id = TransferId(draft) }));
            Assert.Equal("DRAFT_EXPIRED", await conn.ExecuteScalarAsync<string>("SELECT purge_reason FROM oem_transfer_files WHERE transfer_id=@id", new { id = TransferId(draft) }));
        }

        var oemLogs = (await world.Admin.GetAsync("/api/v1/oem/audit-logs?pageSize=100", ct).Ok())["list"]!.AsArray();
        Assert.Contains(oemLogs, row => row!["action"]!.GetValue<string>() == "OEM_TRANSFER_ABANDON");
        Assert.All(oemLogs, row => Assert.True(row!["action"]!.GetValue<string>().StartsWith("OEM_", StringComparison.Ordinal)
            || row["action"]!.GetValue<string>() == "DEPT_LEADER_CHANGE"));
        Assert.Contains(oemLogs, row => row!["actorRealm"]!.GetValue<string>() == "oem");
        await world.Viewer.GetAsync("/api/v1/oem/audit-logs", ct).Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 240_000)]
    public async Task DraftExpiryRechecksFreshnessAfterTakingTheTransferLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "并发编辑草稿", ct);
        var transferId = TransferId(draft);
        await using var blocker = await host.OpenAsync(ct);
        await blocker.ExecuteAsync("UPDATE oem_transfers SET updated_at=UTC_TIMESTAMP(3) - INTERVAL 40 DAY WHERE id=@transferId", new { transferId });
        await using var tx = await blocker.BeginTransactionAsync(ct);
        await blocker.ExecuteScalarAsync<ulong>("SELECT id FROM oem_transfers WHERE id=@transferId FOR UPDATE", new { transferId }, tx);

        var expiry = host.Service<OemPurgeService>().ExpireDraftsAsync(host.Service<OemTransferService>(), ct);
        await Task.Delay(200, ct);
        Assert.False(expiry.IsCompleted);
        await blocker.ExecuteAsync("UPDATE oem_transfers SET updated_at=UTC_TIMESTAMP(3) WHERE id=@transferId", new { transferId }, tx);
        await tx.CommitAsync(ct);

        Assert.Equal(0, await expiry);
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal("DRAFT", await verify.ExecuteScalarAsync<string>(
            "SELECT lifecycle_status FROM oem_transfers WHERE id=@transferId", new { transferId }));
    }

    [Fact(Timeout = 240_000)]
    public async Task DownloadCompletionTakesTheTransferLockBeforeItsLeaseAndPreservesTheReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var (transferId, fileId, content) = await ReleasedInboundAsync(world, ct);
        var started = await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok();
        var sessionId = started["downloadSessionId"]!.GetValue<string>();
        OemDownloadSession session;
        OemTransferFile file;
        await using (var context = await host.Service<IDbContextFactory<YfDbContext>>().CreateDbContextAsync(ct))
        {
            session = await context.OemDownloadSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId, ct);
            file = await context.OemTransferFiles.AsNoTracking().SingleAsync(item => item.Id == fileId, ct);
        }
        var leaseId = Guid.NewGuid().ToString("D");
        await using (var setup = await host.OpenAsync(ct))
            await setup.ExecuteAsync(@"INSERT INTO oem_download_leases
                (id,session_id,file_id,owner,started_at,last_progress_at,lease_until,hard_deadline,status)
                VALUES (@leaseId,@sessionId,@fileId,'test',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),
                    UTC_TIMESTAMP(3)+INTERVAL 5 MINUTE,UTC_TIMESTAMP(3)+INTERVAL 10 MINUTE,'ACTIVE')",
                new { leaseId, sessionId, fileId });

        await using var blocker = await host.OpenAsync(ct);
        await using var blockerTx = await blocker.BeginTransactionAsync(ct);
        await blocker.ExecuteScalarAsync<ulong>("SELECT id FROM oem_transfers WHERE id=@transferId FOR UPDATE", new { transferId }, blockerTx);
        var completion = host.Service<OemDeliveryService>().FinishAsync(
            leaseId, session, file, new ByteRange(0, (ulong)content.Length - 1), delivered: true, ct);

        await using (var observer = await host.OpenAsync(ct))
        {
            var waiting = false;
            for (var attempt = 0; attempt < 200 && !waiting; attempt++)
            {
                // MySQL 5.7 does not expose every row-lock wait through
                // INNODB_LOCK_WAITS. PROCESSLIST still shows the actual blocked
                // SELECT, which proves FinishAsync reached the transfer lock.
                waiting = await observer.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM information_schema.processlist
                    WHERE id <> CONNECTION_ID() AND db=DATABASE() AND command='Query'
                      AND info LIKE '%oem_transfers%' AND info LIKE '%FOR UPDATE%'") > 0;
                if (!waiting) await Task.Delay(25, ct);
            }
            Assert.True(waiting, "download completion did not reach the blocked transfer-row lock");
        }

        // If completion had locked the lease first, this creates the inverse
        // lease -> transfer / transfer -> lease cycle and MySQL aborts one side.
        Assert.Equal(leaseId, await blocker.ExecuteScalarAsync<string>(
            "SELECT id FROM oem_download_leases WHERE id=@leaseId FOR UPDATE", new { leaseId }, blockerTx));
        await blockerTx.CommitAsync(ct);
        await completion.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await using var verify = await host.OpenAsync(ct);
        var recorded = await verify.QuerySingleAsync<(string Status, DateTime? Receipt, ulong Delivered)>(@"
            SELECT s.status AS Status,f.first_recipient_download_at AS Receipt,
                COALESCE(SUM(r.end_offset-r.start_offset+1),0) AS Delivered
            FROM oem_download_sessions s JOIN oem_transfer_files f ON f.id=s.file_id
            LEFT JOIN oem_download_ranges r ON r.session_id=s.id
            WHERE s.id=@sessionId GROUP BY s.id,f.id", new { sessionId });
        Assert.Equal("COMPLETED", recorded.Status);
        Assert.NotNull(recorded.Receipt);
        Assert.Equal((ulong)content.Length, recorded.Delivered);
    }
}
