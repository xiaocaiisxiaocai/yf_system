using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class UtcDatabaseSessionTests
{
    [Fact]
    public async Task RawAndEfConnectionsUseUtcEvenAfterSessionWasChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await using (var connection = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("+00:00", await connection.ExecuteScalarAsync<string>("SELECT @@session.time_zone"));
            await connection.ExecuteAsync("SET SESSION time_zone = '+08:00'");
        }
        await using (var connection = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("+00:00", await connection.ExecuteScalarAsync<string>("SELECT @@session.time_zone"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), CURRENT_TIMESTAMP())"));
        }

        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(AppDb.BuildConnectionString(database.Options), EfDb.ServerVersion)
            .AddInterceptors(UtcDatabaseSession.Instance).Options;
        await using var context = new YfDbContext(options);
        await context.Database.OpenConnectionAsync(ct);
        Assert.Equal("+00:00", await context.Database.SqlQueryRaw<string>("SELECT @@session.time_zone AS Value").SingleAsync(ct));
        await context.Database.ExecuteSqlRawAsync("SET SESSION time_zone = '+08:00'", ct);
        await context.Database.CloseConnectionAsync();
        context.Database.OpenConnection();
        Assert.Equal("+00:00", await context.Database.SqlQueryRaw<string>("SELECT @@session.time_zone AS Value").SingleAsync(ct));

        await context.Database.ExecuteSqlRawAsync("INSERT INTO suppliers (name, status) VALUES ('UTC test', 'ACTIVE')", ct);
        var delta = await context.Database.SqlQueryRaw<long>(
            "SELECT ABS(TIMESTAMPDIFF(SECOND, created_at, UTC_TIMESTAMP())) AS Value FROM suppliers WHERE name='UTC test'").SingleAsync(ct);
        Assert.InRange(delta, 0, 5);
    }

    [Fact]
    public async Task PooledFactoryCanConnectAndRetainsUtcOnReuse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<YfDbContext>(options => options
            .UseMySql(AppDb.BuildConnectionString(database.Options), EfDb.ServerVersion)
            .AddInterceptors(UtcDatabaseSession.Instance), poolSize: 1);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<YfDbContext>>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = await factory.CreateDbContextAsync(ct);
            Assert.True(await context.Database.CanConnectAsync(ct));
            Assert.Equal("+00:00", await context.Database.SqlQueryRaw<string>(
                "SELECT @@session.time_zone AS Value").SingleAsync(ct));
        }
    }
}
