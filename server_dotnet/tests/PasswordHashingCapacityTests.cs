using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class PasswordHashingCapacityTests
{
    [Fact]
    public void HashingScalesWithCoresButStaysMemoryBounded()
    {
        Assert.InRange(PasswordService.HashConcurrency, 2, 8);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 2, 8), PasswordService.HashConcurrency);
        Assert.True(PasswordService.HashQueueTimeout > TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcurrentLoginsVerifyInParallelWithoutRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var hash = await PasswordService.HashAsync("Correct#2026", ct);
        var results = await Task.WhenAll(Enumerable.Range(0, PasswordService.HashConcurrency * 2)
            .Select(index => PasswordService.VerifyAsync(index % 2 == 0 ? "Correct#2026" : "Wrong#2026", hash, ct)));
        Assert.Equal(Enumerable.Range(0, results.Length).Select(index => index % 2 == 0), results);
    }
}
