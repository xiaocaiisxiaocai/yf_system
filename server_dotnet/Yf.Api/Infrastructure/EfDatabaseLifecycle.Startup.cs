using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Infrastructure;

internal static partial class EfDatabaseLifecycle
{
    internal static async Task PrepareStartupAsync(AppOptions options, CancellationToken ct = default)
    {
        if (!options.AutoInitializeDatabase)
        {
            await ValidateReadyAsync(new AppDb(options), ct);
            WarnAboutBootstrapPassword(options);
            return;
        }

        var settings = new MySqlConnectionStringBuilder(AppDb.BuildConnectionString(options));
        var name = settings.Database;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Contains('\0') ||
            new[] { "mysql", "information_schema", "performance_schema", "sys" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic initialization requires a named application database; system databases are not allowed.");

        await using var connection = new MySqlConnection(settings.ConnectionString);
        var exists = true;
        try { await connection.OpenAsync(ct); }
        catch (MySqlException ex) when (ex.Number == 1049)
        {
            // Authentication, transport and permission errors must not trigger database creation.
            exists = false;
            settings.Database = "";
            connection.ConnectionString = settings.ConnectionString;
            await connection.OpenAsync(ct);
        }

        // Same lease as explicit initialization/migration, acquired before CREATE DATABASE.
        // A second IIS worker rechecks the completed schema instead of seeding twice.
        await using var lease = await MySqlNamedLock.TryAcquireAsync(
            connection, MySqlNamedLock.Name("schema", name), 60, ct)
            ?? throw new InvalidOperationException("Another database initialization or migration is running. Retry startup after it completes.");
        string? passwordHash = null;
        if (!exists)
        {
            await using var find = connection.CreateCommand();
            find.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name=@name";
            find.Parameters.AddWithValue("@name", name);
            exists = Convert.ToInt32(await find.ExecuteScalarAsync(ct)) != 0;
            if (!exists)
            {
                passwordHash = await HashBootstrapPasswordAsync(options, ct);
                await using var create = connection.CreateCommand();
                // MySQL identifiers cannot be parameters. Quote every backtick in the configured name.
                create.CommandText = $"CREATE DATABASE `{name.Replace("`", "``")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                await create.ExecuteNonQueryAsync(ct);
            }
            await connection.ChangeDatabaseAsync(name, ct);
        }

        await using var db = EfDb.Use(connection);
        if (await CountTablesAsync(connection, ct) == 0)
        {
            passwordHash ??= await HashBootstrapPasswordAsync(options, ct);
            await db.Database.MigrateAsync(ct);
            await ValidateHistoryAsync(db, requireCurrent: true, ct);
            await BootstrapSeedCatalog.SeedEmptyAsync(db, passwordHash, ct);
            Console.WriteLine("First-start database initialization completed. Administrator: admin; change the initial password at first login. No password printed.");
        }
        else
        {
            // Never auto-upgrade a nonempty database: a pending migration can drop data (e.g. DropOemPlatform).
            // Partial initialization also stops here for inspection instead of overwriting data.
            await ValidateHistoryAsync(db, requireCurrent: true, ct);
            WarnAboutBootstrapPassword(options);
        }
        await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(db, ct);
    }

    private static void WarnAboutBootstrapPassword(AppOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BootstrapPassword)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD")))
            Console.Error.WriteLine("WARNING: Database is already initialized. Remove the unused App:BootstrapPassword configuration and YF_BOOTSTRAP_PASSWORD environment variable. Existing account passwords are unchanged.");
    }

    private static Task<string> HashBootstrapPasswordAsync(AppOptions options, CancellationToken ct)
    {
        var password = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD") ?? options.BootstrapPassword;
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("First-start initialization requires App:BootstrapPassword or process environment YF_BOOTSTRAP_PASSWORD.");
        return PasswordService.HashAsync(password, ct);
    }
}
