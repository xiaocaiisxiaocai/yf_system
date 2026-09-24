using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yf.Api.Infrastructure;

/// <summary>
/// DateTimeKind=Utc controls materialization, not CURRENT_TIMESTAMP in MySQL. Set the
/// session on every checkout, including EF-owned pooled connections and first startup.
/// A numeric offset works without installing MySQL's optional time-zone tables.
/// </summary>
internal sealed class UtcDatabaseSession : DbConnectionInterceptor
{
    internal static readonly UtcDatabaseSession Instance = new();

    internal static async Task InitializeAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET SESSION time_zone = '+00:00'";
        await command.ExecuteNonQueryAsync(ct);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SET SESSION time_zone = '+00:00'";
        command.ExecuteNonQuery();
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => InitializeAsync(connection, cancellationToken);
}
