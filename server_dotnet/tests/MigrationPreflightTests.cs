using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class MigrationPreflightTests
{
    private const string Latest = "20261007003510_AllowMacroAndBinaryExcelUploads";
    private const string Barrier = "20261006031500_ReplaceOemMalwareScanningWithValidation";

    [Fact(Timeout = 120_000)]
    public async Task ApplicationPooledContextsKeepPreflightAfterReuse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var factory = host.Service<IDbContextFactory<YfDbContext>>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = await factory.CreateDbContextAsync(ct);
            var history = (await context.Database.GetAppliedMigrationsAsync(ct)).ToArray();

            var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
                context.GetService<IMigrator>().MigrateAsync("RestoreOemPlatform", ct));

            Assert.Contains("before executing any migration", error.Message, StringComparison.Ordinal);
            Assert.Equal(history, (await context.Database.GetAppliedMigrationsAsync(ct)).ToArray());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        }
    }

    [Theory]
    [InlineData("20260928063650_AddRefreshTokenRevokeReason")]
    [InlineData("RestoreOemPlatform")]
    [InlineData(Migration.InitialDatabase)]
    public void DowngradeScriptsRefuseAnIrreversiblePathWithoutOpeningTheDatabase(string target)
    {
        using var connection = new MySqlConnection("Server=127.0.0.1;Port=1;Database=offline");
        using var context = EfDb.Use(connection);
        var migrator = context.GetService<IMigrator>();

        var error = Assert.Throws<NotSupportedException>(() => migrator.GenerateScript(Latest, target));

        Assert.Contains("before executing any migration", error.Message, StringComparison.Ordinal);
        Assert.Contains(Barrier, error.Message, StringComparison.Ordinal);
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void ReversibleDowngradesAndForwardScriptsRemainAvailable()
    {
        using var connection = new MySqlConnection("Server=127.0.0.1;Port=1;Database=offline");
        using var context = EfDb.Use(connection);
        var migrator = context.GetService<IMigrator>();

        var down = migrator.GenerateScript(Latest, Barrier);
        Assert.Contains("oem:company_delete", down, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN `activated_at`", down, StringComparison.Ordinal);
        var up = migrator.GenerateScript(Barrier, Latest);
        Assert.Contains("ADD `activated_at`", up, StringComparison.Ordinal);
        Assert.Contains("oem:company_delete", up, StringComparison.Ordinal);
        Assert.Equal(string.Empty, migrator.GenerateScript(Latest, Latest));
        Assert.False(migrator.HasPendingModelChanges());
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}
