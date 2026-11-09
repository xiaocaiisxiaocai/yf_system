using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

/// <summary>
/// Bounds actual collaboration file responses, including Range requests made with an already-issued cookie.
/// One budget is shared by downloads and previews so an account cannot move pressure between endpoints.
/// Supplier logins are shared by a vendor's staff, so the per-account budget must cover several people at once;
/// stalled transfers are reclaimed by <see cref="GuardedFileResult"/> rather than by a tight concurrency cap.
/// </summary>
public sealed class FileStreamLimiter
{
    internal const int MaximumConcurrentPerUser = 16;
    internal const int MaximumConcurrentGlobal = 128;
    internal const int MaximumRequestsPerMinute = 300;
    private const int MaximumWindows = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private readonly Dictionary<ulong, int> activeByUser = [];
    private readonly Dictionary<ulong, RequestWindow> requestWindows = [];
    private int activeGlobal;

    public IDisposable Acquire(ulong userId) => Acquire(userId, DateTime.UtcNow);

    internal IDisposable Acquire(ulong userId, DateTime now)
    {
        lock (gate)
        {
            if (activeGlobal >= MaximumConcurrentGlobal
                || activeByUser.GetValueOrDefault(userId) >= MaximumConcurrentPerUser)
                throw ApiException.TooManyRequests("同时进行的文件传输已达上限，请稍后重试");

            if (requestWindows.Count >= MaximumWindows && !requestWindows.ContainsKey(userId))
            {
                foreach (var stale in requestWindows.Where(item => now - item.Value.Start >= Window).Select(item => item.Key).ToArray())
                    requestWindows.Remove(stale);
                if (requestWindows.Count >= MaximumWindows)
                    throw ApiException.TooManyRequests("文件请求繁忙，请稍后重试");
            }

            var window = requestWindows.GetValueOrDefault(userId);
            window = window is null || now - window.Start >= Window
                ? new RequestWindow(now, 1)
                : window with { Count = window.Count + 1 };
            requestWindows[userId] = window;
            if (window.Count > MaximumRequestsPerMinute)
                throw ApiException.TooManyRequests("文件请求过于频繁，请稍后重试");

            activeGlobal++;
            activeByUser[userId] = activeByUser.GetValueOrDefault(userId) + 1;
            return new Lease(this, userId);
        }
    }

    private void Release(ulong userId)
    {
        lock (gate)
        {
            if (!activeByUser.TryGetValue(userId, out var active)) return;
            activeGlobal--;
            if (active == 1) activeByUser.Remove(userId);
            else activeByUser[userId] = active - 1;
        }
    }

    private sealed record RequestWindow(DateTime Start, int Count);

    private sealed class Lease(FileStreamLimiter owner, ulong userId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(userId);
        }
    }
}

/// <summary>
/// Holds a stream-concurrency lease for the complete HTTP response and aborts a response whose client stops
/// accepting data (fewer than 64 KiB in a minute, e.g. a paused video or a dead connection), so the slot is
/// reclaimed; a slow but progressing download is never cut off before the generous hard deadline.
/// The wrapped file result retains ASP.NET Core's normal conditional and Range semantics.
/// </summary>
internal sealed class GuardedFileResult(
    IResult inner,
    IDisposable lease,
    TransferWatchdogLimits? limits = null) : IResult
{
    internal static readonly TransferWatchdogLimits DefaultLimits =
        new(64 * 1024, TransferWatchdogLimits.DefaultWindow, TimeSpan.FromHours(24));
    private static readonly Action<ILogger, TransferWatchdogVerdict, Exception?> LogWatchdogAbort =
        LoggerMessage.Define<TransferWatchdogVerdict>(
            LogLevel.Warning, new EventId(1, nameof(GuardedFileResult)),
            "Single file response aborted by watchdog ({Verdict}).");

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        using var _ = lease;
        var originalAbort = httpContext.RequestAborted;
        using var watchdog = new TransferWatchdog(limits ?? DefaultLimits, originalAbort);
        using var abortOnDeadline = watchdog.Token.Register(() =>
        {
            if (watchdog.Verdict != TransferWatchdogVerdict.Healthy) httpContext.Abort();
        });
        httpContext.RequestAborted = watchdog.Token;
        var originalBody = httpContext.Response.Body;
        httpContext.Response.Body = new ProgressStream(originalBody, watchdog);
        try
        {
            await inner.ExecuteAsync(httpContext);
        }
        catch (Exception error) when (ExpectedTermination(error, watchdog, originalAbort))
        {
            if (watchdog.Verdict != TransferWatchdogVerdict.Healthy)
            {
                var logger = httpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<GuardedFileResult>();
                if (logger is not null) LogWatchdogAbort(logger, watchdog.Verdict, null);
            }
        }
        finally
        {
            httpContext.Response.Body = originalBody;
            httpContext.RequestAborted = originalAbort;
        }
    }

    /// <summary>Reports the bytes the response body accepted, which is the watchdog's progress signal.</summary>
    private sealed class ProgressStream(Stream inner, TransferWatchdog watchdog) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            watchdog.Report(count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            watchdog.Report(buffer.Length);
        }
    }

    private static bool ExpectedTermination(Exception error, TransferWatchdog watchdog, CancellationToken originalAbort) =>
        error is OperationCanceledException or IOException
        && (originalAbort.IsCancellationRequested || watchdog.Verdict != TransferWatchdogVerdict.Healthy);
}
