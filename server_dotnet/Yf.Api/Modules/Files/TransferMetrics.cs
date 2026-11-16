namespace Yf.Api.Modules.Files;

/// <summary>What one streamed response delivered: body bytes accepted, wall time, and whether it ran to the end.</summary>
internal readonly record struct TransferOutcome(long Bytes, TimeSpan Elapsed, bool Completed);

/// <summary>Duration and average speed written into upload/download audit details.</summary>
internal static class TransferMetrics
{
    internal static long DurationMs(TimeSpan elapsed) => Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds));

    /// <summary>Average bytes per second; null when the transfer was too short to measure.</summary>
    internal static long? BytesPerSecond(double bytes, TimeSpan elapsed) =>
        elapsed.TotalMilliseconds >= 1 ? (long)Math.Round(bytes / elapsed.TotalSeconds) : null;

    /// <summary>Runs the end-of-transfer callback; a failure there is logged and never breaks the finished response.</summary>
    internal static async Task NotifyAsync(HttpContext context, Func<HttpContext, TransferOutcome, Task>? onFinished, TransferOutcome outcome)
    {
        if (onFinished is null) return;
        try { await onFinished(context, outcome); }
        catch (Exception error)
        {
            context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(typeof(TransferMetrics))
                .LogWarning("Recording the end of a file transfer failed ({ErrorType}).", error.GetType().Name);
        }
    }
}
