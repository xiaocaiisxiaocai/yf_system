namespace Yf.Api.Modules.Files;

/// <summary>Limits for one streamed transfer (batch ZIP download).</summary>
/// <param name="MinimumBytesPerWindow">Fewer bytes delivered to the client within one window counts as a stall.</param>
/// <param name="Window">Length of the throughput window.</param>
/// <param name="MaximumDuration">Hard upper bound for the whole transfer.</param>
internal sealed record TransferWatchdogLimits(long MinimumBytesPerWindow, TimeSpan Window, TimeSpan MaximumDuration)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);
}

internal enum TransferWatchdogVerdict
{
    Healthy,
    Stalled,
    TooLong,
}

/// <summary>
/// Cancels a long-running streamed response when the client (or the producer) stops making progress, so a
/// stuck batch download cannot hold a <see cref="BatchDownloadLimiter"/> slot and its file handles forever.
/// Progress is what the response body has accepted; every window must deliver at least the configured minimum,
/// and the whole transfer must finish within the maximum duration.
/// </summary>
internal sealed class TransferWatchdog : IDisposable
{
    private readonly TransferWatchdogLimits limits;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource cancellation;
    private readonly ITimer? timer;
    private readonly long started;
    private long transferred;
    private long checkpoint;
    private int verdict;

    public TransferWatchdog(TransferWatchdogLimits limits, CancellationToken outer, TimeProvider? time = null, bool startTimer = true)
    {
        if (limits.Window <= TimeSpan.Zero || limits.MaximumDuration <= TimeSpan.Zero || limits.MinimumBytesPerWindow < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.limits = limits;
        this.time = time ?? TimeProvider.System;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
        started = this.time.GetTimestamp();
        if (startTimer) timer = this.time.CreateTimer(_ => Check(), null, limits.Window, limits.Window);
    }

    public CancellationToken Token => cancellation.Token;

    public TransferWatchdogVerdict Verdict => (TransferWatchdogVerdict)Volatile.Read(ref verdict);

    public long Transferred => Interlocked.Read(ref transferred);

    public void Report(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref transferred, bytes);
    }

    /// <summary>Evaluates one window. Called by the timer; exposed for deterministic tests.</summary>
    internal TransferWatchdogVerdict Check()
    {
        if (Verdict != TransferWatchdogVerdict.Healthy) return Verdict;
        var total = Interlocked.Read(ref transferred);
        var delivered = total - Interlocked.Exchange(ref checkpoint, total);
        var result = time.GetElapsedTime(started) >= limits.MaximumDuration ? TransferWatchdogVerdict.TooLong
            : delivered < limits.MinimumBytesPerWindow ? TransferWatchdogVerdict.Stalled
            : TransferWatchdogVerdict.Healthy;
        if (result == TransferWatchdogVerdict.Healthy) return result;
        if (Interlocked.CompareExchange(ref verdict, (int)result, (int)TransferWatchdogVerdict.Healthy) == (int)TransferWatchdogVerdict.Healthy)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return Verdict;
    }

    public void Dispose()
    {
        timer?.Dispose();
        cancellation.Dispose();
    }
}
