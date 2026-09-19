using System.Security.Cryptography;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Scanning;

/// <summary>
/// Uses the endpoint antivirus already installed on the server (for example Trend Micro
/// OfficeScan / Apex One) through its real-time (on-access) scanning, since such products
/// offer no scanning API to applications.
///
/// Two mechanisms, both fail closed:
/// <list type="number">
/// <item>Canary: before any file may be reported clean, an EICAR test file is written to the
/// probe directory and must be removed or altered by the antivirus within the
/// timeout. A canary that survives means real-time scanning is off or the OEM storage is
/// excluded, and the engine reports itself unavailable (files stay quarantined).</item>
/// <item>Verification: the quarantined file is re-read after a settle delay. A file that has
/// vanished, is access-denied, or no longer matches the recorded SHA-256 was intercepted by
/// the antivirus and is reported infected. Only an unchanged, readable file is clean.</item>
/// </list>
/// </summary>
public sealed class OnAccessFileScanner : IFileScanner
{
    private readonly OemOnAccessOptions options;
    private readonly string probeDirectory;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly byte[] canaryContent;
    private readonly string canaryExtension;
    private readonly SemaphoreSlim canaryGate = new(1, 1);
    private DateTimeOffset? canaryVerifiedAt;
    private DateTimeOffset? canaryFailedAt;

    public OnAccessFileScanner(OemOnAccessOptions options, string probeDirectory, TimeProvider time, ILogger logger)
        : this(options, probeDirectory, time, logger, System.Text.Encoding.ASCII.GetBytes(FakeFileScanner.EicarSignature), ".com") { }

    /// <summary>Test seam: a harmless canary that a simulated antivirus reacts to.</summary>
    internal OnAccessFileScanner(OemOnAccessOptions options, string probeDirectory, TimeProvider time, ILogger logger, byte[] canaryContent, string canaryExtension)
    {
        this.options = options;
        this.probeDirectory = probeDirectory;
        this.time = time;
        this.logger = logger;
        this.canaryContent = canaryContent;
        this.canaryExtension = canaryExtension;
    }

    public string Name => $"{options.ProductName}（实时扫描）";

    public async Task<ScanResult> ScanAsync(ScanTarget target, CancellationToken ct)
    {
        if (!await EnsureCanaryAsync(ct))
            return ScanResult.Unavailable(Name, $"{options.ProductName} 实时扫描未生效：测试文件未被拦截（请确认实时扫描已开启且 OEM 存储目录未被排除）");

        // Give on-write scanning time to finish before judging the file.
        var age = time.GetUtcNow() - File.GetLastWriteTimeUtc(target.Path);
        var settle = TimeSpan.FromSeconds(options.SettleSeconds);
        if (age < settle) await Task.Delay(settle - age, time, ct);

        // Reading triggers on-read scanning; the content must be exactly what was uploaded.
        var first = await VerifyAsync(target, ct);
        if (first is not null) return first;
        // Some products act asynchronously after the read: look once more after the settle delay.
        if (settle > TimeSpan.Zero) await Task.Delay(settle, time, ct);
        var second = await VerifyAsync(target, ct);
        if (second is not null) return second;
        var info = new FileInfo(target.Path);
        if (!info.Exists) return Intercepted("文件在扫描后被清除或隔离");
        if ((ulong)info.Length != target.Size) return Intercepted("文件在扫描后被修改");
        return new ScanResult(ScanVerdict.Clean, Name, null, null, null, null);
    }

    /// <summary>A quarantined file that disappeared or became unreadable was taken by the antivirus.</summary>
    public ScanResult? ExplainAccessFailure(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => Intercepted("文件已被清除或隔离"),
        UnauthorizedAccessException => Intercepted("文件被拒绝访问"),
        QuarantineContentChangedException => Intercepted("文件内容已被修改（威胁已被清除）"),
        _ => null,
    };

    private async Task<ScanResult?> VerifyAsync(ScanTarget target, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            return sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase) ? null : Intercepted("文件内容已被修改（威胁已被清除）");
        }
        catch (Exception error) when (ExplainAccessFailure(error) is ScanResult explained) { return explained; }
    }

    private ScanResult Intercepted(string what) =>
        new(ScanVerdict.Infected, Name, null, null, $"{options.ProductName} 拦截", $"{what}，判定为 {options.ProductName} 已拦截");

    private async Task<bool> EnsureCanaryAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(options.CanaryIntervalMinutes);
        if (canaryVerifiedAt is { } verified && time.GetUtcNow() - verified < interval) return true;
        await canaryGate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (canaryVerifiedAt is { } again && now - again < interval) return true;
            // After a failed canary, retry at most once a minute rather than on every job.
            if (canaryFailedAt is { } failed && now - failed < TimeSpan.FromMinutes(1)) return false;
            var intercepted = await RunCanaryAsync(ct);
            if (intercepted) { canaryVerifiedAt = time.GetUtcNow(); canaryFailedAt = null; }
            else { canaryVerifiedAt = null; canaryFailedAt = time.GetUtcNow(); }
            return intercepted;
        }
        finally { canaryGate.Release(); }
    }

    private async Task<bool> RunCanaryAsync(CancellationToken ct)
    {
        var path = Path.Combine(probeDirectory, $"canary-{Guid.NewGuid():N}{canaryExtension}");
        try
        {
            Directory.CreateDirectory(probeDirectory);
            CleanupStaleCanaries();
            try
            {
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(canaryContent, ct);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                // An ACL, full disk or other I/O failure is not evidence of an antivirus verdict.
                // Only a successfully written probe can establish an observed interception.
                logger.LogWarning("OEM on-access scan canary could not be written ({ErrorType}); files stay quarantined.", error.GetType().Name);
                return false;
            }
            var deadline = time.GetUtcNow().AddSeconds(options.CanaryTimeoutSeconds);
            while (true)
            {
                if (CanaryIntercepted(path)) return true;
                if (time.GetUtcNow() >= deadline) break;
                await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
            }
            logger.LogWarning("OEM on-access scan canary was not intercepted within {Seconds}s; files stay quarantined.", options.CanaryTimeoutSeconds);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("OEM on-access scan canary could not be verified ({ErrorType}); files stay quarantined.", error.GetType().Name);
            return false;
        }
        finally { TryDelete(path); }
    }

    private bool CanaryIntercepted(string path)
    {
        try
        {
            // File.Exists also returns false for access errors; opening distinguishes those
            // failures from a probe that was actually removed after a successful write.
            var content = File.ReadAllBytes(path);
            return !content.AsSpan().SequenceEqual(canaryContent);
        }
        catch (FileNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; } // briefly locked while being scanned: look again
    }

    private void CleanupStaleCanaries()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(probeDirectory, "canary-*"))
                if (time.GetUtcNow() - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromHours(1)) TryDelete(stale);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
