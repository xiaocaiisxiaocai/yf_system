using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

/// <summary>
/// Builds an IDbContextFactory&lt;YfDbContext&gt; for tests that construct
/// IdentityService directly, mirroring how ApiApplication.cs wires it in
/// production (same connection string, SQL dialect and UTC session initialization).
/// </summary>
internal static class EfTestSupport
{
    public static IDbContextFactory<YfDbContext> DbContextFactory(AppOptions options)
    {
        var connectionString = AppDb.BuildConnectionString(options);
        var builder = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(connectionString, EfDb.ServerVersion)
            .AddInterceptors(UtcDatabaseSession.Instance);
        return new SimpleDbContextFactory(builder.Options);
    }

    // No pooling: tests don't need it, and it avoids depending on whichever EF Core
    // package happens to expose a public pooled-factory type in this version.
    private sealed class SimpleDbContextFactory(DbContextOptions<YfDbContext> options) : IDbContextFactory<YfDbContext>
    {
        public YfDbContext CreateDbContext() => new(options);
    }
}
