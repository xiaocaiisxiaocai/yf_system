using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class OemReadinessTests
{
    [Fact]
    public async Task UnconfiguredOemIsOmittedWithoutRunningStorageProbe()
    {
        var storageCalled = false;

        var report = await OemReadiness.CheckAsync(
            new AppOptions(),
            OemReadinessDatabaseEvidence.NotVerified,
            (_, _) =>
            {
                storageCalled = true;
                return Task.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(report);
        Assert.False(storageCalled);
    }

    [Fact]
    public async Task ConfiguredOemNeedsNoScannerConfigurationToBeReady()
    {
        var report = await OemReadiness.CheckAsync(
            ConfiguredOptions(),
            new OemReadinessDatabaseEvidence(true),
            (_, _) => Task.FromResult(true),
            TestContext.Current.CancellationToken);

        var oem = Assert.IsType<OemReadinessReport>(report);
        Assert.True(oem.Configured);
        Assert.True(oem.ReadyForTransfers);
        Assert.Equal("ready", oem.Storage);
        Assert.Equal("ready", oem.Worker);
        Assert.Equal("ready", oem.Reconciliation);
        Assert.Empty(oem.Issues);
    }

    [Theory]
    [InlineData(false, "not-ready", "oem-reconciliation-required")]
    [InlineData(null, "not-verified", "oem-reconciliation-not-verified")]
    public async Task ReconciliationMustBeVerifiedReady(bool? reconciliationReady, string expectedStatus, string expectedIssue)
    {
        var report = await OemReadiness.CheckAsync(
            ConfiguredOptions(),
            new OemReadinessDatabaseEvidence(reconciliationReady),
            (_, _) => Task.FromResult(true),
            TestContext.Current.CancellationToken);

        var oem = Assert.IsType<OemReadinessReport>(report);
        Assert.False(oem.ReadyForTransfers);
        Assert.Equal(expectedStatus, oem.Reconciliation);
        Assert.Equal([expectedIssue], oem.Issues);
    }

    [Fact]
    public async Task StorageFailureAndDisabledWorkerReturnOnlySafeIssueCodes()
    {
        var options = ConfiguredOptions();
        options.WorkerEnabled = false;

        var report = await OemReadiness.CheckAsync(
            options,
            new OemReadinessDatabaseEvidence(true),
            (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);

        var oem = Assert.IsType<OemReadinessReport>(report);
        Assert.False(oem.ReadyForTransfers);
        Assert.Equal("not-ready", oem.Storage);
        Assert.Equal("not-ready", oem.Worker);
        Assert.Equal("ready", oem.Reconciliation);
        Assert.Equal(["oem-storage-read-write-failed", "oem-worker-disabled"], oem.Issues);
    }

    private static AppOptions ConfiguredOptions() => new()
    {
        OemStorageRoot = @"C:\oem-readiness-test",
    };
}
