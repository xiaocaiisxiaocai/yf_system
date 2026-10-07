using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Storage;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// Download grants, cookies and stream cut-offs are enforced against this process's clock while the
/// session deadline lives on the database clock. They must be derived from DB-clock durations so that,
/// whichever way the clocks disagree, the app-clock expiry is never later than the DB-clock deadline.
/// The database clock is skewed per connection with MySQL's session <c>timestamp</c>, which
/// UTC_TIMESTAMP (the OEM clock) follows.
/// </summary>
public sealed class OemDeliveryClockSkewTests
{
    public static TheoryData<int> SkewHours => new() { 13, -13 };

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(SkewHours))]
    public async Task GrantAndCookieExpiryFollowTheDatabaseDeadlineUnderClockSkew(int skewHours)
    {
        var ct = TestContext.Current.CancellationToken;
        var skew = TimeSpan.FromHours(skewHours);
        await using var host = await OemTestHost.StartAsync(ct);
        var fixture = await ReleasedInboundAsync(host, OemTestHost.Pdf(new string('k', 4000)), ct);
        var factory = OemTestDbFactory.With(host, new SkewedDatabaseClock(skew));
        await AssertSkewedAsync(factory, skew, ct);
        var delivery = Delivery(host, factory);

        var before = DateTimeOffset.UtcNow;
        var start = new DefaultHttpContext();
        var response = await delivery.StartAsync(start, fixture.Actor, fixture.FileId, ct);
        var after = DateTimeOffset.UtcNow;

        await using var conn = await host.OpenAsync(ct);
        var session = await conn.QuerySingleAsync<(DateTime CreatedAt, DateTime AbsoluteDeadline)>(
            "SELECT created_at, absolute_deadline FROM oem_download_sessions WHERE id=@id", new { id = response.DownloadSessionId });
        // The DB-clock answer: never past the session deadline, and the grant lifetime caps it.
        Assert.True(response.ExpiresAt <= session.AbsoluteDeadline);
        var remaining = response.ExpiresAt - session.CreatedAt;
        Assert.InRange(remaining, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));

        var setCookie = SetCookieHeaderValue.Parse(start.Response.Headers.SetCookie.ToString());
        Assert.Equal(OemDownloadGrantService.CookieName(fixture.FileId), setCookie.Name.ToString());
        var cookieExpires = Assert.NotNull(setCookie.Expires);
        // Cookie dates have second precision; the upper bound is the one that matters.
        Assert.True(cookieExpires <= after + remaining, $"cookie {cookieExpires:O} outlives the DB deadline ({after + remaining:O} in app clock)");
        Assert.True(cookieExpires >= before + remaining - TimeSpan.FromSeconds(1), $"cookie {cookieExpires:O} expires too early");
        Assert.True((cookieExpires - DateTime.SpecifyKind(response.ExpiresAt, DateTimeKind.Utc)).Duration() > TimeSpan.FromHours(12),
            "the cookie must not reuse the DB-clock timestamp as an app-clock time");

        var grant = host.Service<OemDownloadGrantService>().Parse(Uri.UnescapeDataString(setCookie.Value.ToString()), fixture.FileId, before);
        Assert.InRange(grant.ExpiresAt, (before + remaining).ToUnixTimeSeconds() - 1, (after + remaining).ToUnixTimeSeconds());

        // Streaming under the same skew neither refuses a valid grant nor cuts a short download off.
        var stream = StreamContext(setCookie, Stream.Null, out var lifetime);
        var body = new MemoryStream();
        stream.Response.Body = body;
        await delivery.StreamAsync(stream, fixture.FileId, ct);
        Assert.False(lifetime.Aborted);
        Assert.Equal(StatusCodes.Status200OK, stream.Response.StatusCode);
        Assert.Equal(fixture.Content, body.ToArray());
        var lease = await conn.QuerySingleAsync<(DateTime HardDeadline, string Status)>(
            "SELECT hard_deadline, status FROM oem_download_leases WHERE session_id=@id", new { id = response.DownloadSessionId });
        Assert.True(lease.HardDeadline <= session.AbsoluteDeadline);
        Assert.Equal(DownloadLeaseStatuses.Released, lease.Status);
        Assert.Equal(DownloadSessionStatuses.Completed, await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM oem_download_sessions WHERE id=@id", new { id = response.DownloadSessionId }));
    }

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(SkewHours))]
    public async Task StreamIsCutOffAtTheDatabaseHardDeadlineUnderClockSkew(int skewHours)
    {
        var ct = TestContext.Current.CancellationToken;
        var skew = TimeSpan.FromHours(skewHours);
        await using var host = await OemTestHost.StartAsync(ct);
        var content = OemTestHost.Pdf(new string('c', 80 * 64 * 1024));
        var fixture = await ReleasedInboundAsync(host, content, ct);
        var factory = OemTestDbFactory.With(host, new SkewedDatabaseClock(skew));
        await AssertSkewedAsync(factory, skew, ct);
        var delivery = Delivery(host, factory);

        // The file is due a few seconds from now on the DB clock with no drain period, so the
        // session's hard deadline is that DB-clock instant.
        var window = TimeSpan.FromSeconds(6);
        await using var conn = await host.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE system_configs SET cfg_value='0' WHERE cfg_key='oem.download.purge_drain_minutes'");
        await conn.ExecuteAsync("UPDATE oem_transfer_files SET purge_due_at=@due WHERE id=@id",
            new { due = DateTime.UtcNow + skew + window, id = fixture.FileId });

        var start = new DefaultHttpContext();
        var response = await delivery.StartAsync(start, fixture.Actor, fixture.FileId, ct);
        var setCookie = SetCookieHeaderValue.Parse(start.Response.Headers.SetCookie.ToString());
        var stream = StreamContext(setCookie, new SlowStream(TimeSpan.FromMilliseconds(200)), out var lifetime);
        var clock = Stopwatch.StartNew();
        await delivery.StreamAsync(stream, fixture.FileId, ct);
        clock.Stop();

        // About 80 writes x 200 ms = 16 s for the whole file; the cut-off must come at the
        // ~6 s DB deadline (never at a deadline shifted by the 13 h clock difference).
        Assert.True(lifetime.Aborted, "the stream must be aborted at the hard deadline");
        Assert.True(clock.Elapsed < window + TimeSpan.FromSeconds(4), $"stream ran {clock.Elapsed} past a {window} deadline");
        var written = ((SlowStream)stream.Response.Body).Written;
        Assert.True(written < content.Length, "the whole file was delivered past the hard deadline");
        var session = await conn.QuerySingleAsync<(DateTime AbsoluteDeadline, string Status)>(
            "SELECT absolute_deadline, status FROM oem_download_sessions WHERE id=@id", new { id = response.DownloadSessionId });
        Assert.Equal(DownloadSessionStatuses.Started, session.Status);
        var lease = await conn.QuerySingleAsync<(DateTime HardDeadline, string Status)>(
            "SELECT hard_deadline, status FROM oem_download_leases WHERE session_id=@id", new { id = response.DownloadSessionId });
        Assert.True(lease.HardDeadline <= session.AbsoluteDeadline);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_download_ranges WHERE session_id=@id", new { id = response.DownloadSessionId }));
    }

    private sealed record Fixture(ulong FileId, byte[] Content, InternalOemActor Actor);

    private static async Task<Fixture> ReleasedInboundAsync(OemTestHost host, byte[] content, CancellationToken ct)
    {
        var world = await OutboundWorldAsync(host, ct);
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "时钟偏差" }, ct).Ok();
        var file = await host.UploadAsync(world.Vendor, TransferId(draft), "skew.pdf", content, ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        await using var conn = await host.OpenAsync(ct);
        var viewerId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='zp_viewer'");
        var loginSession = await conn.ExecuteScalarAsync<string>(
            "SELECT session_id FROM refresh_tokens WHERE user_id=@viewerId AND revoked=0 ORDER BY id DESC LIMIT 1", new { viewerId });
        return new Fixture(file.Id(), content,
            new InternalOemActor(new CurrentUser(viewerId, "zp_viewer", UserTypes.Internal, null, loginSession), loginSession!));
    }

    private static OemDeliveryService Delivery(OemTestHost host, Microsoft.EntityFrameworkCore.IDbContextFactory<YfDbContext> factory) =>
        new(factory, host.Service<OemStorage>(), host.Service<OemDownloadGrantService>(), host.Service<OemDownloadRateLimiter>(),
            host.Service<OemEventDispatcher>(), host.Service<OemAuditWriter>(), host.Service<AppOptions>(), NullLogger<OemDeliveryService>.Instance);

    private static async Task AssertSkewedAsync(Microsoft.EntityFrameworkCore.IDbContextFactory<YfDbContext> factory, TimeSpan skew, CancellationToken ct)
    {
        await using var read = await OemUnitOfWork.ReadAsync(factory, ct);
        Assert.InRange(read.Now - DateTime.UtcNow, skew - TimeSpan.FromMinutes(1), skew + TimeSpan.FromMinutes(1));
    }

    private static DefaultHttpContext StreamContext(SetCookieHeaderValue grantCookie, Stream body, out AbortRecorder lifetime)
    {
        var context = new DefaultHttpContext();
        lifetime = new AbortRecorder();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Headers.Cookie = $"{grantCookie.Name}={grantCookie.Value}";
        context.Response.Body = body;
        return context;
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
    private sealed class SlowStream(TimeSpan delay) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            Written += buffer.Length;
        }
    }

    /// <summary>Freezes each new connection's clock at app time + skew (MySQL session <c>timestamp</c>).</summary>
    private sealed class SkewedDatabaseClock(TimeSpan skew) : DbConnectionInterceptor
    {
        private string Statement() =>
            "SET SESSION timestamp = " + ((DateTimeOffset.UtcNow + skew).ToUnixTimeMilliseconds() / 1000m).ToString("0.000", CultureInfo.InvariantCulture);

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = Statement();
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Statement();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
