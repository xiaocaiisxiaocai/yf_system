using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class IdentityProjectionCacheTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AccessClaims Claims = new(17, "cache-user", "session-family", new DateTimeOffset(Now.AddHours(1)).ToUnixTimeSeconds());

    [Fact]
    public async Task SameRevisionAndSessionReuseProjectionAcrossRequests()
    {
        var cache = new IdentityProjectionCache();
        var loads = 0;
        Task<IdentityProjection?> Load(CancellationToken _) =>
            Task.FromResult<IdentityProjection?>(Projection(Now.AddMinutes(30), Interlocked.Increment(ref loads)));

        var first = await cache.ResolveAsync(new(4, Now), Claims, Load, TestContext.Current.CancellationToken);
        var second = await cache.ResolveAsync(new(4, Now.AddSeconds(1)), Claims, Load, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task DatabaseClockExpiresCachedSessionWithoutWaitingForAnApplicationTtl()
    {
        var cache = new IdentityProjectionCache();
        var loads = 0;
        Task<IdentityProjection?> Load(CancellationToken _) =>
            Task.FromResult<IdentityProjection?>(Projection(Now.AddSeconds(5), Interlocked.Increment(ref loads)));

        await cache.ResolveAsync(new(7, Now), Claims, Load, TestContext.Current.CancellationToken);
        var expired = await cache.ResolveAsync(new(7, Now.AddSeconds(5)), Claims, Load, TestContext.Current.CancellationToken);

        Assert.Equal(2, loads);
        Assert.NotNull(expired);
        Assert.False(expired.SessionActive);
    }

    [Fact]
    public async Task NewRevisionInvalidatesPreviouslyCachedProjection()
    {
        var cache = new IdentityProjectionCache();
        var loads = 0;
        Task<IdentityProjection?> Load(CancellationToken _) =>
            Task.FromResult<IdentityProjection?>(Projection(Now.AddMinutes(30), Interlocked.Increment(ref loads)));

        await cache.ResolveAsync(new(10, Now), Claims, Load, TestContext.Current.CancellationToken);
        var refreshed = await cache.ResolveAsync(new(11, Now.AddSeconds(1)), Claims, Load, TestContext.Current.CancellationToken);

        Assert.Equal(2, loads);
        Assert.Equal("cache-user-2", refreshed!.EmployeeNo);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task OlderInflightLoadCannotRepopulateCacheAfterNewRevisionWasObserved()
    {
        var cache = new IdentityProjectionCache();
        var oldLoad = new TaskCompletionSource<IdentityProjection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequest = cache.ResolveAsync(new(20, Now), Claims, _ => oldLoad.Task, TestContext.Current.CancellationToken);

        var current = await cache.ResolveAsync(new(21, Now.AddSeconds(1)), Claims,
            _ => Task.FromResult<IdentityProjection?>(Projection(Now.AddMinutes(30), 21)), TestContext.Current.CancellationToken);
        oldLoad.SetResult(Projection(Now.AddMinutes(30), 20));
        var staleResult = await oldRequest;

        var unexpectedLoad = false;
        var currentAgain = await cache.ResolveAsync(new(21, Now.AddSeconds(2)), Claims, _ =>
        {
            unexpectedLoad = true;
            return Task.FromResult<IdentityProjection?>(null);
        }, TestContext.Current.CancellationToken);

        Assert.Equal("cache-user-20", staleResult!.EmployeeNo);
        Assert.Same(current, currentAgain);
        Assert.False(unexpectedLoad);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task CacheRemainsBoundedWhenManySignedSessionKeysAreSeen()
    {
        var cache = new IdentityProjectionCache(2);
        for (var index = 0; index < 5; index++)
        {
            var claims = Claims with { SessionId = $"session-{index}" };
            await cache.ResolveAsync(new(30, Now), claims,
                _ => Task.FromResult<IdentityProjection?>(Projection(Now.AddMinutes(30), index)),
                TestContext.Current.CancellationToken);
            Assert.InRange(cache.Count, 1, 2);
        }
    }

    private static IdentityProjection Projection(DateTime validUntil, int version) => new(
        Claims.UserId, $"cache-user-{version}", UserTypes.Internal, null,
        AccountStatuses.Active, false, false, validUntil);
}
