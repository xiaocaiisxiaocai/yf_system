using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Tests.Oem;

public sealed class OemBackgroundWorkerTests
{
    [Fact]
    public async Task DisabledWorkerCompletesWithoutRunningJobs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = new CountingJob("disabled", TimeSpan.FromMilliseconds(10), target: 1);
        using var worker = new OemBackgroundWorker([job], new AppOptions { WorkerEnabled = false },
            NullLogger<OemBackgroundWorker>.Instance);

        await worker.StartAsync(timeout.Token);

        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask!.WaitAsync(timeout.Token);
        Assert.Equal(0, job.RunCount);
    }

    [Fact]
    public async Task SlowJobDoesNotBlockOtherJobsAndDoesNotReenter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var slow = new BlockingFirstJob("slow", TimeSpan.FromMilliseconds(10));
        var fast = new CountingJob("fast", TimeSpan.FromMilliseconds(10), target: 2);
        using var worker = Create(slow, fast);

        await worker.StartAsync(timeout.Token);
        try
        {
            await slow.FirstStarted.Task.WaitAsync(timeout.Token);
            await fast.TargetReached.Task.WaitAsync(timeout.Token);
            await Task.Delay(75, timeout.Token);

            Assert.Equal(1, slow.RunCount);
            Assert.Equal(1, slow.MaximumConcurrentRuns);

            slow.ReleaseFirst.TrySetResult();
            await slow.SecondStarted.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, slow.MaximumConcurrentRuns);
        }
        finally
        {
            slow.ReleaseFirst.TrySetResult();
            await worker.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task FailingJobDoesNotStopOtherJobs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var failing = new FailingJob(TimeSpan.FromMilliseconds(10), target: 2);
        var healthy = new CountingJob("healthy", TimeSpan.FromMilliseconds(10), target: 3);
        using var worker = Create(failing, healthy);

        await worker.StartAsync(timeout.Token);
        try
        {
            await failing.TargetReached.Task.WaitAsync(timeout.Token);
            await healthy.TargetReached.Task.WaitAsync(timeout.Token);
            Assert.True(failing.RunCount >= 2);
            Assert.True(healthy.RunCount >= 3);
        }
        finally
        {
            await worker.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public void RetryDelayBacksOffExponentiallyAndIsCapped()
    {
        var interval = TimeSpan.FromSeconds(3);
        Assert.Equal(interval, OemBackgroundWorker.RetryDelay(interval, 0));
        Assert.Equal(TimeSpan.FromSeconds(6), OemBackgroundWorker.RetryDelay(interval, 1));
        Assert.Equal(TimeSpan.FromSeconds(24), OemBackgroundWorker.RetryDelay(interval, 3));
        Assert.Equal(OemBackgroundWorker.MaximumBackoff, OemBackgroundWorker.RetryDelay(interval, 10));
        Assert.Equal(OemBackgroundWorker.MaximumBackoff, OemBackgroundWorker.RetryDelay(interval, int.MaxValue));
        // A job whose interval already exceeds the cap is never retried sooner than its interval.
        Assert.Equal(TimeSpan.FromHours(1), OemBackgroundWorker.RetryDelay(TimeSpan.FromHours(1), 4));
    }

    [Fact]
    public async Task ConsecutiveFailuresBackOffAndASuccessResetsTheInterval()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Fails four times, then succeeds: delays 40 + 80 + 160 + 320 ms before the 5th run,
        // then the plain interval again.
        var job = new FlakyJob(TimeSpan.FromMilliseconds(20), failures: 4);
        using var worker = Create(job);

        await worker.StartAsync(timeout.Token);
        try
        {
            await job.RunReached(7).WaitAsync(timeout.Token);
            var backedOff = job.TimeOf(5) - job.TimeOf(1);
            Assert.True(backedOff >= TimeSpan.FromMilliseconds(550), $"5th run after {backedOff.TotalMilliseconds} ms; failures did not back off");
            Assert.True(job.TimeOf(7) - job.TimeOf(5) < TimeSpan.FromMilliseconds(400), "the interval was not reset after a success");
        }
        finally
        {
            await worker.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task StopCancelsRunningJobsAndCompletes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = new CancellationJob();
        using var worker = Create(job);

        await worker.StartAsync(timeout.Token);
        await job.Started.Task.WaitAsync(timeout.Token);

        await worker.StopAsync(timeout.Token);

        await job.Cancelled.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, job.RunCount);
    }

    private static OemBackgroundWorker Create(params IOemBackgroundJob[] jobs) =>
        new(jobs, new AppOptions { WorkerEnabled = true }, NullLogger<OemBackgroundWorker>.Instance);

    private sealed class BlockingFirstJob(string name, TimeSpan interval) : IOemBackgroundJob
    {
        private int activeRuns;
        private int maximumConcurrentRuns;
        private int runCount;

        public string Name => name;
        public TimeSpan Interval => interval;
        public int RunCount => Volatile.Read(ref runCount);
        public int MaximumConcurrentRuns => Volatile.Read(ref maximumConcurrentRuns);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunOnceAsync(CancellationToken ct)
        {
            var active = Interlocked.Increment(ref activeRuns);
            SetMaximum(active);
            var run = Interlocked.Increment(ref runCount);
            try
            {
                if (run == 1)
                {
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(ct);
                }
                else if (run == 2) SecondStarted.TrySetResult();
            }
            finally { Interlocked.Decrement(ref activeRuns); }
        }

        private void SetMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrentRuns);
                if (current >= value || Interlocked.CompareExchange(ref maximumConcurrentRuns, value, current) == current) return;
            }
        }
    }

    private sealed class CountingJob(string name, TimeSpan interval, int target) : IOemBackgroundJob
    {
        private int runCount;
        public string Name => name;
        public TimeSpan Interval => interval;
        public int RunCount => Volatile.Read(ref runCount);
        public TaskCompletionSource TargetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunOnceAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref runCount) >= target) TargetReached.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingJob(TimeSpan interval, int target) : IOemBackgroundJob
    {
        private int runCount;
        public string Name => "failing";
        public TimeSpan Interval => interval;
        public int RunCount => Volatile.Read(ref runCount);
        public TaskCompletionSource TargetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunOnceAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref runCount) >= target) TargetReached.TrySetResult();
            throw new InvalidOperationException("simulated job failure");
        }
    }

    private sealed class FlakyJob(TimeSpan interval, int failures) : IOemBackgroundJob
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Dictionary<int, TaskCompletionSource> reached = [];
        private int runCount;
        public string Name => "flaky";
        public TimeSpan Interval => interval;
        private readonly List<TimeSpan> times = [];

        public TimeSpan TimeOf(int run)
        {
            lock (reached) return times[run - 1];
        }

        public Task RunReached(int run)
        {
            lock (reached)
            {
                if (!reached.TryGetValue(run, out var source))
                    reached[run] = source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Volatile.Read(ref runCount) >= run) source.TrySetResult();
                return source.Task;
            }
        }

        public Task RunOnceAsync(CancellationToken ct)
        {
            var run = Interlocked.Increment(ref runCount);
            lock (reached)
            {
                times.Add(clock.Elapsed);
                if (reached.TryGetValue(run, out var source)) source.TrySetResult();
                else reached[run] = CompletedSource();
            }
            return run <= failures ? throw new InvalidOperationException("simulated outage") : Task.CompletedTask;
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult();
            return source;
        }
    }

    private sealed class CancellationJob : IOemBackgroundJob
    {
        private int runCount;
        public string Name => "cancellation";
        public TimeSpan Interval => TimeSpan.FromHours(1);
        public int RunCount => Volatile.Read(ref runCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref runCount);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
