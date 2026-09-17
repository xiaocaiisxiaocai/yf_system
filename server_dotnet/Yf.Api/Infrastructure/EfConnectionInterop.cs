using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Exposes the provider connection and current transaction for the few MySQL-specific
/// operations which EF cannot express (row/named locks and database clock reads).
/// Callers continue to share one physical connection and one business transaction.
/// </summary>
public static class EfConnectionInterop
{
    public static MySqlConnection Connection(this DatabaseFacade database) =>
        (MySqlConnection)database.GetDbConnection();

    public static MySqlTransaction? Transaction(this DatabaseFacade database) =>
        (MySqlTransaction?)database.CurrentTransaction?.GetDbTransaction();

    public static MySqlTransaction RequireTransaction(this DatabaseFacade database) =>
        Transaction(database) ?? throw new InvalidOperationException("No active EF Core transaction.");
}
