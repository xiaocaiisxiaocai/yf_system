using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Uploads;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// The merge's final transaction moves the merged bytes into quarantine before it commits. When
/// CommitAsync faults, the outcome is decided by a fresh read: a commit that really landed keeps
/// the stored file (its row points at it); a commit that never happened deletes it.
/// </summary>
public sealed class OemMergeCommitUncertaintyTests
{
    [Theory(Timeout = 240_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeKeepsTheStoredFileOnlyWhenTheFaultedCommitActuallyLanded(bool commitLanded)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "提交结果不确定", ct);
        var transferId = TransferId(draft);
        var content = OemTestHost.Pdf(new string('m', 3000));
        var init = await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/uploads/init",
            new { fileName = "uncertain.pdf", fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/0") { Content = new ByteArrayContent(content) })
        using (var response = await world.Sender.Http.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fault = new MergeCommitFault(afterCommit: commitLanded);
        var storage = host.Service<OemStorage>();
        var uploads = new OemUploadService(OemTestDbFactory.With(host, fault), host.Service<AppDb>(), storage,
            host.Service<OemAuditWriter>(), NullLogger<OemUploadService>.Instance);
        var actor = new InternalOemActor(new CurrentUser(world.SenderId, "zp_sender", UserTypes.Internal, null), "merge-fault");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => uploads.MergeAsync(actor, sessionId, ct));
        Assert.True(IsInjected(error), error.ToString());
        Assert.Equal(1, fault.Fired);
        Assert.False(File.Exists(Path.Combine(storage.SessionDirectory(sessionId, create: false, ct), "merged.tmp")));

        await using var conn = await host.OpenAsync(ct);
        var session = await conn.QuerySingleAsync<(string Status, ulong? ResultFileId, ulong ReservedBytes)>(
            "SELECT status, result_file_id, reserved_bytes FROM oem_upload_sessions WHERE id=@sessionId", new { sessionId });
        var files = (await conn.QueryAsync<(ulong Id, string StoragePath, string Sha256)>(
            "SELECT id, storage_path, sha256 FROM oem_transfer_files WHERE transfer_id=@transferId", new { transferId })).ToArray();
        var quarantined = QuarantinedFiles(storage);
        if (commitLanded)
        {
            // The row committed: the file it references must survive and match, and the session is completed.
            var file = Assert.Single(files);
            Assert.Equal("COMPLETED", session.Status);
            Assert.Equal(file.Id, session.ResultFileId);
            Assert.Equal(0UL, session.ReservedBytes);
            var stored = storage.Absolute(file.StoragePath);
            Assert.True(File.Exists(stored), "a committed file row must never point at a deleted file");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), file.Sha256);
            Assert.Equal(content, await File.ReadAllBytesAsync(stored, ct));
            Assert.Equal([Path.GetFullPath(stored)], quarantined);
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_file_scan_jobs WHERE file_id=@id", new { id = file.Id }));
            // A client retry observes the completed merge instead of producing a second file.
            var retried = await world.Sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Ok();
            Assert.Equal(file.Id, retried.Id());
        }
        else
        {
            // Nothing committed: no row, no stray file, and the session is merge-able again.
            Assert.Empty(files);
            Assert.Empty(quarantined);
            Assert.Equal("UPLOADING", session.Status);
            Assert.Null(session.ResultFileId);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM oem_file_scan_jobs j JOIN oem_transfer_files f ON f.id=j.file_id WHERE f.transfer_id=@transferId", new { transferId }));
            var retried = await world.Sender.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Ok();
            var stored = await conn.ExecuteScalarAsync<string>("SELECT storage_path FROM oem_transfer_files WHERE id=@id", new { id = retried.Id() });
            Assert.Equal(content, await File.ReadAllBytesAsync(storage.Absolute(stored!), ct));
            Assert.Single(QuarantinedFiles(storage));
        }
    }

    private static string[] QuarantinedFiles(OemStorage storage)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(storage.Absolute(OemStorage.QuarantineRelative(OemStorage.NewStoredName()))))!;
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray()
            : [];
    }

    private static bool IsInjected(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is InjectedCommitFault) return true;
        return false;
    }

    private sealed class InjectedCommitFault(string message) : Exception(message);

    /// <summary>Faults the merge's file-creating commit once, either before it reaches MySQL or after MySQL committed it.</summary>
    private sealed class MergeCommitFault(bool afterCommit) : DbTransactionInterceptor
    {
        private int fired;
        public int Fired => Volatile.Read(ref fired);

        private bool ShouldFire(DbContext? context) =>
            context is not null && context.ChangeTracker.Entries<OemTransferFile>().Any() && Interlocked.CompareExchange(ref fired, 1, 0) == 0;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && ShouldFire(eventData.Context)) throw new InjectedCommitFault("commit failed before reaching the server");
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit && ShouldFire(eventData.Context)) throw new InjectedCommitFault("commit landed but its acknowledgement was lost");
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A context factory for the OEM test host's database with extra EF interceptors, so a test can
/// construct one OEM service with injected database faults or clocks while the HTTP host stays normal.
/// </summary>
internal static class OemTestDbFactory
{
    public static IDbContextFactory<YfDbContext> With(OemTestHost host, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(AppDb.BuildConnectionString(host.Service<AppOptions>()), EfDb.ServerVersion)
            .AddInterceptors([UtcDatabaseSession.Instance, .. interceptors])
            .Options;
        return new Factory(options);
    }

    private sealed class Factory(DbContextOptions<YfDbContext> options) : IDbContextFactory<YfDbContext>
    {
        public YfDbContext CreateDbContext() => new(options);
    }
}
