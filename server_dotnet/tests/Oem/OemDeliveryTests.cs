using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Transfers;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Downloads, receipts, purge, draft expiry, restore reconciliation and the OEM audit view.</summary>
public sealed class OemDeliveryTests
{
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

    private static async Task<(ulong TransferId, ulong FileId, byte[] Content)> ReleasedInboundAsync(OutboundWorld world, ulong retentionTemplateId, CancellationToken ct)
    {
        var content = OemTestHost.Pdf(new string('d', 4000));
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传工装", retentionTemplateId }, ct).Ok();
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
        var retention = await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "下载后一小时", mode = "AFTER_FIRST_RECEIPT", receiptGraceMinutes = 60 }, ct).Ok();
        var (transferId, fileId, content) = await ReleasedInboundAsync(world, retention.Id(), ct);

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
        var retention = await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "发布后一天", mode = "AFTER_RELEASE", releaseTtlMinutes = 1440 }, ct).Ok();
        var (_, fileId, _) = await ReleasedInboundAsync(world, retention.Id(), ct);
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
        var (_, lostId, _) = await ReleasedInboundAsync(world, world.KeepTemplateId, ct);
        var (_, keptId, _) = await ReleasedInboundAsync(world, world.KeepTemplateId, ct);
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
        Assert.All(oemLogs, row => Assert.True(row!["action"]!.GetValue<string>().StartsWith("OEM_") || row["action"]!.GetValue<string>() == "DEPT_LEADER_CHANGE"));
        Assert.Contains(oemLogs, row => row!["actorRealm"]!.GetValue<string>() == "oem");
        await world.Viewer.GetAsync("/api/v1/oem/audit-logs", ct).Status(HttpStatusCode.Forbidden);
    }
}
