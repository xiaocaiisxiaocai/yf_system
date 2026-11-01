using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Maintenance;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>A temporarily unavailable OEM volume must not mark files lost; purge removes the old storage root's copy first.</summary>
public sealed class OemStorageSafetyTests
{
    private static async Task<(ulong FileId, string Path)[]> ReleasedInboundFilesAsync(OemTestHost host, OutboundWorld world, int count, CancellationToken ct)
    {
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "批量回传" }, ct).Ok();
        var id = TransferId(draft);
        for (var index = 0; index < count; index++)
            await host.UploadAsync(world.Vendor, id, $"bulk-{index}.pdf", OemTestHost.Pdf("bulk" + index), ct);
        var detail = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(detail) }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        await using var conn = await host.OpenAsync(ct);
        return (await conn.QueryAsync<(ulong FileId, string Path)>(
            "SELECT id AS FileId, storage_path AS Path FROM oem_transfer_files WHERE transfer_id=@id ORDER BY id", new { id })).ToArray();
    }

    private static async Task<string[]> PayloadStatusesAsync(OemTestHost host, IEnumerable<ulong> fileIds, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return (await conn.QueryAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id IN @ids ORDER BY id", new { ids = fileIds.ToArray() })).ToArray();
    }

    private static async Task<int> SuspectAlertsAsync(OemTestHost host, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_RECONCILE_STORAGE_SUSPECT'");
    }

    [Fact(Timeout = 240_000)]
    public async Task RoutineCheckMarksNothingLostWhileTheVolumeIsUnreadableOrFilesVanishInBulk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var files = await ReleasedInboundFilesAsync(host, world, OemReconcileService.MassLossMinimumFiles, ct);
        Assert.Equal(OemReconcileService.MassLossMinimumFiles, files.Length);
        var ids = files.Select(file => file.FileId).ToArray();
        Assert.All(await PayloadStatusesAsync(host, ids, ct), status => Assert.Equal("AVAILABLE", status));
        var oemRoot = Path.Combine(host.StorageRoot, "oem");
        string Absolute(string relative) => Path.Combine(oemRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var backups = files.ToDictionary(file => file.FileId, file => File.ReadAllBytes(Absolute(file.Path)));
        var reconcile = host.Service<OemReconcileService>();

        // The area holding the files is not there (an unmounted or half-restored volume): nothing is marked.
        var available = Path.Combine(oemRoot, "available");
        var parked = Path.Combine(oemRoot, "available-parked");
        Directory.Move(available, parked);
        Assert.Equal(0, await reconcile.RoutineCheckAsync(ct));
        Directory.Move(parked, available);
        Assert.All(await PayloadStatusesAsync(host, ids, ct), status => Assert.Equal("AVAILABLE", status));
        Assert.Equal(1, await SuspectAlertsAsync(host, ct));

        // A sound check clears the outage, so the next one is alerted again.
        Assert.Equal(0, await reconcile.RoutineCheckAsync(ct));
        foreach (var file in files) File.Delete(Absolute(file.Path));
        Assert.Equal(0, await reconcile.RoutineCheckAsync(ct));
        Assert.All(await PayloadStatusesAsync(host, ids, ct), status => Assert.Equal("AVAILABLE", status));
        Assert.Equal(2, await SuspectAlertsAsync(host, ct));

        // Below the mass-loss threshold a loss is still flagged, as before.
        foreach (var file in files) File.WriteAllBytes(Absolute(file.Path), backups[file.FileId]);
        foreach (var file in files.Take(OemReconcileService.MassLossMinimumFiles - 1)) File.Delete(Absolute(file.Path));
        Assert.Equal(OemReconcileService.MassLossMinimumFiles - 1, await reconcile.RoutineCheckAsync(ct));
        var statuses = await PayloadStatusesAsync(host, ids, ct);
        Assert.Equal(OemReconcileService.MassLossMinimumFiles - 1, statuses.Count(status => status == "STORAGE_LOST"));
        Assert.Single(statuses, status => status == "AVAILABLE");
        Assert.Equal(2, await SuspectAlertsAsync(host, ct));
    }

    [Fact(Timeout = 240_000)]
    public async Task PurgeRemovesTheOldRootCopyFirstAndStaysPendingWhileAnyCopyRemains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var (fileId, relative) = Assert.Single(await ReleasedInboundFilesAsync(host, world, 1, ct));
        var options = host.Service<AppOptions>();
        var oldRoot = Path.Combine(host.StorageRoot, "oem-old");
        var local = relative.Replace('/', Path.DirectorySeparatorChar);
        var current = Path.Combine(host.StorageRoot, "oem", local);
        var old = Path.Combine(oldRoot, local);
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        // A copy in the old root that cannot be deleted (a directory stands where the file should be).
        Directory.CreateDirectory(old);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_transfer_files SET purge_due_at=UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE id=@fileId", new { fileId });

        options.OemStorageFallbackRoot = oldRoot;
        try
        {
            var purge = host.Service<OemPurgeService>();
            // The old root is handled first and its failure stops the purge: the new root's copy stays, so a
            // migration cannot copy anything back, and the row is not reported as purged.
            Assert.Equal(0, await purge.RunOnceAsync(ct));
            Assert.True(File.Exists(current), "the current copy must outlive a failed delete of the old copy");
            await using (var conn = await host.OpenAsync(ct))
            {
                Assert.Equal("PURGE_PENDING", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId }));
                await conn.ExecuteAsync("UPDATE oem_transfer_files SET purge_next_attempt_at=NULL WHERE id=@fileId", new { fileId });
            }

            // With a deletable old copy next to the current one, a retry removes both and only then marks it purged.
            Directory.Delete(old);
            File.Copy(current, old);
            Assert.Equal(1, await purge.RunOnceAsync(ct));
            Assert.False(File.Exists(current));
            Assert.False(File.Exists(old));
            await using var verify = await host.OpenAsync(ct);
            Assert.Equal("PURGED", await verify.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId }));
        }
        finally
        {
            options.OemStorageFallbackRoot = null;
            try { Directory.Delete(oldRoot, recursive: true); } catch { }
        }
    }
}
