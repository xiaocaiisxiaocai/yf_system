using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Uploads;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Regression tests for upload/transfer/delivery races, permission rechecks and existence leaks.</summary>
public sealed class OemFlowHardeningTests
{
    [Fact]
    public void UnparseableRangesAreIgnoredAndOnlyUnsatisfiableRangesAreRefused()
    {
        foreach (var header in new[] { "bytes=abc", "bytes=1-x", "items=0-5", "bytes=5-2", "bytes=-", "bytes=+1-2", "bytes 0-5" })
        {
            Assert.Equal(new ByteRange(0, 99), ByteRange.Parse(header, 100, out var unsatisfiable));
            Assert.False(unsatisfiable, header);
        }
        Assert.Null(ByteRange.Parse("bytes=-0", 100, out var zeroSuffix));
        Assert.True(zeroSuffix);
        Assert.Null(ByteRange.Parse("bytes=100-200", 100, out var beyond));
        Assert.True(beyond);
        Assert.Equal(new ByteRange(5, 9), ByteRange.Parse("bytes=5-9", 100, out _));
    }

    [Fact(Timeout = 240_000)]
    public async Task UnparseableRangeHeaderDownloadsTheWholeFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var content = OemTestHost.Pdf(new string('r', 2000));
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "范围头" }, ct).Ok();
        var file = await host.UploadAsync(world.Vendor, TransferId(draft), "range.pdf", content, ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        await world.Viewer.PostAsync($"/api/v1/oem/files/{file.Id()}/download-sessions", null, ct).Ok();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/oem/files/{file.Id()}/download");
        request.Headers.TryAddWithoutValidation("Range", "bytes=abc-def");
        using var response = await world.Viewer.Http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact(Timeout = 240_000)]
    public async Task CallersWhoCannotSeeASentTransferGetNotFoundInsteadOfForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "存在性", ct);
        var id = TransferId(draft);
        var file = await host.UploadAsync(world.Sender, id, "exists.pdf", OemTestHost.Pdf("exists"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();

        // The vendor cannot see an unreleased outbound transfer: no hint that it exists.
        await world.Vendor.DeleteAsync($"/api/v1/oem/files/{file.Id()}", ct).Status(HttpStatusCode.NotFound);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init", new { fileName = "x.pdf", fileSize = 10UL }, ct)
            .Status(HttpStatusCode.NotFound);
        // An internal viewer may see it, but is not its sender.
        await world.Viewer.DeleteAsync($"/api/v1/oem/files/{file.Id()}", ct).Status(HttpStatusCode.Forbidden);
        // The sender sees it and learns it is frozen.
        await world.Sender.DeleteAsync($"/api/v1/oem/files/{file.Id()}", ct).Status(HttpStatusCode.Conflict);
    }

    [Fact(Timeout = 240_000)]
    public async Task ApprovalDoesNotReleaseATransferWhoseFileWasLostMeanwhile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "审批期间丢失", ct);
        var id = TransferId(draft);
        var file = await host.UploadAsync(world.Sender, id, "lost.pdf", OemTestHost.Pdf("lost"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var task = Assert.Single((await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok())["list"]!.AsArray())!;

        // Reconcile flags the file while the approval is pending.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_transfer_files SET payload_status='STORAGE_LOST' WHERE id=@id", new { id = file.Id() });

        var decided = await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve",
            new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("BLOCKED", decided["summary"]!["lifecycleStatus"]!.GetValue<string>());
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_TRANSFER_RELEASE'"));
    }

    [Fact(Timeout = 240_000)]
    public async Task ChunkUploadAndMergeRecheckTheCreatePermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var userId = await host.CreateInternalUserAsync("zp_revoked", "Revoked#2026x", ["oem:transfer_create", "oem:transfer_view"], world.Org.Section, ct);
        var sender = await host.LoginInternalAsync("zp_revoked", "Revoked#2026x", ct);
        var draft = await sender.PostAsync("/api/v1/oem/transfers", new { title = "撤权", oemCompanyId = world.CompanyId }, ct).Ok();
        var content = OemTestHost.Pdf("revoked");
        var init = await sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init",
            new { fileName = "revoked.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();

        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync(@"DELETE rp FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
                JOIN user_roles ur ON ur.role_id=rp.role_id WHERE ur.user_id=@userId AND p.code='oem:transfer_create'", new { userId });

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) })
        using (var response = await sender.Http.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 240_000)]
    public async Task OptionalChunkDigestHeaderIsVerifiedBeforeTheChunkIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "分片摘要", ct);
        var content = OemTestHost.Pdf(new string('d', 4096));
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init",
            new { fileName = "digest.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        var chunkPath = host.Service<OemStorage>().ChunkPath(sessionId, 0);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var wrong = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("other"u8)).ToLowerInvariant();

        async Task<(HttpStatusCode Status, string Body)> PutAsync(string? header)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) };
            if (header is not null) request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", header);
            using var response = await world.Sender.Http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }

        // Malformed and mismatching digests are refused with 400/40001; nothing is stored.
        foreach (var (header, message) in new[] { ("not-a-digest", "分片 SHA-256 摘要格式无效"), ("", "分片 SHA-256 摘要格式无效"), (wrong, "分片 SHA-256 校验失败") })
        {
            var (status, body) = await PutAsync(header);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("40001", body);
            Assert.Contains(message, body);
            Assert.False(File.Exists(chunkPath));
        }

        // A matching digest (any case) is accepted; a later mismatch never replaces the stored chunk.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(digest.ToUpperInvariant())).Status);
        Assert.True(File.Exists(chunkPath));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(wrong)).Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(chunkPath, ct));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(chunkPath)!, "*.uploading"));

        // Without the header the chunk is accepted unchecked, as before.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(null)).Status);
        await world.Sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Ok();
    }

    [Fact(Timeout = 240_000)]
    public async Task ALateChunkDoesNotResurrectAnAbortedSessionDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "迟到分片", ct);
        var content = OemTestHost.Pdf(new string('c', 64 * 1024));
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init",
            new { fileName = "late.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        var directory = host.Service<OemStorage>().SessionDirectory(sessionId, create: false, ct);

        // The chunk request is admitted and starts writing, then stalls until the session is aborted.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new GatedContent(content, 1024, gate.Task);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = body };
        var upload = world.Sender.Http.SendAsync(request, ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.uploading").Any()))
        {
            Assert.True(DateTime.UtcNow < deadline, "chunk write did not start");
            await Task.Delay(50, ct);
        }
        await world.Sender.DeleteAsync($"/api/v1/oem/uploads/{sessionId}", ct).Ok();
        gate.SetResult();

        using var response = await upload;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(Directory.Exists(directory));
    }

    [Fact(Timeout = 240_000)]
    public async Task ExpirySkipsASessionWhoseMergeIsStillRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "长合并", ct);
        var content = OemTestHost.Pdf("merge");
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init",
            new { fileName = "merge.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) })
        using (var response = await world.Sender.Http.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var directory = host.Service<OemStorage>().SessionDirectory(sessionId, create: false, ct);
        var uploads = host.Service<OemUploadService>();

        await using (var holder = await host.OpenAsync(ct))
        {
            await holder.ExecuteAsync("UPDATE oem_upload_sessions SET status='MERGING', expires_at=UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE id=@sessionId", new { sessionId });
            // A long merge holds the per-session merge lock past the session TTL.
            await using var mergeLock = await MySqlNamedLock.TryAcquireAsync(holder, MySqlNamedLock.Name("oem-merge", holder.Database, sessionId), 0, ct);
            Assert.NotNull(mergeLock);
            Assert.Equal(0, await uploads.ExpireStaleSessionsAsync(ct));
            Assert.Equal("MERGING", await holder.ExecuteScalarAsync<string>("SELECT status FROM oem_upload_sessions WHERE id=@sessionId", new { sessionId }));
            Assert.True(File.Exists(host.Service<OemStorage>().ChunkPath(sessionId, 0)));
        }

        // Once nobody merges any more, the stale session is expired and cleaned up.
        Assert.Equal(1, await uploads.ExpireStaleSessionsAsync(ct));
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal("EXPIRED", await verify.ExecuteScalarAsync<string>("SELECT status FROM oem_upload_sessions WHERE id=@sessionId", new { sessionId }));
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>Request body that sends a prefix, then waits for <paramref name="gate"/> before sending the rest.</summary>
    private sealed class GatedContent(byte[] content, int prefix, Task gate) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(content.AsMemory(0, prefix));
            await stream.FlushAsync();
            await gate;
            await stream.WriteAsync(content.AsMemory(prefix));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }
    }
}
