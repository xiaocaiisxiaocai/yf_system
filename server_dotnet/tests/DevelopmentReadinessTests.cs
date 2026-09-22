using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Tests;

public sealed class DevelopmentReadinessTests
{
    [Fact]
    public async Task InvalidConfigurationStopsBeforeAnyProbeAndReturnsOnlySafeIssueCode()
    {
        var databaseCalled = false;
        var options = new AppOptions
        {
            ConnectionString = "Server=127.0.0.1;Password=do-not-report-this",
            StorageRoot = @"Z:\secret\missing",
            JwtSecret = "short",
        };

        var report = await DevelopmentReadiness.CheckAsync(options, AppContext.BaseDirectory, _ =>
        {
            databaseCalled = true;
            return Task.FromResult(new OemSettings(new Dictionary<string, string?>()));
        }, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(report.ReadyForStartup);
        Assert.False(databaseCalled);
        Assert.False(report.Checks.Configuration);
        Assert.Equal(["configuration-invalid"], report.Issues);
        Assert.DoesNotContain("secret", string.Join(',', report.Issues), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRootsDisabledWorkerAndNonClamAvAreReportedWithoutDatabaseAccess()
    {
        var sandbox = Directory.CreateTempSubdirectory("yf_readiness_").FullName;
        try
        {
            var applicationRoot = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
            var options = ValidOptions(Path.Combine(sandbox, "missing-collab"), "");
            options.WorkerEnabled = false;
            options.OemScanner.Engine = "None";

            var report = await DevelopmentReadiness.CheckAsync(options, applicationRoot,
                _ => Task.FromResult(new OemSettings(new Dictionary<string, string?>())),
                TimeProvider.System, TestContext.Current.CancellationToken);

            Assert.False(report.ReadyForStartup);
            Assert.True(report.Checks.Configuration);
            Assert.True(report.Checks.LocalTargets);
            Assert.True(report.Checks.Database);
            Assert.True(report.Checks.Reconciliation);
            Assert.False(report.Checks.Storage);
            Assert.False(report.Checks.OemStorage);
            Assert.False(report.Checks.Worker);
            Assert.False(report.Checks.Scanner);
            Assert.Equal([
                "storage-read-write-failed",
                "oem-storage-not-configured",
                "worker-disabled",
                "scanner-must-use-clamav",
            ], report.Issues);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task NonLoopbackTargetStopsBeforeDatabaseAndFilesystemProbes()
    {
        var databaseCalled = false;
        var sandbox = Path.Combine(Path.GetTempPath(), "yf_readiness_remote_" + Guid.NewGuid().ToString("N"));
        var options = ValidOptions(Path.Combine(sandbox, "storage"), Path.Combine(sandbox, "oem"));
        options.ConnectionString = "Server=192.0.2.10;Database=yf;User ID=yf;Password=test;SslMode=VerifyFull";

        var report = await DevelopmentReadiness.CheckAsync(options, Path.Combine(sandbox, "app"), _ =>
        {
            databaseCalled = true;
            return Task.FromResult(new OemSettings(new Dictionary<string, string?>()));
        }, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(databaseCalled);
        Assert.False(report.Checks.LocalTargets);
        Assert.Equal(["non-loopback-target"], report.Issues);
    }

    [Fact]
    public async Task ReconciliationRequiredSkipsScannerProbe()
    {
        var sandbox = Directory.CreateTempSubdirectory("yf_readiness_reconcile_").FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
            var storage = Directory.CreateDirectory(Path.Combine(sandbox, "storage")).FullName;
            var oem = Directory.CreateDirectory(Path.Combine(sandbox, "oem")).FullName;
            var options = ValidOptions(storage, oem);
            options.OemScanner.Engine = "ClamAV";
            options.OemScanner.ClamAv.Port = 1;
            var settings = new OemSettings(new Dictionary<string, string?>
            {
                [OemSettingCatalog.ReconcileRequired] = "required",
            });

            var report = await DevelopmentReadiness.CheckAsync(options, app, _ => Task.FromResult(settings),
                TimeProvider.System, TestContext.Current.CancellationToken);

            Assert.False(report.Checks.Reconciliation);
            Assert.False(report.Checks.Scanner);
            Assert.Contains("oem-reconciliation-required", report.Issues);
            Assert.Contains("scanner-not-ready", report.Issues);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    private static AppOptions ValidOptions(string storageRoot, string oemStorageRoot) => new()
    {
        ConnectionString = "Server=127.0.0.1;Database=yf;User ID=yf;Password=test;SslMode=None",
        StorageRoot = storageRoot,
        OemStorageRoot = oemStorageRoot,
        JwtSecret = "development-readiness-test-secret-0123456789",
        WebBaseUrl = "http://127.0.0.1:5273",
    };
}
