using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;

namespace Yf.Api.Infrastructure;

internal sealed record DevelopmentReadinessChecks(
    bool Configuration,
    bool LocalTargets,
    bool Database,
    bool Reconciliation,
    bool Storage,
    bool OemStorage,
    bool Worker,
    bool Scanner);

internal sealed record DevelopmentScannerEvidence(
    string? EngineVersion,
    string? SignatureVersion,
    DateTimeOffset? SignatureUpdatedAt);

internal sealed record DevelopmentReadinessReport(
    bool ReadyForStartup,
    DevelopmentReadinessChecks Checks,
    IReadOnlyList<string> Issues,
    DevelopmentScannerEvidence? Scanner = null);

/// <summary>
/// Performs a bounded, local-only preflight without constructing the application host. Database
/// access is read-only; the only writes are uniquely named probe files under the configured roots.
/// </summary>
internal static class DevelopmentReadiness
{
    private static readonly byte[] ProbeContent = "yf-development-readiness\n"u8.ToArray();

    internal static DevelopmentReadinessReport ConfigurationFailure(string issue) => new(
        false,
        new(false, false, false, false, false, false, false, false),
        [issue]);

    internal static DevelopmentReadinessReport TimeoutFailure() => new(
        false,
        new(true, true, false, false, false, false, false, false),
        ["readiness-timeout"]);

    internal static Task<DevelopmentReadinessReport> CheckAsync(
        AppOptions options,
        string applicationRoot,
        CancellationToken ct) => CheckAsync(options, applicationRoot, async cancellationToken =>
        {
            await using var connection = await new AppDb(options).OpenAsync(cancellationToken);
            await EfDatabaseLifecycle.ValidateReadyAsync(connection, cancellationToken);
            await using var db = EfDb.Use(connection);
            return await OemSettings.LoadAsync(db, cancellationToken);
        }, TimeProvider.System, ct);

    internal static async Task<DevelopmentReadinessReport> CheckAsync(
        AppOptions options,
        string applicationRoot,
        Func<CancellationToken, Task<OemSettings>> validateDatabaseAndLoadSettings,
        TimeProvider time,
        CancellationToken ct)
    {
        var issues = new List<string>();
        try
        {
            options.Validate();
            options.ValidateStorageLocation(applicationRoot);
        }
        catch
        {
            return ConfigurationFailure("configuration-invalid");
        }

        if (!UsesOnlyLoopbackTargets(options))
        {
            return new(false, new(true, false, false, false, false, false, false, false), ["non-loopback-target"]);
        }

        OemSettings? settings = null;
        var databaseReady = false;
        try
        {
            settings = await validateDatabaseAndLoadSettings(ct);
            databaseReady = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            issues.Add("database-not-ready");
        }

        var reconciliationReady = databaseReady && settings is { ReconcileRequired: false };
        if (settings?.ReconcileRequired == true) issues.Add("oem-reconciliation-required");

        var storageReady = await ProbeRootAsync(options.StorageRoot, ct);
        if (!storageReady) issues.Add("storage-read-write-failed");

        var oemStorageConfigured = !string.IsNullOrWhiteSpace(options.OemStorageRoot);
        var oemStorageReady = oemStorageConfigured && await ProbeRootAsync(options.OemStorageRoot, ct);
        if (!oemStorageConfigured) issues.Add("oem-storage-not-configured");
        else if (!oemStorageReady) issues.Add("oem-storage-read-write-failed");

        var workerReady = options.WorkerEnabled;
        if (!workerReady) issues.Add("worker-disabled");

        DevelopmentScannerEvidence? scannerEvidence = null;
        var scannerReady = false;
        if (options.OemScanner.Engine != "ClamAV")
        {
            issues.Add("scanner-must-use-clamav");
        }
        else if (databaseReady && reconciliationReady && settings is not null && oemStorageReady)
        {
            var scanResult = await ProbeClamAvAsync(options, settings, time, ct);
            scannerEvidence = new(scanResult?.EngineVersion, scanResult?.SignatureVersion, scanResult?.SignatureUpdatedAt);
            scannerReady = scanResult?.Verdict == ScanVerdict.Clean;
            if (!scannerReady)
            {
                var stale = settings.BlockOnStaleSignatures
                    && scanResult?.SignatureUpdatedAt is { } updatedAt
                    && time.GetUtcNow() - updatedAt > settings.MaxSignatureAge;
                issues.Add(stale ? "scanner-signatures-stale" : "scanner-not-ready");
            }
        }
        else
        {
            issues.Add("scanner-not-ready");
        }

        var checks = new DevelopmentReadinessChecks(
            true, true, databaseReady, reconciliationReady, storageReady, oemStorageReady, workerReady, scannerReady);
        return new(checks.Configuration && checks.LocalTargets && checks.Database && checks.Reconciliation && checks.Storage
            && checks.OemStorage && checks.Worker && checks.Scanner, checks, issues, scannerEvidence);
    }

    private static bool UsesOnlyLoopbackTargets(AppOptions options)
    {
        try
        {
            var database = new MySqlConnectionStringBuilder(options.ConnectionString);
            var databaseIsLoopback = database.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(database.Server, out var address) && IPAddress.IsLoopback(address);
            return databaseIsLoopback && new Uri(options.WebBaseUrl).IsLoopback;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeRootAsync(string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        var path = Path.Combine(root, $".yf-readiness-{Guid.NewGuid():N}.tmp");
        var passed = false;
        var created = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await stream.WriteAsync(ProbeContent, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            passed = (await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(ProbeContent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            passed = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(path); }
                catch { passed = false; }
            }
        }
        return passed;
    }

    private static async Task<ScanResult?> ProbeClamAvAsync(
        AppOptions options,
        OemSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var path = Path.Combine(options.OemStorageRoot, $".yf-clamav-readiness-{Guid.NewGuid():N}.tmp");
        var created = false;
        var cleanupSucceeded = true;
        ScanResult? result = null;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await stream.WriteAsync(ProbeContent, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            var target = new ScanTarget(
                path,
                Convert.ToHexStringLower(SHA256.HashData(ProbeContent)),
                (ulong)ProbeContent.Length,
                settings.BlockOnStaleSignatures ? new ScanFreshnessPolicy(settings.MaxSignatureAge) : null);
            var scanner = new ClamAvFileScanner(options.OemScanner.ClamAv, time, NullLogger.Instance);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { result = await scanner.ScanAsync(target, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch { }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        finally
        {
            if (created)
            {
                try { File.Delete(path); }
                catch { cleanupSucceeded = false; }
            }
        }
        return cleanupSucceeded ? result : null;
    }
}
