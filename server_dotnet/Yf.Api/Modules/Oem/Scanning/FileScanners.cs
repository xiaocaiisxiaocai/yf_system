using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Modules.Oem.Scanning;

public static class ScanJobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Clean = "CLEAN";
    public const string Infected = "INFECTED";
    public const string Error = "ERROR";
    public const string Unscannable = "UNSCANNABLE";
}

public enum ScanVerdict { Clean, Infected, Unscannable, Error, EngineUnavailable }

public sealed record ScanResult(ScanVerdict Verdict, string EngineName, string? EngineVersion, string? SignatureVersion, string? ThreatName, string? Error)
{
    public static ScanResult Unavailable(string engine, string reason) => new(ScanVerdict.EngineUnavailable, engine, null, null, null, reason);
}

/// <summary>
/// Malware engine abstraction. The engine is not chosen yet; production runs
/// <see cref="UnavailableFileScanner"/>, which keeps every file quarantined until a
/// real engine is plugged in behind this interface (no table or workflow changes).
/// </summary>
public interface IFileScanner
{
    string Name { get; }
    Task<ScanResult> ScanAsync(string path, CancellationToken ct);
}

/// <summary>Fail-closed placeholder: nothing is ever reported clean.</summary>
public sealed class UnavailableFileScanner : IFileScanner
{
    public string Name => "none";

    public Task<ScanResult> ScanAsync(string path, CancellationToken ct) =>
        Task.FromResult(ScanResult.Unavailable(Name, "扫描引擎未配置"));
}

/// <summary>
/// Development/test engine: reports the industry-standard EICAR test string as a threat
/// and everything else as clean. It performs no real malware detection and refuses to
/// start unless explicitly acknowledged in configuration.
/// </summary>
public sealed class FakeFileScanner : IFileScanner
{
    public const string EicarSignature = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private static readonly byte[] Eicar = System.Text.Encoding.ASCII.GetBytes(EicarSignature);

    public string Name => "fake";

    public async Task<ScanResult> ScanAsync(string path, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize + Eicar.Length];
        var carry = 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(carry, bufferSize), ct)) > 0)
        {
            var window = buffer.AsSpan(0, carry + read);
            if (window.IndexOf(Eicar) >= 0) return new ScanResult(ScanVerdict.Infected, Name, "1.0", "fake", "Eicar-Test-Signature", null);
            carry = Math.Min(Eicar.Length - 1, window.Length);
            window[^carry..].CopyTo(buffer);
        }
        return new ScanResult(ScanVerdict.Clean, Name, "1.0", "fake", null, null);
    }
}

public static class FileScannerFactory
{
    public static IFileScanner Create(AppOptions options) => options.OemScanner.Engine switch
    {
        "Fake" => new FakeFileScanner(),
        _ => new UnavailableFileScanner(),
    };
}

/// <summary>
/// Scan pipeline (chain of responsibility): signature check → archive limits and
/// encryption → malware engine. The first step that reaches a verdict ends the chain;
/// only files that pass every step are clean.
/// </summary>
public sealed class OemScanPipeline(IFileScanner engine)
{
    public string EngineName => engine.Name;

    public async Task<ScanResult> RunAsync(string path, string extension, ArchiveLimits limits, string workDirectory, CancellationToken ct)
    {
        var signature = await FileSignatureInspector.InspectFileAsync(path, extension, ct);
        if (!signature.Accepted)
            return new ScanResult(ScanVerdict.Unscannable, engine.Name, null, null, null, signature.Reason);
        ArchiveVerdict archive;
        try { archive = await ArchiveInspector.InspectAsync(path, extension, limits, workDirectory, ct); }
        catch (InvalidDataException) { archive = new ArchiveVerdict(ArchiveOutcome.Corrupt, "压缩包内容无效"); }
        if (archive.Outcome is ArchiveOutcome.Encrypted or ArchiveOutcome.LimitExceeded or ArchiveOutcome.Corrupt)
            return new ScanResult(ScanVerdict.Unscannable, engine.Name, null, null, null, archive.Reason);
        return await engine.ScanAsync(path, ct);
    }
}
