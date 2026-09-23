using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class DownloadGrantSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GrantIsSingleUseAndSessionIsBoundToTheOriginalScope()
    {
        var service = new DownloadGrantService();
        var ids = new ulong[] { 7, 9, 7 };
        var issue = service.Issue(42, "session-family", ids, batch: true, Now);

        Assert.True(DownloadGrantService.IsValidHandle(issue.Handle));
        Assert.False(issue.Handle.Contains(issue.Secret, StringComparison.Ordinal));
        var session = service.Redeem(issue.Handle, issue.Secret, Now.AddSeconds(1));
        Assert.Equal((ulong)42, session.UserId);
        Assert.Equal("session-family", session.AuthSessionId);
        Assert.Equal(new ulong[] { 7, 9 }, session.FileIds.ToArray());
        Assert.True(session.Batch);
        Assert.Throws<ApiException>(() => service.Redeem(issue.Handle, issue.Secret, Now.AddSeconds(2)));

        var resumed = service.GetSession(issue.Handle, session.Secret, Now.AddMinutes(14));
        Assert.Equal(session.FileIds.ToArray(), resumed.FileIds.ToArray());
        Assert.Throws<ApiException>(() => service.GetSession(issue.Handle, session.Secret, Now.AddMinutes(15).AddSeconds(1)));
    }

    [Fact]
    public void GrantExpiresAndSeparateDownloadsUseSeparateCookieNames()
    {
        var service = new DownloadGrantService();
        var first = service.Issue(1, "s", [1], batch: false, Now);
        var second = service.Issue(1, "s", [2], batch: false, Now);

        Assert.NotEqual(first.Handle, second.Handle);
        Assert.NotEqual(DownloadGrantService.GrantCookieName(first.Handle),
            DownloadGrantService.GrantCookieName(second.Handle));
        Assert.NotEqual(DownloadGrantService.SessionCookieName(first.Handle),
            DownloadGrantService.SessionCookieName(second.Handle));
        Assert.Throws<ApiException>(() => service.Redeem(first.Handle, first.Secret,
            Now.AddSeconds(DownloadGrantService.GrantLifetimeSeconds)));
    }

    [Fact]
    public void DownloadGrantEntriesAreBoundedPerUserAndExpiredEntriesReleaseTheQuota()
    {
        var service = new DownloadGrantService();
        for (var index = 0; index < 32; index++)
            service.Issue(99, "same-session", [(ulong)index + 1], batch: false, Now);

        Assert.Throws<ApiException>(() => service.Issue(99, "same-session", [999], batch: false, Now));
        service.Issue(100, "other-session", [1000], batch: false, Now);
        service.Issue(99, "same-session", [1001], batch: false,
            Now.AddSeconds(DownloadGrantService.GrantLifetimeSeconds));
    }

    [Fact]
    public async Task AuditDeduplicationSerializesConcurrentWritesAndIsTimeBounded()
    {
        var service = new DownloadGrantService();
        var issue = service.Issue(1, "s", [7], batch: false, Now);
        var session = service.Redeem(issue.Handle, issue.Secret, Now);

        var first = await service.AcquireSessionAuditAsync(session.Handle, TestContext.Current.CancellationToken);
        Assert.True(first.ShouldWrite);
        var concurrent = service.AcquireSessionAuditAsync(session.Handle, TestContext.Current.CancellationToken).AsTask();
        Assert.False(concurrent.IsCompleted);
        first.Complete();
        first.Dispose();
        using (var duplicate = await concurrent) Assert.False(duplicate.ShouldWrite);

        using (var preview = await service.AcquireWindowAuditAsync(
                   "preview:1:s:7", TestContext.Current.CancellationToken, Now))
        {
            Assert.True(preview.ShouldWrite);
            preview.Complete();
        }
        using (var duplicate = await service.AcquireWindowAuditAsync(
                   "preview:1:s:7", TestContext.Current.CancellationToken, Now.AddMinutes(1)))
            Assert.False(duplicate.ShouldWrite);
        using (var renewed = await service.AcquireWindowAuditAsync(
                   "preview:1:s:7", TestContext.Current.CancellationToken, Now.AddMinutes(3)))
            Assert.True(renewed.ShouldWrite);
    }

    [Fact]
    public async Task FailedAuditLeaseLetsTheNextConcurrentRequestWriteTheAudit()
    {
        var service = new DownloadGrantService();
        var issue = service.Issue(1, "s", [7], batch: false, Now);
        var session = service.Redeem(issue.Handle, issue.Secret, Now);

        var failed = await service.AcquireSessionAuditAsync(session.Handle, TestContext.Current.CancellationToken);
        var retry = service.AcquireSessionAuditAsync(session.Handle, TestContext.Current.CancellationToken).AsTask();
        failed.Dispose();
        using var next = await retry;
        Assert.True(next.ShouldWrite);
    }

    [Fact]
    public async Task TenConcurrentRangeRequestsShareOneSessionAndOneAudit()
    {
        var service = new DownloadGrantService();
        var issue = service.Issue(1, "s", [7], batch: false, Now);
        var session = service.Redeem(issue.Handle, issue.Secret, Now);
        var auditWriters = 0;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var resumed = service.GetSession(issue.Handle, session.Secret, Now.AddMinutes(1));
            Assert.Equal((ulong)7, Assert.Single(resumed.FileIds));
            using var audit = await service.AcquireSessionAuditAsync(
                resumed.Handle, TestContext.Current.CancellationToken);
            if (!audit.ShouldWrite) return;
            Interlocked.Increment(ref auditWriters);
            await Task.Yield();
            audit.Complete();
        }));

        Assert.Equal(1, auditWriters);
    }

    [Theory]
    [InlineData("GET", "/api/v1/files/7/native-download/0123456789abcdef0123456789abcdef", true)]
    [InlineData("GET", "/api/v1/files/batch-download/0123456789abcdef0123456789abcdef", true)]
    [InlineData("POST", "/api/v1/files/7/native-download/0123456789abcdef0123456789abcdef", false)]
    [InlineData("GET", "/api/v1/files/0/native-download/0123456789abcdef0123456789abcdef", false)]
    [InlineData("GET", "/api/v1/files/7/native-download/not-a-handle", false)]
    [InlineData("GET", "/api/v1/files/7/native-download/0123456789abcdef0123456789abcdef/extra", false)]
    [InlineData("GET", "/api/v1/files/batch-download/0123456789ABCDEF0123456789ABCDEF", false)]
    public void IdentityBypassIsLimitedToExactNativeDownloadRoutes(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(expected, IdentityMiddleware.IsNativeDownloadRequest(context.Request));
    }

    [Fact]
    public void BatchLimiterOnlyLimitsConcurrencyAndReleasesIdempotently()
    {
        var limiter = new BatchDownloadLimiter();
        var first = limiter.Acquire(1);
        Assert.Throws<ApiException>(() => limiter.Acquire(1));
        var second = limiter.Acquire(2);
        Assert.Throws<ApiException>(() => limiter.Acquire(3));

        first.Dispose();
        first.Dispose();
        using var third = limiter.Acquire(3);
        second.Dispose();
    }
}
