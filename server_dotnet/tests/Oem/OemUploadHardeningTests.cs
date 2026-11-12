using System.Net;
using Dapper;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Uploads;
using Yf.Api.Modules.Oem.Validation;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Validation queue fairness between vendors, the per-vendor backlog cap and chunk writes racing a merge.</summary>
public sealed class OemUploadHardeningTests
{
    [Fact(Timeout = 240_000)]
    public async Task ValidationCandidatesAlternateBetweenVendorsInsteadOfFollowingUploadOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var second = await host.CreateVendorAsync(world.Admin, "第二代工厂", "vendor_two", ct);

        // The first vendor uploads three files before the second one uploads three: strict age order would
        // validate all of the first vendor's files before any of the second's.
        foreach (var (vendor, title) in new[] { (world.Vendor, "第一家回传"), (second, "第二家回传") })
        {
            var draft = await vendor.PostAsync("/api/v1/oem/transfers", new { title }, ct).Ok();
            for (var index = 0; index < 3; index++)
                await host.UploadAsync(vendor, TransferId(draft), $"queue-{index}.pdf", OemTestHost.Pdf(title + index), ct);
        }

        var picked = await host.Service<OemFileValidationService>().SelectCandidatesAsync(ct);
        Assert.Equal(OemFileValidationService.BatchSize, picked.Length);
        var companies = new List<ulong>();
        await using (var conn = await host.OpenAsync(ct))
            foreach (var jobId in picked)
                companies.Add(await conn.ExecuteScalarAsync<ulong>(
                    "SELECT t.oem_company_id FROM oem_file_scan_jobs j JOIN oem_transfer_files f ON f.id=j.file_id " +
                    "JOIN oem_transfers t ON t.id=f.transfer_id WHERE j.id=@jobId", new { jobId }));
        Assert.NotEqual(companies[0], companies[1]);
        Assert.Equal(new[] { companies[0], companies[1], companies[0], companies[1] }, companies.ToArray());

        // Everything still gets validated.
        await host.RunOemJobsAsync(ct);
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal(6, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_transfer_files WHERE scan_status='VALID'"));
    }

    [Fact(Timeout = 240_000)]
    public async Task UploadRulesTellEachSideItsAllowedExtensions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        foreach (var client in new[] { world.Admin, world.Vendor })
        {
            var rules = await client.GetAsync("/api/v1/oem/upload-rules", ct).Ok();
            var extensions = rules["allowedExtensions"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            Assert.Contains("pdf", extensions);
            Assert.DoesNotContain("exe", extensions);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task UploadInitIsRefusedWhileAVendorHasTooManyFilesAwaitingValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "积压回传" }, ct).Ok();
        var id = TransferId(draft);
        await host.UploadAsync(world.Vendor, id, "first.pdf", OemTestHost.Pdf("first"), ct);

        // Pad the vendor's backlog up to the limit with copies of the pending job (the worker is not running).
        await using (var conn = await host.OpenAsync(ct))
            for (var copy = 1; copy < OemUploadService.MaxUnfinishedValidationsPerCompany; copy++)
                await conn.ExecuteAsync(
                    "INSERT INTO oem_file_scan_jobs(file_id,file_sha256,file_size_bytes,status,attempt_count,next_attempt_at,concurrency_version,created_at) " +
                    "SELECT file_id,file_sha256,file_size_bytes,'PENDING',0,NULL,0,created_at FROM oem_file_scan_jobs LIMIT 1");

        var init = new { fileName = "next.pdf", fileSize = 1000UL };
        var refused = await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init", init, ct).Status(HttpStatusCode.Conflict);
        Assert.Contains("等待校验", refused.Body);

        // Another vendor is not affected by this vendor's backlog.
        var other = await host.CreateVendorAsync(world.Admin, "无积压厂商", "vendor_idle", ct);
        var otherDraft = await other.PostAsync("/api/v1/oem/transfers", new { title = "无积压回传" }, ct).Ok();
        await other.PostAsync($"/api/v1/oem/transfers/{TransferId(otherDraft)}/uploads/init", init, ct).Ok();

        // Finished jobs no longer count.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_file_scan_jobs SET status='VALID'");
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init", init, ct).Ok();
    }

    [Fact(Timeout = 240_000)]
    public async Task AChunkThatLosesTheRaceAgainstTheMergeStartIsRefusedAndNeverBecomesAChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "合并竞态", ct);
        var content = OemTestHost.Pdf(new string('r', 3000));
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init",
            new { fileName = "race.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        var chunkPath = host.Service<OemStorage>().ChunkPath(sessionId, 0);

        // The merge holds the session row lock; the chunk is admitted (the session is still UPLOADING), writes its
        // body and then waits for that same lock to swap itself in.
        await using var merge = await host.OpenAsync(ct);
        await using var mergeTx = await merge.BeginTransactionAsync(ct);
        await merge.ExecuteScalarAsync<string>("SELECT id FROM oem_upload_sessions WHERE id=@sessionId FOR UPDATE", new { sessionId }, mergeTx);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) };
        var upload = world.Sender.Http.SendAsync(request, ct);
        await LockWaitProbe.WaitForLockWaitAsync(host.ConnectionString, ct, upload);
        Assert.False(upload.IsCompleted);

        // The merge starts first: MERGING is committed before the chunk may swap itself in.
        await merge.ExecuteAsync("UPDATE oem_upload_sessions SET status='MERGING' WHERE id=@sessionId", new { sessionId }, mergeTx);
        await mergeTx.CommitAsync(ct);

        using var response = await upload;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(chunkPath), "a chunk must not appear once the merge has started");
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(chunkPath)!, "*.uploading"));
    }
}
