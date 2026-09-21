using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Scanning;

namespace Yf.Api.Tests.Oem;

public sealed class OemClamAvScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentSignatures = Now.AddHours(-2);

    [Fact]
    public async Task CleanRequiresExactReplyMatchingContentAndStableVersion()
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => "stream: OK");
        using var file = TempFile.Create("drawing-content"u8.ToArray());
        var scanner = Scanner(clamd.Port);

        var result = await scanner.ScanAsync(file.Target(new ScanFreshnessPolicy(TimeSpan.FromDays(1))), TestContext.Current.CancellationToken);

        Assert.True(result.Verdict == ScanVerdict.Clean, result.Error);
        Assert.Equal("1.4.6", result.EngineVersion);
        Assert.Equal("28001", result.SignatureVersion);
        Assert.Equal(CurrentSignatures, result.SignatureUpdatedAt);
        Assert.Equal(file.Content, clamd.LastContent);
        Assert.True(scanner.SupportsSignatureFreshness);
    }

    [Theory]
    [InlineData("stream: Win.Test.Eicar-1 FOUND", ScanVerdict.Infected, "Win.Test.Eicar-1")]
    [InlineData("stream: Heuristics.Encrypted.Zip FOUND", ScanVerdict.Unscannable, null)]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxFileSize FOUND", ScanVerdict.Unscannable, null)]
    [InlineData("stream: INSTREAM size limit exceeded. ERROR", ScanVerdict.Unscannable, null)]
    [InlineData("INSTREAM size limit exceeded. ERROR", ScanVerdict.Unscannable, null)]
    [InlineData("stream: Temporary scan failure ERROR", ScanVerdict.Error, null)]
    public async Task ClamdVerdictsAreMappedWithoutTurningEngineErrorsIntoClean(string response, ScanVerdict expected, string? threat)
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => response);
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(clamd.Port).ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Verdict);
        Assert.Equal(threat, result.ThreatName);
    }

    [Theory]
    [InlineData("stream: OK FOUND")]
    [InlineData("other: OK")]
    [InlineData("stream: OK\nstream: OK")]
    [InlineData("stream: OK\0stream: Win.Test.Eicar-1 FOUND")]
    [InlineData("")]
    public async Task LookalikeOrUnknownOkIsNeverAccepted(string response)
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => response);
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(clamd.Port).ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.NotEqual(ScanVerdict.Clean, result.Verdict);
    }

    [Fact]
    public async Task TruncatedReplyFailsClosed()
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => null);
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(clamd.Port).ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
    }

    [Fact]
    public async Task ChangedContentCannotBorrowAnOkForTheRecordedUpload()
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => "stream: OK");
        using var file = TempFile.Create("original"u8.ToArray());
        var target = file.Target();
        await File.WriteAllBytesAsync(file.Path, "replacement"u8.ToArray(), TestContext.Current.CancellationToken);

        var result = await Scanner(clamd.Port).ScanAsync(target, TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Contains("不一致", result.Error);
    }

    [Fact]
    public async Task StaleSignatureDatabaseFailsClosedWithVersionEvidence()
    {
        await using var clamd = new ClamdFixture(Now.AddDays(-3), _ => "stream: OK");
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(clamd.Port).ScanAsync(file.Target(new ScanFreshnessPolicy(TimeSpan.FromDays(1))), TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
        Assert.Equal("28001", result.SignatureVersion);
        Assert.NotNull(result.SignatureUpdatedAt);
    }

    [Fact]
    public async Task VersionChangingDuringScanRequiresRetry()
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => "stream: OK", versionForRequest: request => request == 1 ? "28001" : "28002");
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(clamd.Port).ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Contains("版本发生变化", result.Error);
    }

    [Fact]
    public async Task OversizedFileIsRejectedBeforeAnyBytesLeaveTheProcess()
    {
        using var file = TempFile.Create("12345"u8.ToArray());
        var scanner = Scanner(UnusedPort(), maximumBytes: 4);

        var result = await scanner.ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.Unscannable, result.Verdict);
    }

    [Fact]
    public async Task MissingLocalDaemonFailsClosed()
    {
        using var file = TempFile.Create("x"u8.ToArray());

        var result = await Scanner(UnusedPort()).ScanAsync(file.Target(), TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        await using var clamd = new ClamdFixture(CurrentSignatures, _ => "stream: OK", holdVersionReply: true);
        using var file = TempFile.Create("x"u8.ToArray());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scanner(clamd.Port).ScanAsync(file.Target(), cancel.Token));
    }

    [Fact]
    public void FactoryPreservesOnAccessAndAddsClamAv()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = new AppOptions { OemScanner = { Engine = "ClamAV" } };
        var scanner = FileScannerFactory.Create(options, loggerFactory);
        Assert.IsType<ClamAvFileScanner>(scanner);
        Assert.True(new OemScanPipeline(scanner).SupportsSignatureFreshness);

        options.OemScanner.Engine = "None";
        Assert.IsType<UnavailableFileScanner>(FileScannerFactory.Create(options, loggerFactory));
    }

    private static ClamAvFileScanner Scanner(int port, long maximumBytes = 1024 * 1024) =>
        new(new OemClamAvOptions { Port = port, ConnectTimeoutSeconds = 1, MaxStreamBytes = maximumBytes },
            new FixedTimeProvider(Now), NullLogger.Instance);

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public byte[] Content { get; }

        private TempFile(byte[] content)
        {
            Content = content;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yf-clamav-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(Path, content);
        }

        public static TempFile Create(byte[] content) => new(content);
        public ScanTarget Target(ScanFreshnessPolicy? freshness = null) =>
            new(Path, Convert.ToHexStringLower(SHA256.HashData(Content)), (ulong)Content.Length, freshness);

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }

    private sealed class ClamdFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Func<byte[], string?> scanReply;
        private readonly Func<int, string> versionForRequest;
        private readonly DateTimeOffset signaturesAt;
        private readonly bool holdVersionReply;
        private readonly Task acceptLoop;
        private int versionRequests;

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public byte[] LastContent { get; private set; } = [];

        public ClamdFixture(DateTimeOffset signaturesAt, Func<byte[], string?> scanReply,
            Func<int, string>? versionForRequest = null, bool holdVersionReply = false)
        {
            this.signaturesAt = signaturesAt;
            this.scanReply = scanReply;
            this.versionForRequest = versionForRequest ?? (_ => "28001");
            this.holdVersionReply = holdVersionReply;
            listener.Start();
            acceptLoop = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(() => ServeAsync(client), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var command = await ReadNulAsync(stream, stop.Token);
                    if (command == "zVERSION")
                    {
                        if (holdVersionReply) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                        var request = Interlocked.Increment(ref versionRequests);
                        var local = signaturesAt.ToLocalTime();
                        var timestamp = local.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
                        await ReplyAsync(stream, $"ClamAV 1.4.6/{versionForRequest(request)}/{timestamp}", stop.Token);
                        return;
                    }
                    if (command != "zINSTREAM") return;
                    using var content = new MemoryStream();
                    var header = new byte[4];
                    while (true)
                    {
                        await ReadExactlyAsync(stream, header, stop.Token);
                        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                        if (length == 0) break;
                        var chunk = new byte[checked((int)length)];
                        await ReadExactlyAsync(stream, chunk, stop.Token);
                        await content.WriteAsync(chunk, stop.Token);
                    }
                    LastContent = content.ToArray();
                    var reply = scanReply(LastContent);
                    if (reply is not null) await ReplyAsync(stream, reply, stop.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        private static async Task<string> ReadNulAsync(NetworkStream stream, CancellationToken ct)
        {
            using var content = new MemoryStream();
            var one = new byte[1];
            while (await stream.ReadAsync(one, ct) != 0)
            {
                if (one[0] == 0) return Encoding.ASCII.GetString(content.ToArray());
                content.WriteByte(one[0]);
            }
            return Encoding.ASCII.GetString(content.ToArray());
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private static async Task ReplyAsync(NetworkStream stream, string reply, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(reply + "\0");
            await stream.WriteAsync(bytes, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            try { await acceptLoop; } catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
