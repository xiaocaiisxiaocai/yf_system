using System.Net;
using Dapper;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Storage;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>While the post-restore reconcile runs, no storage-changing work may start or finish.</summary>
public sealed class OemStorageReconcileGateTests
{
    [Fact(Timeout = 240_000)]
    public async Task ChunkMergeAndSendAreRefusedUntilReconcileCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "核对期间", ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, "first.pdf", OemTestHost.Pdf("first"), ct);

        var content = OemTestHost.Pdf("second");
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init",
            new { fileName = "second.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        Assert.Equal(1, init["totalChunks"]!.GetValue<int>());

        await SetMarkerAsync(host, "RESTORED", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PutChunkAsync(world.Sender, sessionId, content, ct));
        await world.Sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Status(HttpStatusCode.ServiceUnavailable, 50302);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) }, ct)
            .Status(HttpStatusCode.ServiceUnavailable, 50302);

        await SetMarkerAsync(host, "", ct);
        Assert.Equal(HttpStatusCode.OK, await PutChunkAsync(world.Sender, sessionId, content, ct));
        await world.Sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Ok();
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) }, ct).Ok();
    }

    [Fact(Timeout = 240_000)]
    public async Task RestoreReconcileMovesContentBackToTheRestoredPathInsteadOfDeclaringItMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var id = TransferId(await CreateOutboundAsync(world, "恢复核对", ct));
        var promotedLater = (await host.UploadAsync(world.Sender, id, "promoted.pdf", OemTestHost.Pdf("promoted later"), ct)).Id();
        var lost = (await host.UploadAsync(world.Sender, id, "lost.pdf", OemTestHost.Pdf("really lost"), ct)).Id();
        await host.RunOemJobsAsync(ct);
        var storage = host.Service<OemStorage>();

        // The restored database predates promotion: both rows still point into quarantine.
        await using (var conn = await host.OpenAsync(ct))
        {
            foreach (var fileId in new[] { promotedLater, lost })
            {
                var stored = await conn.QuerySingleAsync<(string Name, string Path, string Status)>(
                    "SELECT stored_name, storage_path, payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId });
                Assert.Equal("AVAILABLE", stored.Status);
                if (fileId == lost) File.Delete(storage.Absolute(stored.Path));
                await conn.ExecuteAsync("UPDATE oem_transfer_files SET payload_status='QUARANTINED', storage_path=@path WHERE id=@fileId",
                    new { fileId, path = OemStorage.QuarantineRelative(stored.Name) });
            }
        }
        await SetMarkerAsync(host, "RESTORED", ct);
        await host.Service<OemReconcileService>().RunOnceAsync(ct);

        await using (var conn = await host.OpenAsync(ct))
        {
            var kept = await conn.QuerySingleAsync<(string Path, string Status)>(
                "SELECT storage_path, payload_status FROM oem_transfer_files WHERE id=@promotedLater", new { promotedLater });
            Assert.Equal("QUARANTINED", kept.Status);
            Assert.True(File.Exists(storage.Absolute(kept.Path)));
            Assert.Equal("MISSING_UNVERIFIED", await conn.ExecuteScalarAsync<string>(
                "SELECT payload_status FROM oem_transfer_files WHERE id=@lost", new { lost }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='OEM_RECONCILE_FILE_RELOCATED' AND target_id=@promotedLater", new { promotedLater }));
            Assert.Equal("", await conn.ExecuteScalarAsync<string>(
                "SELECT cfg_value FROM system_configs WHERE cfg_key='oem.storage.reconcile_required'"));
        }
    }

    private static async Task SetMarkerAsync(OemTestHost host, string value, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteAsync("UPDATE system_configs SET cfg_value=@value WHERE cfg_key='oem.storage.reconcile_required'", new { value }));
    }

    private static async Task<HttpStatusCode> PutChunkAsync(ApiClient client, string sessionId, byte[] content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) };
        using var response = await client.Http.SendAsync(request, ct);
        return response.StatusCode;
    }
}
