using System.Net;
using Dapper;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Maintenance;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Per-account budgets of the entry points that write a row on every call, and cleanup of finished upload sessions.</summary>
public sealed class OemWriteEntryLimitTests
{
    [Fact]
    public void ScopedBudgetsAreIndependentOfEachOtherAndOfDownloadStreaming()
    {
        var limiter = new OemDownloadRateLimiter();
        for (var call = 0; call < OemDownloadRateLimiter.EntryPerMinute; call++)
            Assert.True(limiter.Allow(OemRealms.Oem, 7, OemDownloadRateLimiter.EntryPerMinute, OemDownloadRateLimiter.PreviewScope));
        Assert.False(limiter.Allow(OemRealms.Oem, 7, OemDownloadRateLimiter.EntryPerMinute, OemDownloadRateLimiter.PreviewScope));

        // Another scope, another account and the unscoped streaming budget are untouched.
        Assert.True(limiter.Allow(OemRealms.Oem, 7, OemDownloadRateLimiter.EntryPerMinute, OemDownloadRateLimiter.DownloadStartScope));
        Assert.True(limiter.Allow(OemRealms.Oem, 8, OemDownloadRateLimiter.EntryPerMinute, OemDownloadRateLimiter.PreviewScope));
        Assert.True(limiter.Allow(OemRealms.Oem, 7, 1));
        Assert.False(limiter.Allow(OemRealms.Oem, 7, 1));
    }

    [Fact(Timeout = 240_000)]
    public async Task PreviewDownloadStartAndUploadInitAnswer429OnceTheirAccountBudgetIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "限流回传" }, ct).Ok();
        var fileId = (await host.UploadAsync(world.Vendor, TransferId(inbound), "limit.pdf", OemTestHost.Pdf("limit"), ct)).Id();
        await world.Vendor.VendorSendAsync(TransferId(inbound), Version(inbound) + 1, ct).Ok();
        await host.RunOemJobsAsync(ct);
        ulong viewerId;
        await using (var conn = await host.OpenAsync(ct))
            viewerId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='zp_viewer'");
        var limiter = host.Service<OemDownloadRateLimiter>();

        // One call of the scope was already made over HTTP; the rest of the minute's budget is spent directly.
        void SpendRestOfBudget(ulong actorId, string scope, int budget = OemDownloadRateLimiter.EntryPerMinute)
        {
            for (var call = 1; call < budget; call++)
                Assert.True(limiter.Allow(OemRealms.Internal, actorId, budget, scope));
        }

        // Normal use passes, and each entry point has its own budget.
        await world.Viewer.GetAsync($"/api/v1/oem/files/{fileId}/content", ct).Status(HttpStatusCode.OK);
        SpendRestOfBudget(viewerId, OemDownloadRateLimiter.PreviewScope);
        await world.Viewer.GetAsync($"/api/v1/oem/files/{fileId}/content", ct).Status(HttpStatusCode.TooManyRequests, 42902);
        await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok();
        SpendRestOfBudget(viewerId, OemDownloadRateLimiter.DownloadStartScope, OemDownloadRateLimiter.TransferStartPerMinute);
        await world.Viewer.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Status(HttpStatusCode.TooManyRequests, 42902);

        var draft = await CreateOutboundAsync(world, "限流上传", ct);
        var init = new { fileName = "init.pdf", fileSize = 1000UL };
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init", init, ct).Ok();
        // Upload and download starts have the smaller budget shared with collaboration transfers.
        SpendRestOfBudget(world.SenderId, OemDownloadRateLimiter.UploadInitScope, OemDownloadRateLimiter.TransferStartPerMinute);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/uploads/init", init, ct).Status(HttpStatusCode.TooManyRequests, 42902);
        // The streaming budget itself is separate and not spent.
        Assert.True(limiter.Allow(OemRealms.Internal, viewerId, 600));
    }

    [Fact(Timeout = 240_000)]
    public async Task AbortedAndExpiredUploadSessionsAreRemovedAfterAWeekButOpenOnesNever()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "上传会话清理", ct);
        var transferId = TransferId(draft);

        async Task<string> InitAsync(string name) =>
            (await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/uploads/init", new { fileName = name, fileSize = 1000UL }, ct).Ok())["sessionId"]!.GetValue<string>();

        var oldAborted = await InitAsync("a.pdf");
        var freshAborted = await InitAsync("b.pdf");
        var open = await InitAsync("c.pdf");
        await world.Sender.DeleteAsync($"/api/v1/oem/uploads/{oldAborted}", ct).Ok();
        await world.Sender.DeleteAsync($"/api/v1/oem/uploads/{freshAborted}", ct).Ok();
        var oldExpired = await InitAsync("d.pdf");
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE oem_upload_sessions SET status='EXPIRED' WHERE id=@oldExpired", new { oldExpired });
            await conn.ExecuteAsync("UPDATE oem_upload_sessions SET updated_at=UTC_TIMESTAMP(3) - INTERVAL 8 DAY WHERE id IN @ids",
                new { ids = new[] { oldAborted, oldExpired, open } });
        }

        await host.Service<OemSessionCleanupService>().RunOnceAsync(ct);

        await using var verify = await host.OpenAsync(ct);
        var remaining = (await verify.QueryAsync<string>("SELECT id FROM oem_upload_sessions WHERE transfer_id=@transferId", new { transferId })).ToHashSet();
        Assert.DoesNotContain(oldAborted, remaining);
        Assert.DoesNotContain(oldExpired, remaining);
        Assert.Contains(freshAborted, remaining);
        Assert.Contains(open, remaining);
    }
}
