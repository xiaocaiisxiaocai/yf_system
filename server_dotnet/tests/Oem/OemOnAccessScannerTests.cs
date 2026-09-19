using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Scanning;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// On-access (real-time) antivirus integration, e.g. OfficeScan. A simulated antivirus watches
/// the directory and reacts to a harmless marker, so no real EICAR file is written here.
/// </summary>
public sealed class OemOnAccessScannerTests
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("YF-SIMULATED-THREAT-MARKER");

    private enum Reaction { Off, Delete, Clean }

    /// <summary>Stand-in for the endpoint antivirus: deletes or cleans files containing the marker.</summary>
    private sealed class SimulatedAntivirus : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public SimulatedAntivirus(string root, Reaction reaction)
        {
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (reaction != Reaction.Off)
                        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var content = await File.ReadAllBytesAsync(path);
                                if (content.AsSpan().IndexOf(Marker) < 0) continue;
                                if (reaction == Reaction.Delete) File.Delete(path);
                                else await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("cleaned"));
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await loop;
            stop.Dispose();
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yf_onaccess_").FullName;
        public string Probe => Path.Combine(Root, "scan-probe");
        public string Quarantine => Directory.CreateDirectory(Path.Combine(Root, "quarantine")).FullName;

        public ScanTarget Write(string name, byte[] content)
        {
            var path = Path.Combine(Quarantine, name);
            File.WriteAllBytes(path, content);
            return new ScanTarget(path, Convert.ToHexStringLower(SHA256.HashData(content)), (ulong)content.Length);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static OnAccessFileScanner Scanner(Workspace work, int canaryTimeoutSeconds = 5) =>
        new(new OemOnAccessOptions { ProductName = "OfficeScan", SettleSeconds = 1, CanaryTimeoutSeconds = canaryTimeoutSeconds, CanaryIntervalMinutes = 60 },
            work.Probe, TimeProvider.System, NullLogger.Instance, Marker, ".bin");

    private static byte[] Pdf(string body) => Encoding.ASCII.GetBytes("%PDF-1.4\n" + body + "\n%%EOF\n");

    [Fact(Timeout = 60_000)]
    public async Task WithoutRealTimeScanningNothingIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        using var work = new Workspace();
        await using var antivirus = new SimulatedAntivirus(work.Root, Reaction.Off);
        var scanner = Scanner(work, canaryTimeoutSeconds: 2);
        var target = work.Write("clean.pdf", Pdf("drawing"));

        var result = await scanner.ScanAsync(target, ct);
        Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
        Assert.Contains("实时扫描未生效", result.Error);
        Assert.Empty(Directory.EnumerateFiles(work.Probe));

        // A failed canary is not re-run for every job (no alert storm, no repeated waits).
        var started = DateTime.UtcNow;
        Assert.Equal(ScanVerdict.EngineUnavailable, (await scanner.ScanAsync(target, ct)).Verdict);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact(Timeout = 60_000)]
    public async Task ProbeWritePermissionFailureNeverCountsAsAnAntivirusInterception()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Workspace();
        var probe = Directory.CreateDirectory(work.Probe);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var acl = probe.GetAccessControl();
        acl.AddAccessRule(deny);
        probe.SetAccessControl(acl);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(work.Probe, "control.bin"), "control"));
            var result = await Scanner(work).ScanAsync(work.Write("drawing.pdf", Pdf("drawing")), TestContext.Current.CancellationToken);
            Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
        }
        finally
        {
            var restore = probe.GetAccessControl();
            restore.RemoveAccessRuleSpecific(deny);
            probe.SetAccessControl(restore);
        }
    }

    [Fact]
    public async Task InvalidProbeDirectoryKeepsFilesQuarantined()
    {
        using var work = new Workspace();
        File.WriteAllText(work.Probe, "a file cannot be used as a probe directory");
        var result = await Scanner(work).ScanAsync(work.Write("drawing.pdf", Pdf("drawing")), TestContext.Current.CancellationToken);
        Assert.Equal(ScanVerdict.EngineUnavailable, result.Verdict);
    }

    [Fact(Timeout = 60_000)]
    public async Task UnchangedFilesAreCleanOnceTheCanaryWasIntercepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var work = new Workspace();
        await using var antivirus = new SimulatedAntivirus(work.Root, Reaction.Delete);
        var scanner = Scanner(work);

        var result = await scanner.ScanAsync(work.Write("clean.pdf", Pdf("drawing")), ct);
        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal("OfficeScan（实时扫描）", result.EngineName);
        Assert.Empty(Directory.EnumerateFiles(work.Probe));
    }

    [Fact(Timeout = 60_000)]
    public async Task FilesRemovedByTheAntivirusAreInfected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var work = new Workspace();
        await using var antivirus = new SimulatedAntivirus(work.Root, Reaction.Delete);
        var scanner = Scanner(work);

        var result = await scanner.ScanAsync(work.Write("threat.pdf", Pdf(Encoding.ASCII.GetString(Marker))), ct);
        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("OfficeScan 拦截", result.ThreatName);
    }

    [Fact(Timeout = 60_000)]
    public async Task FilesCleanedByTheAntivirusAreInfected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var work = new Workspace();
        await using var antivirus = new SimulatedAntivirus(work.Root, Reaction.Clean);
        var scanner = Scanner(work);

        var result = await scanner.ScanAsync(work.Write("threat.pdf", Pdf(Encoding.ASCII.GetString(Marker))), ct);
        Assert.Equal(ScanVerdict.Infected, result.Verdict);
    }

    [Fact]
    public void AccessFailuresAreAttributedOnlyByTheOnAccessEngine()
    {
        using var work = new Workspace();
        var onAccess = Scanner(work);
        Assert.Equal(ScanVerdict.Infected, onAccess.ExplainAccessFailure(new FileNotFoundException())!.Verdict);
        Assert.Equal(ScanVerdict.Infected, onAccess.ExplainAccessFailure(new UnauthorizedAccessException())!.Verdict);
        Assert.Equal(ScanVerdict.Infected, onAccess.ExplainAccessFailure(new QuarantineContentChangedException())!.Verdict);
        Assert.Null(onAccess.ExplainAccessFailure(new IOException("sharing violation")));
        IFileScanner fake = new FakeFileScanner(), none = new UnavailableFileScanner();
        Assert.Null(fake.ExplainAccessFailure(new FileNotFoundException()));
        Assert.Null(none.ExplainAccessFailure(new UnauthorizedAccessException()));
    }

    [Fact]
    public void OnAccessOptionsAreValidated()
    {
        new OemScannerOptions { Engine = "OnAccess" }.Validate();
        Assert.Throws<InvalidOperationException>(() => new OemScannerOptions { Engine = "ClamAV" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OemScannerOptions { Engine = "OnAccess", OnAccess = { CanaryTimeoutSeconds = 1 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OemScannerOptions { Engine = "OnAccess", OnAccess = { ProductName = " " } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OemScannerOptions { Engine = "OnAccess", OnAccess = { SettleSeconds = 500 } }.Validate());
    }
}
