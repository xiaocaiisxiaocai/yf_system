using Dapper;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Storage;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// Post-restore relocation hashes the candidate before any reconcile lock is taken and only re-checks "still the
/// same file" under the lock, so a large file never holds the transfer lock while it is read.
/// </summary>
public sealed class OemReconcileRelocationTests
{
    [Fact(Timeout = 240_000)]
    public async Task RelocationRefusesSameSizeContentWhoseHashDiffers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (fileId, _, contentPath, name) = await RestoredCandidateAsync(host, ct);

        // Same length, different bytes: only the hash can tell it is not the recorded content.
        var bytes = await File.ReadAllBytesAsync(contentPath, ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(contentPath, bytes, ct);

        await SetMarkerAsync(host, "RESTORED", ct);
        await host.Service<OemReconcileService>().RunOnceAsync(ct);

        await AssertNotRelocatedAsync(host, fileId, name, ct);
    }

    [Fact(Timeout = 240_000)]
    public async Task RelocationHashesBeforeTakingTheTransferLockAndRechecksTheFileUnderIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (fileId, transferId, contentPath, name) = await RestoredCandidateAsync(host, ct);
        await SetMarkerAsync(host, "RESTORED", ct);

        await using var blocker = await host.OpenAsync(ct);
        await using var transaction = await blocker.BeginTransactionAsync(ct);
        await blocker.ExecuteScalarAsync<ulong>(
            "SELECT id FROM oem_transfers WHERE id=@transferId FOR UPDATE", new { transferId }, transaction);

        var reconcile = Task.Run(() => host.Service<OemReconcileService>().RunOnceAsync(ct), ct);
        // Waiting on the transfer row proves the candidate was already hashed: hashing happens before the lock.
        await LockWaitProbe.WaitForLockWaitAsync(host.ConnectionString, ct, reconcile);
        Assert.False(reconcile.IsCompleted, "Reconcile must be waiting for the transfer lock.");

        // The prepared (already hashed) file changes while reconcile waits. Hashing under the lock would simply
        // read the file again and relocate it; the pre-hashed candidate must instead be refused.
        File.SetLastWriteTimeUtc(contentPath, File.GetLastWriteTimeUtc(contentPath).AddSeconds(-30));
        await transaction.CommitAsync(ct);
        await reconcile;

        await AssertNotRelocatedAsync(host, fileId, name, ct);
    }

    /// <summary>
    /// An uploaded and promoted file whose restored row still points into quarantine while the content sits in the
    /// available area (the backup predates promotion).
    /// </summary>
    private static async Task<(ulong FileId, ulong TransferId, string ContentPath, string StoredName)> RestoredCandidateAsync(
        OemTestHost host, CancellationToken ct)
    {
        var world = await OutboundWorldAsync(host, ct);
        var transferId = TransferId(await CreateOutboundAsync(world, "迁移核对哈希", ct));
        var fileId = (await host.UploadAsync(world.Sender, transferId, "candidate.pdf", OemTestHost.Pdf("relocation candidate"), ct)).Id();
        await host.RunOemJobsAsync(ct);
        var storage = host.Service<OemStorage>();
        await using var conn = await host.OpenAsync(ct);
        var stored = await conn.QuerySingleAsync<(string Name, string Path, string Status)>(
            "SELECT stored_name, storage_path, payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId });
        Assert.Equal("AVAILABLE", stored.Status);
        var contentPath = storage.Absolute(stored.Path);
        Assert.True(File.Exists(contentPath));
        await conn.ExecuteAsync("UPDATE oem_transfer_files SET payload_status='QUARANTINED', storage_path=@path WHERE id=@fileId",
            new { fileId, path = OemStorage.QuarantineRelative(stored.Name) });
        return (fileId, transferId, contentPath, stored.Name);
    }

    private static async Task AssertNotRelocatedAsync(OemTestHost host, ulong fileId, string storedName, CancellationToken ct)
    {
        var storage = host.Service<OemStorage>();
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal("MISSING_UNVERIFIED", await conn.ExecuteScalarAsync<string>(
            "SELECT payload_status FROM oem_transfer_files WHERE id=@fileId", new { fileId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='OEM_RECONCILE_FILE_RELOCATED' AND target_id=@fileId", new { fileId }));
        Assert.False(File.Exists(storage.Absolute(OemStorage.QuarantineRelative(storedName))));
    }

    private static async Task SetMarkerAsync(OemTestHost host, string value, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteAsync(
            "UPDATE system_configs SET cfg_value=@value WHERE cfg_key='oem.storage.reconcile_required'", new { value }));
    }
}
