using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Uses the caller's already-open connection and transaction. Disposing this
/// context never owns, commits or closes either resource. Shared authorization,
/// audit and workflow helpers can therefore use EF in the business transaction.
/// </summary>
internal static class EfDb
{
    internal static YfDbContext Use(MySqlConnection connection, MySqlTransaction? transaction = null)
    {
        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(connection, ServerVersion.Parse("5.7.44-mysql"))
            .Options;
        var context = new YfDbContext(options);
        if (transaction is not null) context.Database.UseTransaction(transaction);
        return context;
    }

    internal static IQueryable<T> Page<T>(this IQueryable<T> query, ulong offset, ulong size)
        => offset > int.MaxValue ? query.Take(0) : query.Skip((int)offset).Take((int)Math.Min(size, int.MaxValue));
}
