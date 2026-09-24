using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class DatabaseHealthProbeTests
{
    [Fact]
    public async Task HealthyResultIsReusedBrieflyAndFailuresAreNotCached()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var probe = new DatabaseHealthProbe(() => now);
        var calls = 0;
        var up = false;
        Task<bool> Check(CancellationToken _) { calls++; return Task.FromResult(up); }

        Assert.False(await probe.IsUpAsync(Check, ct));
        Assert.False(await probe.IsUpAsync(Check, ct));
        Assert.Equal(2, calls);

        up = true;
        Assert.True(await probe.IsUpAsync(Check, ct));
        up = false;
        Assert.True(await probe.IsUpAsync(Check, ct));
        Assert.Equal(3, calls);

        now += DatabaseHealthProbe.HealthyCacheDuration;
        Assert.False(await probe.IsUpAsync(Check, ct));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneProbeAndAThrowingProbeReportsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new DatabaseHealthProbe();
        var calls = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Slow(CancellationToken _) { Interlocked.Increment(ref calls); return release.Task; }

        var waiting = Enumerable.Range(0, 8).Select(_ => probe.IsUpAsync(Slow, ct)).ToArray();
        release.SetResult(true);
        Assert.All(await Task.WhenAll(waiting), Assert.True);
        Assert.Equal(1, calls);

        var failing = new DatabaseHealthProbe();
        Assert.False(await failing.IsUpAsync(_ => throw new InvalidOperationException("db down"), ct));
    }
}
