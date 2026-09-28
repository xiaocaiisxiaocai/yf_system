using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class BatchDownloadStreamingTests
{
    [Fact]
    public async Task ArchiveStreamsEveryFileWithUniqueNamesAndReleasesTheLease()
    {
        var directory = Directory.CreateTempSubdirectory("yf_zip_stream_");
        try
        {
            var large = new byte[3 * 1024 * 1024 + 17];
            Random.Shared.NextBytes(large);
            var first = Path.Combine(directory.FullName, "a.bin");
            var second = Path.Combine(directory.FullName, "b.bin");
            await File.WriteAllBytesAsync(first, large, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "second", TestContext.Current.CancellationToken);
            var lease = new CountingLease();
            var result = new FileService.ZipStreamResult(
                [new(first, "报告.bin"), new(second, "报告.bin"), new(second, "notes.txt")], "yf_files_test.zip", lease);
            var context = new DefaultHttpContext();
            using var body = new MemoryStream();
            context.Response.Body = body;

            await result.ExecuteAsync(context);

            Assert.Equal("application/zip", context.Response.ContentType);
            Assert.Contains("yf_files_test.zip", context.Response.Headers.ContentDisposition.ToString());
            Assert.Equal(1, lease.Disposals);
            body.Position = 0;
            using var archive = new ZipArchive(body, ZipArchiveMode.Read);
            Assert.Equal(["报告.bin", "报告 (2).bin", "notes.txt"], archive.Entries.Select(entry => entry.FullName));
            using var copy = new MemoryStream();
            await using (var entry = archive.Entries[0].Open()) await entry.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(large, copy.ToArray());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task AFailureMidStreamAbortsTheConnectionInsteadOfEndingTheDownload()
    {
        var lease = new CountingLease();
        var result = new FileService.ZipStreamResult(
            [new(Path.Combine(Path.GetTempPath(), "yf-missing-" + Guid.NewGuid().ToString("N")), "gone.bin")],
            "yf_files_test.zip", lease);
        var aborted = new AbortRecorder();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(aborted);
        context.Response.Body = new MemoryStream();

        await Assert.ThrowsAnyAsync<IOException>(() => result.ExecuteAsync(context));
        Assert.True(aborted.Aborted);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task AlreadyCompressedExtensionsAreStoredWithoutRecompression()
    {
        var directory = Directory.CreateTempSubdirectory("yf_zip_level_");
        try
        {
            var repeated = Enumerable.Repeat((byte)'A', 256 * 1024).ToArray();
            var pdf = Path.Combine(directory.FullName, "drawing.pdf");
            var text = Path.Combine(directory.FullName, "notes.txt");
            await File.WriteAllBytesAsync(pdf, repeated, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(text, repeated, TestContext.Current.CancellationToken);
            var result = new FileService.ZipStreamResult(
                [new(pdf, "drawing.pdf"), new(text, "notes.txt")], "levels.zip", new CountingLease());
            var context = new DefaultHttpContext();
            using var body = new MemoryStream();
            context.Response.Body = body;

            await result.ExecuteAsync(context);

            body.Position = 0;
            using var archive = new ZipArchive(body, ZipArchiveMode.Read);
            Assert.Equal(repeated.Length, archive.GetEntry("drawing.pdf")!.CompressedLength);
            Assert.True(archive.GetEntry("notes.txt")!.CompressedLength < repeated.Length / 10);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void WatchdogCancelsOnlyWhenAWindowDeliversTooLittleOrTheTransferRunsTooLong()
    {
        var clock = new ManualClock();
        var limits = new TransferWatchdogLimits(1000, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));
        using (var watchdog = new TransferWatchdog(limits, CancellationToken.None, clock, startTimer: false))
        {
            watchdog.Report(600);
            watchdog.Report(400);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferWatchdogVerdict.Healthy, watchdog.Check());
            // Bytes are counted per window: the previous window's progress does not carry over.
            watchdog.Report(999);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferWatchdogVerdict.Stalled, watchdog.Check());
            Assert.True(watchdog.Token.IsCancellationRequested);
            watchdog.Report(1_000_000);
            Assert.Equal(TransferWatchdogVerdict.Stalled, watchdog.Check());
        }

        using (var watchdog = new TransferWatchdog(limits, CancellationToken.None, clock, startTimer: false))
        {
            for (var minute = 0; minute < 9; minute++)
            {
                watchdog.Report(10_000);
                clock.Advance(TimeSpan.FromSeconds(60));
                Assert.Equal(TransferWatchdogVerdict.Healthy, watchdog.Check());
            }
            watchdog.Report(10_000);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(TransferWatchdogVerdict.TooLong, watchdog.Check());
            Assert.True(watchdog.Token.IsCancellationRequested);
        }

        using var outer = new CancellationTokenSource();
        using (var watchdog = new TransferWatchdog(limits, outer.Token, clock, startTimer: false))
        {
            outer.Cancel();
            Assert.True(watchdog.Token.IsCancellationRequested);
            Assert.Equal(TransferWatchdogVerdict.Healthy, watchdog.Verdict);
        }
    }

    [Fact]
    public async Task AStalledClientIsAbortedByTheWatchdogAndReleasesTheLease()
    {
        var directory = Directory.CreateTempSubdirectory("yf_zip_stall_");
        try
        {
            var path = Path.Combine(directory.FullName, "a.bin");
            await File.WriteAllBytesAsync(path, new byte[256 * 1024], TestContext.Current.CancellationToken);
            var lease = new CountingLease();
            var result = new FileService.ZipStreamResult([new(path, "a.bin")], "stall.zip", lease,
                new TransferWatchdogLimits(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5)));
            var aborted = new AbortRecorder();
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpRequestLifetimeFeature>(aborted);
            context.Response.Body = new NeverAcceptingStream();

            var execution = result.ExecuteAsync(context);
            var finished = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            Assert.Same(execution, finished);
            await execution;
            Assert.True(aborted.Aborted);
            Assert.Equal(1, lease.Disposals);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task AnArchiveThatOutlivesTheMaximumDurationIsAborted()
    {
        var directory = Directory.CreateTempSubdirectory("yf_zip_slow_");
        try
        {
            var path = Path.Combine(directory.FullName, "a.bin");
            var content = new byte[4 * 1024 * 1024];
            Random.Shared.NextBytes(content);
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            var lease = new CountingLease();
            // Every window delivers data, but the whole transfer exceeds the maximum duration.
            var result = new FileService.ZipStreamResult([new(path, "a.bin")], "slow.zip", lease,
                new TransferWatchdogLimits(1, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)));
            var aborted = new AbortRecorder();
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpRequestLifetimeFeature>(aborted);
            var body = new TricklingStream();
            context.Response.Body = body;

            await result.ExecuteAsync(context).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(aborted.Aborted);
            Assert.Equal(1, lease.Disposals);
            Assert.True(body.Written < content.Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>A client that never reads: every write waits until the transfer is cancelled.</summary>
    private sealed class NeverAcceptingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>A slow client: accepts a little data every 20 ms.</summary>
    private sealed class TricklingStream : Stream
    {
        private long written;
        public long Written => Interlocked.Read(ref written);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (var offset = 0; offset < buffer.Length; offset += 4096)
            {
                await Task.Delay(20, cancellationToken);
                Interlocked.Add(ref written, Math.Min(4096, buffer.Length - offset));
            }
        }
    }

    private sealed class CountingLease : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}
