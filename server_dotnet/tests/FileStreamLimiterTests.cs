using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class FileStreamLimiterTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TransferWatchdogLimits TestLimits =
        new(0, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

    [Fact]
    public void FilesModuleSharesOneLimiterAcrossFileServiceScopes()
    {
        var services = new ServiceCollection();
        services.AddFilesModule();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<FileStreamLimiter>(), provider.GetRequiredService<FileStreamLimiter>());
    }

    [Fact]
    public void ConcurrentStreamsAreBoundedPerUserAndGloballyAndReleaseIdempotently()
    {
        var limiter = new FileStreamLimiter();
        var oneUser = Enumerable.Range(0, FileStreamLimiter.MaximumConcurrentPerUser)
            .Select(_ => limiter.Acquire(7, Now)).ToArray();
        AssertTooMany(() => limiter.Acquire(7, Now));

        oneUser[0].Dispose();
        oneUser[0].Dispose();
        using (limiter.Acquire(7, Now)) { }
        foreach (var lease in oneUser) lease.Dispose();

        var global = Enumerable.Range(0, FileStreamLimiter.MaximumConcurrentGlobal)
            .Select(index => limiter.Acquire((ulong)(100 + index / FileStreamLimiter.MaximumConcurrentPerUser), Now))
            .ToArray();
        AssertTooMany(() => limiter.Acquire(999, Now));
        foreach (var lease in global) lease.Dispose();
        using (limiter.Acquire(999, Now)) { }
    }

    [Fact]
    public void ActualStreamRequestsHaveAPerAccountMinuteBudget()
    {
        var limiter = new FileStreamLimiter();
        for (var index = 0; index < FileStreamLimiter.MaximumRequestsPerMinute; index++)
            limiter.Acquire(7, Now).Dispose();

        AssertTooMany(() => limiter.Acquire(7, Now));
        using (limiter.Acquire(7, Now.AddMinutes(1))) { }
    }

    [Fact]
    public async Task CompletedAndCancelledResponsesReleaseTheirConcurrencyLease()
    {
        var limiter = new FileStreamLimiter();
        var completed = new GuardedFileResult(new CompletedResult(), limiter.Acquire(7, Now), TestLimits);
        await completed.ExecuteAsync(Context(TestContext.Current.CancellationToken));
        AssertFullUserCapacity(limiter, 7);

        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Context(request.Token);
        var cancelled = new GuardedFileResult(new WaitingResult(entered), limiter.Acquire(8, Now), TestLimits);
        var execution = cancelled.ExecuteAsync(context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        request.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AssertFullUserCapacity(limiter, 8);
    }

    [Fact]
    public async Task DeadlineAbortsAStuckResponseAndReleasesItsLease()
    {
        var limiter = new FileStreamLimiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortDeadline = new TransferWatchdogLimits(
            0, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        var result = new GuardedFileResult(new WaitingResult(entered), limiter.Acquire(9, Now), shortDeadline);

        var execution = result.ExecuteAsync(Context(TestContext.Current.CancellationToken));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AssertFullUserCapacity(limiter, 9);
    }

    [Fact]
    public async Task StalledResponseIsAbortedAndReleasesItsLease()
    {
        var limiter = new FileStreamLimiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stallOnly = new TransferWatchdogLimits(1, TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        var result = new GuardedFileResult(new WaitingResult(entered), limiter.Acquire(11, Now), stallOnly);

        var execution = result.ExecuteAsync(Context(TestContext.Current.CancellationToken));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await execution.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AssertFullUserCapacity(limiter, 11);
    }

    [Fact]
    public async Task BytesWrittenThroughTheResponseKeepASlowTransferAlive()
    {
        var limiter = new FileStreamLimiter();
        var context = Context(TestContext.Current.CancellationToken);
        var originalBody = new MemoryStream();
        context.Response.Body = originalBody;
        // Window 25x the write interval, so a loaded test machine still sees progress in every window.
        var limits = new TransferWatchdogLimits(1, TimeSpan.FromMilliseconds(500), TimeSpan.FromHours(1));

        await new GuardedFileResult(new TrickleResult(8, TimeSpan.FromMilliseconds(20)), limiter.Acquire(12, Now), limits)
            .ExecuteAsync(context);

        Assert.Equal(8, originalBody.Length);
        Assert.Same(originalBody, context.Response.Body);
        AssertFullUserCapacity(limiter, 12);
    }

    [Fact]
    public async Task GuardPreservesAspNetRangeResponses()
    {
        var limiter = new FileStreamLimiter();
        var content = new byte[] { 0, 1, 2, 3, 4, 5 };
        var response = new MemoryStream();
        var context = Context(TestContext.Current.CancellationToken);
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=2-4";
        context.Response.Body = response;
        var inner = Results.File(new MemoryStream(content), "application/octet-stream", enableRangeProcessing: true);

        await new GuardedFileResult(inner, limiter.Acquire(10, Now), TestLimits).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 2-4/6", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(new byte[] { 2, 3, 4 }, response.ToArray());
        AssertFullUserCapacity(limiter, 10);
    }

    private static DefaultHttpContext Context(CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext();
        context.RequestAborted = requestAborted;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return context;
    }

    private static void AssertFullUserCapacity(FileStreamLimiter limiter, ulong userId)
    {
        var leases = Enumerable.Range(0, FileStreamLimiter.MaximumConcurrentPerUser)
            .Select(_ => limiter.Acquire(userId, Now)).ToArray();
        AssertTooMany(() => limiter.Acquire(userId, Now));
        foreach (var lease in leases) lease.Dispose();
    }

    private static void AssertTooMany(Func<IDisposable> operation)
    {
        var error = Assert.Throws<ApiException>(() => operation());
        Assert.Equal(StatusCodes.Status429TooManyRequests, error.Status);
        Assert.Equal(42902, error.Code);
    }

    private sealed class CompletedResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    /// <summary>Writes one byte per delay; aborts (throws) if the watchdog cancels the request.</summary>
    private sealed class TrickleResult(int bytes, TimeSpan delay) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            for (var index = 0; index < bytes; index++)
            {
                await Task.Delay(delay, httpContext.RequestAborted);
                await httpContext.Response.Body.WriteAsync(new byte[] { 1 }, httpContext.RequestAborted);
            }
        }
    }

    private sealed class WaitingResult(TaskCompletionSource entered) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, httpContext.RequestAborted);
        }
    }
}
