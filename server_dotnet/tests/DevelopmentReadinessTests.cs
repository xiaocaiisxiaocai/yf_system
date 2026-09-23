using Yf.Api.Infrastructure;

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
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.False(report.ReadyForStartup);
        Assert.False(databaseCalled);
        Assert.False(report.Checks.Configuration);
        Assert.Equal(["configuration-invalid"], report.Issues);
        Assert.DoesNotContain("secret", string.Join(',', report.Issues), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingStorageAndDisabledWorkerAreReportedWithoutDatabaseFailure()
    {
        var sandbox = Directory.CreateTempSubdirectory("yf_readiness_").FullName;
        try
        {
            var applicationRoot = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
            var options = ValidOptions(Path.Combine(sandbox, "missing-collab"));
            options.WorkerEnabled = false;

            var report = await DevelopmentReadiness.CheckAsync(options, applicationRoot,
                _ => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.False(report.ReadyForStartup);
            Assert.True(report.Checks.Configuration);
            Assert.True(report.Checks.LocalTargets);
            Assert.True(report.Checks.Database);
            Assert.False(report.Checks.Storage);
            Assert.False(report.Checks.Worker);
            Assert.Equal([
                "storage-read-write-failed",
                "worker-disabled",
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
        var options = ValidOptions(Path.Combine(sandbox, "storage"));
        options.ConnectionString = "Server=192.0.2.10;Database=yf;User ID=yf;Password=test;SslMode=VerifyFull";

        var report = await DevelopmentReadiness.CheckAsync(options, Path.Combine(sandbox, "app"), _ =>
        {
            databaseCalled = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.False(databaseCalled);
        Assert.False(report.Checks.LocalTargets);
        Assert.Equal(["non-loopback-target"], report.Issues);
    }

    private static AppOptions ValidOptions(string storageRoot) => new()
    {
        ConnectionString = "Server=127.0.0.1;Database=yf;User ID=yf;Password=test;SslMode=None",
        StorageRoot = storageRoot,
        JwtSecret = "development-readiness-test-secret-0123456789",
        WebBaseUrl = "http://127.0.0.1:5273",
    };
}
