using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// Streaming bookkeeping: every flushed byte of an interrupted request counts toward the receipt, a running stream
/// ends at the next heartbeat once its login is revoked, and admission locks the transfer before the file row.
/// </summary>
public sealed class OemDeliveryResumeTests
{
    private sealed record Fixture(ulong TransferId, ulong FileId, ulong ViewerId, byte[] Content, InternalOemActor Actor);

    private static async Task<Fixture> ReleasedInboundAsync(OemTestHost host, byte[] content, CancellationToken ct)
    {
        var world = await OutboundWorldAsync(host, ct);
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "断点续传" }, ct).Ok();
        var file = await host.UploadAsync(world.Vendor, TransferId(draft), "resume.pdf", content, ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        await using var conn = await host.OpenAsync(ct);
        var viewerId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='zp_viewer'");
        var loginSession = await conn.ExecuteScalarAsync<string>(
            "SELECT session_id FROM refresh_tokens WHERE user_id=@viewerId AND revoked=0 ORDER BY id DESC LIMIT 1", new { viewerId });
        return new Fixture(TransferId(draft), file.Id(), viewerId, content,
            new InternalOemActor(new CurrentUser(viewerId, "zp_viewer", UserTypes.Internal, null, loginSession), loginSession!));
    }

    /// <summary>Starts a download session and returns its id plus the grant cookie as a request header value.</summary>
    private static async Task<(string SessionId, string Cookie)> StartAsync(OemTestHost host, Fixture fixture, CancellationToken ct)
    {
        var start = new DefaultHttpContext();
        var response = await host.Service<OemDeliveryService>().StartAsync(start, fixture.Actor, fixture.FileId, ct);
        var setCookie = SetCookieHeaderValue.Parse(start.Response.Headers.SetCookie.ToString());
        return (response.DownloadSessionId, $"{setCookie.Name}={setCookie.Value}");
    }

    private static DefaultHttpContext StreamContext(string cookie, Stream body, string? range, out AbortRecorder lifetime)
    {
        var context = new DefaultHttpContext();
        lifetime = new AbortRecorder();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Headers.Cookie = cookie;
        if (range is not null) context.Request.Headers.Range = range;
        context.Response.Body = body;
        return context;
    }

    private static async Task<(ulong Start, ulong End)[]> RangesAsync(OemTestHost host, string sessionId, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return (await conn.QueryAsync<(ulong Start, ulong End)>(
            "SELECT start_offset AS Start, end_offset AS End FROM oem_download_ranges WHERE session_id=@sessionId ORDER BY start_offset",
            new { sessionId })).ToArray();
    }

    [Fact(Timeout = 240_000)]
    public async Task InterruptedRequestsCreditTheirFlushedBytesAndResumingCompletesTheReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var content = OemTestHost.Pdf(new string('p', 3 * 1024 * 1024 + 123));
        var fixture = await ReleasedInboundAsync(host, content, ct);
        var delivery = host.Service<OemDeliveryService>();
        var (sessionId, cookie) = await StartAsync(host, fixture, ct);

        // The connection drops after about 2.5 MiB. Bytes are credited in flushed steps of 1 MiB, never beyond what was accepted.
        var dropping = new DroppingStream(2_500_000);
        var first = StreamContext(cookie, dropping, null, out _);
        await Assert.ThrowsAsync<IOException>(() => delivery.StreamAsync(first, fixture.FileId, ct));
        var (rangeStart, rangeEnd) = Assert.Single(await RangesAsync(host, sessionId, ct));
        Assert.Equal(0UL, rangeStart);
        var delivered = rangeEnd + 1;
        Assert.InRange(delivered, 1UL << 20, (ulong)dropping.Written);
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal("STARTED", await conn.ExecuteScalarAsync<string>("SELECT status FROM oem_download_sessions WHERE id=@sessionId", new { sessionId }));
            Assert.Null(await conn.ExecuteScalarAsync<DateTime?>(
                "SELECT first_recipient_download_at FROM oem_transfer_files WHERE id=@fileId", new { fileId = fixture.FileId }));
        }

        // A repeated, shorter attempt from the start only re-covers bytes that are already recorded.
        var repeat = StreamContext(cookie, new DroppingStream(1_200_000), null, out _);
        await Assert.ThrowsAsync<IOException>(() => delivery.StreamAsync(repeat, fixture.FileId, ct));
        var recorded = Assert.Single(await RangesAsync(host, sessionId, ct));
        Assert.Equal((0UL, rangeEnd), (recorded.Start, recorded.End));

        // Resuming where the client stopped makes the union cover the file: the session completes and the receipt is recorded.
        var rest = new MemoryStream();
        var resumed = StreamContext(cookie, rest, $"bytes={delivered}-", out _);
        await delivery.StreamAsync(resumed, fixture.FileId, ct);
        Assert.Equal(content[(int)delivered..], rest.ToArray());
        Assert.Equal(StatusCodes.Status206PartialContent, resumed.Response.StatusCode);

        await using var verify = await host.OpenAsync(ct);
        Assert.Equal("COMPLETED", await verify.ExecuteScalarAsync<string>("SELECT status FROM oem_download_sessions WHERE id=@sessionId", new { sessionId }));
        var receipt = await verify.QuerySingleAsync<(DateTime? At, ulong? ById)>(
            "SELECT first_recipient_download_at AS At, first_recipient_id AS ById FROM oem_transfer_files WHERE id=@fileId", new { fileId = fixture.FileId });
        Assert.NotNull(receipt.At);
        Assert.Equal(fixture.ViewerId, receipt.ById);
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_FILE_FIRST_RECEIPT'"));

        // Once complete, another pass over the same bytes neither changes the ranges nor records a second receipt.
        var again = StreamContext(cookie, new MemoryStream(), $"bytes={delivered}-", out _);
        await delivery.StreamAsync(again, fixture.FileId, ct);
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_FILE_FIRST_RECEIPT'"));
        var complete = Assert.Single(await RangesAsync(host, sessionId, ct));
        Assert.Equal((0UL, (ulong)content.Length - 1), (complete.Start, complete.End));
    }

    [Fact(Timeout = 240_000)]
    public async Task ARunningStreamEndsAtTheNextHeartbeatOnceItsLoginSessionIsRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var content = OemTestHost.Pdf(new string('s', 80 * 64 * 1024));
        var fixture = await ReleasedInboundAsync(host, content, ct);
        var (sessionId, cookie) = await StartAsync(host, fixture, ct);

        // About 80 writes x 100 ms = 8 s for the whole file; the heartbeat that re-checks the login comes after 5 s.
        var slow = new SlowStream(TimeSpan.FromMilliseconds(100));
        var stream = StreamContext(cookie, slow, null, out var lifetime);
        var running = host.Service<OemDeliveryService>().StreamAsync(stream, fixture.FileId, ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (slow.Written == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the stream did not start");
            await Task.Delay(20, ct);
        }
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE refresh_tokens SET revoked=1 WHERE user_id=@viewerId", new { viewerId = fixture.ViewerId });

        await running.WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.True(lifetime.Aborted, "the stream must be aborted once the login session is gone");
        Assert.True(slow.Written < content.Length, "the whole file was sent although the login had been revoked");
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal("RELEASED", await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM oem_download_leases WHERE session_id=@sessionId", new { sessionId }));
        Assert.Equal("STARTED", await verify.ExecuteScalarAsync<string>("SELECT status FROM oem_download_sessions WHERE id=@sessionId", new { sessionId }));
        var credited = await verify.ExecuteScalarAsync<long>(
            "SELECT CAST(COALESCE(SUM(end_offset-start_offset+1),0) AS SIGNED) FROM oem_download_ranges WHERE session_id=@sessionId", new { sessionId });
        Assert.InRange(credited, 0, slow.Written);
    }

    [Fact(Timeout = 240_000)]
    public async Task StreamAdmissionTakesTheTransferLockBeforeTheFileRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var fixture = await ReleasedInboundAsync(host, OemTestHost.Pdf(new string('l', 4000)), ct);
        var (_, cookie) = await StartAsync(host, fixture, ct);

        await using var blocker = await host.OpenAsync(ct);
        await using var tx = await blocker.BeginTransactionAsync(ct);
        await blocker.ExecuteScalarAsync<ulong>("SELECT id FROM oem_transfers WHERE id=@transferId FOR UPDATE", new { transferId = fixture.TransferId }, tx);
        var body = new MemoryStream();
        var streaming = host.Service<OemDeliveryService>().StreamAsync(StreamContext(cookie, body, null, out _), fixture.FileId, ct);
        await LockWaitProbe.WaitForLockWaitAsync(host.ConnectionString, ct, streaming);
        Assert.False(streaming.IsCompleted);

        // Had admission locked the file row first, this would queue behind it while it waits for the transfer: the
        // inverse order of purge and completion, and a deadlock.
        Assert.Equal(fixture.FileId, await blocker.ExecuteScalarAsync<ulong>(
            "SELECT id FROM oem_transfer_files WHERE id=@fileId FOR UPDATE", new { fileId = fixture.FileId }, tx));
        await tx.CommitAsync(ct);

        await streaming.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(fixture.Content, body.ToArray());
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource aborted = new();
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get => aborted.Token; set { } }

        public void Abort()
        {
            Aborted = true;
            aborted.Cancel();
        }
    }

    /// <summary>A client that reads slowly: each write takes <paramref name="delay"/>.</summary>
    private sealed class SlowStream(TimeSpan delay) : WriteOnlyStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Written += buffer.Length;
        }
    }

    /// <summary>A connection that accepts writes until <paramref name="limit"/> bytes were taken, then fails like a dropped one.</summary>
    private sealed class DroppingStream(long limit) : WriteOnlyStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Written + buffer.Length > limit) throw new IOException("the client disconnected");
            Written += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    private abstract class WriteOnlyStream : Stream
    {
        public long Written { get; protected set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public abstract override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    }
}
