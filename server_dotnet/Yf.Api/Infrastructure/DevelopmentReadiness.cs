using System.Net;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

internal sealed record DevelopmentReadinessChecks(
    bool Configuration,
    bool LocalTargets,
    bool Database,
    bool Storage,
    bool Worker);

internal sealed record DevelopmentReadinessReport(
    bool ReadyForStartup,
    DevelopmentReadinessChecks Checks,
    IReadOnlyList<string> Issues);

/// <summary>
/// Performs a bounded, local-only preflight without constructing the application host. Database
/// access is read-only; the only writes are uniquely named probe files under the configured root.
/// </summary>
internal static class DevelopmentReadiness
{
    private static readonly byte[] ProbeContent = "yf-development-readiness\n"u8.ToArray();

    internal static DevelopmentReadinessReport ConfigurationFailure(string issue) => new(
        false,
        new(false, false, false, false, false),
        [issue]);

    internal static DevelopmentReadinessReport TimeoutFailure() => new(
        false,
        new(true, true, false, false, false),
        ["readiness-timeout"]);

    internal static Task<DevelopmentReadinessReport> CheckAsync(
        AppOptions options,
        string applicationRoot,
        CancellationToken ct) => CheckAsync(options, applicationRoot, async cancellationToken =>
        {
            await using var connection = await new AppDb(options).OpenAsync(cancellationToken);
            await EfDatabaseLifecycle.ValidateReadyAsync(connection, cancellationToken);
        }, ct);

    internal static async Task<DevelopmentReadinessReport> CheckAsync(
        AppOptions options,
        string applicationRoot,
        Func<CancellationToken, Task> validateDatabase,
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
            return new(false, new(true, false, false, false, false), ["non-loopback-target"]);

        var databaseReady = false;
        try
        {
            await validateDatabase(ct);
            databaseReady = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            issues.Add("database-not-ready");
        }

        var storageReady = await ProbeRootAsync(options.StorageRoot, ct);
        if (!storageReady) issues.Add("storage-read-write-failed");

        var workerReady = options.WorkerEnabled && options.CopyWorkerEnabled;
        if (!options.WorkerEnabled) issues.Add("worker-disabled");
        if (!options.CopyWorkerEnabled) issues.Add("copy-worker-disabled");

        var checks = new DevelopmentReadinessChecks(
            true, true, databaseReady, storageReady, workerReady);
        return new(checks.Configuration && checks.LocalTargets && checks.Database
            && checks.Storage && checks.Worker, checks, issues);
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
}
