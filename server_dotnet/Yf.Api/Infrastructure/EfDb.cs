using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Uses the caller's already-open connection and transaction. Disposing this
/// context never owns, commits or closes either resource. Shared authorization,
/// audit and workflow helpers can therefore use EF in the business transaction.
/// </summary>
internal static class EfDb
{
    /// <summary>
    /// The one SQL dialect every EF context generates (request, pooled, design-time). The oldest
    /// supported server (MySQL 5.7) keeps the SQL valid on 5.7 and 8.x alike, and a fixed value avoids
    /// a startup round trip and dialect drift between contexts that share one transaction.
    /// </summary>
    internal static readonly ServerVersion ServerVersion = Microsoft.EntityFrameworkCore.ServerVersion.Parse("5.7.44-mysql");

    // DbContextOptions is immutable after construction and may safely be shared. The connection itself
    // is the cache key and is also embedded in the options, so contexts from different connections (or
    // databases) can never cross. Context instances and transaction enlistment remain per call.
    private static readonly ConditionalWeakTable<MySqlConnection, DbContextOptions<YfDbContext>> OptionsByConnection = new();

    internal static YfDbContext Use(MySqlConnection connection, MySqlTransaction? transaction = null)
    {
        var options = OptionsByConnection.GetValue(connection, static item =>
            new DbContextOptionsBuilder<YfDbContext>()
                .UseMySql(item, ServerVersion)
                .ReplaceService<IMigrator, PreflightMySqlMigrator>()
                .AddInterceptors(UtcDatabaseSession.Instance)
                .Options);
        var context = new YfDbContext(options);
        if (transaction is not null) context.Database.UseTransaction(transaction);
        return context;
    }

    internal static IQueryable<T> Page<T>(this IQueryable<T> query, ulong offset, ulong size)
        => offset > int.MaxValue ? query.Take(0) : query.Skip((int)offset).Take((int)Math.Min(size, int.MaxValue));
}

/// <summary>
/// Reads the database's UTC clock, so timestamps and expiry comparisons never depend on the
/// application server's clock. <paramref name="precision"/> matches the target column (datetime(3)/(6)).
/// </summary>
internal static class DbClock
{
    internal static Task<DateTime> UtcNowAsync(YfDbContext db, CancellationToken ct, int precision = 6) => precision switch
    {
        0 => db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP() AS Value").SingleAsync(ct),
        3 => db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(3) AS Value").SingleAsync(ct),
        6 => db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };
}
