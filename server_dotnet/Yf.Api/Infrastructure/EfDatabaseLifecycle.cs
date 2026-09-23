using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Infrastructure;

internal static partial class EfDatabaseLifecycle
{
    internal const string InitialMigrationId = "20260917071123_InitialCreate";

    internal static async Task InitializeEmptyAsync(AppDb database, CancellationToken ct = default)
    {
        var password = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD")
            ?? throw new InvalidOperationException(
                "Initialization requires process environment YF_BOOTSTRAP_PASSWORD; passwords are never generated or printed.");
        var passwordHash = await PasswordService.HashAsync(password, ct);
        await using var connection = await database.OpenAsync(ct);
        await using var schemaLock = await SchemaNamedLock.AcquireAsync(connection, ct);
        if (await CountTablesAsync(connection, ct) != 0)
            throw new InvalidOperationException(
                "Initialization refused: target database is not empty. Recreate an empty development database before initialization.");

        await using var db = EfDb.Use(connection);
        await db.Database.MigrateAsync(ct);
        await ValidateHistoryAsync(db, requireCurrent: true, ct);
        await BootstrapSeedCatalog.SeedEmptyAsync(db, passwordHash, ct);
        await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(db, ct);
        Console.WriteLine(
            "Empty database initialized with EF Core migrations. Administrator: admin; initial password must be changed at first login. No password printed.");
    }

    internal static async Task MigrateAsync(AppDb database, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var schemaLock = await SchemaNamedLock.AcquireAsync(connection, ct);
        if (await CountTablesAsync(connection, ct) == 0)
            throw new InvalidOperationException(
                "Migration refused: target database is empty. Use --initialize-database so required bootstrap data is created.");

        await using var db = EfDb.Use(connection);
        var history = db.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync(ct))
            throw new InvalidOperationException(
                "Migration refused: this nonempty database is not managed by EF Core migrations. Recreate it with --initialize-database.");
        var applied = await history.GetAppliedMigrationsAsync(ct);
        if (applied.Count == 0)
            throw new InvalidOperationException(
                "Migration refused: EF migration history is empty. Recreate the development database with --initialize-database.");
        ValidateHistoryRows(db, applied, requireCurrent: false);

        await db.Database.MigrateAsync(ct);
        await ValidateHistoryAsync(db, requireCurrent: true, ct);
        await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(db, ct);
        Console.WriteLine("Database is at the current EF Core migration.");
    }

    internal static async Task ValidateReadyAsync(AppDb database, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var db = EfDb.Use(connection);
        await ValidateHistoryAsync(db, requireCurrent: true, ct);
        await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(db, ct);
    }

    internal static async Task ValidateReadyAsync(MySqlConnection connection, CancellationToken ct = default)
    {
        await using var db = EfDb.Use(connection);
        await ValidateHistoryAsync(db, requireCurrent: true, ct);
        await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(db, ct);
    }

    private static async Task ValidateHistoryAsync(
        YfDbContext db,
        bool requireCurrent,
        CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync(ct))
            throw new InvalidOperationException(
                "EF migration history is missing. Recreate the development database and run --initialize-database.");
        ValidateHistoryRows(db, await history.GetAppliedMigrationsAsync(ct), requireCurrent);
    }

    private static void ValidateHistoryRows(
        YfDbContext db,
        IReadOnlyList<HistoryRow> applied,
        bool requireCurrent)
    {
        var known = db.Database.GetMigrations().ToArray();
        if (known.Length == 0 || known[0] != InitialMigrationId)
            throw new InvalidOperationException("The compiled EF migration chain does not start at this application's InitialCreate migration.");
        if (applied.Count > known.Length
            || applied.Where((row, index) => row.MigrationId != known[index]).Any())
            throw new InvalidOperationException(
                "Unknown, reordered, or future EF migration history; use the matching application build or restore its database backup.");
        if (requireCurrent && applied.Count != known.Length)
            throw new InvalidOperationException(
                "Database has unapplied EF Core migrations. Back up the database and run --migrate-database before startup.");
    }

    private static async Task<int> CountTablesAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class SchemaNamedLock : IAsyncDisposable
    {
        private readonly MySqlConnection connection;
        private readonly string name;
        private int released;

        private SchemaNamedLock(MySqlConnection connection, string name)
        {
            this.connection = connection;
            this.name = name;
        }

        internal static async Task<SchemaNamedLock> AcquireAsync(MySqlConnection connection, CancellationToken ct)
        {
            var name = MySqlNamedLock.Name("schema", connection.Database);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name,10)";
            command.Parameters.AddWithValue("@name", name);
            var acquired = await command.ExecuteScalarAsync(ct);
            if (acquired is null || acquired is DBNull || Convert.ToInt32(acquired) != 1)
                throw new InvalidOperationException("Another database migration is running. Retry after it completes.");
            return new(connection, name);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@name)";
                command.Parameters.AddWithValue("@name", name);
                await command.ExecuteScalarAsync(timeout.Token);
            }
            catch
            {
                try { MySqlConnection.ClearPool(connection); }
                catch { }
            }
        }
    }
}
