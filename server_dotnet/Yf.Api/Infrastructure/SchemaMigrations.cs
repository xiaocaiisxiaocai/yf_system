using MySqlConnector;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Compatibility facade retained for callers while EF Core migrations are the
/// sole schema-evolution authority. No handwritten DDL is executed here.
/// </summary>
public static class SchemaMigrations
{
    public static Task ApplyAsync(AppDb db, CancellationToken ct = default) =>
        EfDatabaseLifecycle.MigrateAsync(db, ct);

    public static Task ValidateAsync(MySqlConnection connection, CancellationToken ct) =>
        EfDatabaseLifecycle.ValidateReadyAsync(connection, ct);
}
