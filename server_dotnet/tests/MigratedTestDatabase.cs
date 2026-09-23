using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

/// <summary>
/// A throwaway local MySQL database initialized with the current EF migrations. Initialization sets
/// YF_BOOTSTRAP_PASSWORD process-wide, so test classes using it must join ConnectionLifecycleCollection.
/// Skips the test when
/// YF_TEST_DATABASE_URL is not set, and drops the database on dispose.
/// </summary>
internal sealed class MigratedTestDatabase : IAsyncDisposable
{
    private readonly MySqlConnection administration;
    private readonly string databaseName;

    private MigratedTestDatabase(MySqlConnection administration, string databaseName, AppOptions options)
    {
        this.administration = administration;
        this.databaseName = databaseName;
        Options = options;
        Database = new AppDb(options);
    }

    internal AppDb Database { get; }
    internal AppOptions Options { get; }

    internal static async Task<MigratedTestDatabase> CreateOrSkipAsync(CancellationToken ct)
    {
        var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
            throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
        if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Database tests only allow local MySQL");
        var credentials = uri.UserInfo.Split(':', 2);
        var adminOptions = new MySqlConnectionStringBuilder
        {
            Server = uri.Host,
            Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
            UserID = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
            DateTimeKind = MySqlDateTimeKind.Utc,
            SslMode = MySqlSslMode.None,
        };
        var administration = new MySqlConnection(adminOptions.ConnectionString);
        await administration.OpenAsync(ct);
        var databaseName = $"yf_t_{Guid.NewGuid():N}";
        try
        {
            await administration.ExecuteAsync(new CommandDefinition(
                $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                cancellationToken: ct));
            var options = new AppOptions
            {
                ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                {
                    Database = databaseName,
                    MaximumPoolSize = 2,
                    MinimumPoolSize = 0,
                }.ConnectionString,
                StorageRoot = Path.Combine(Path.GetTempPath(), "yf_migrated_test_storage"),
                WorkerEnabled = false,
            };
            var scope = new MigratedTestDatabase(administration, databaseName, options);
            var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "Migrated#" + Guid.NewGuid().ToString("N")[..8] + "!");
            try { await SchemaBootstrap.InitializeEmptyAsync(scope.Database, ct); }
            finally { Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword); }
            return scope;
        }
        catch
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
            throw;
        }
    }

    internal async Task ExecuteAsync(string sql, object? parameters, CancellationToken ct)
    {
        await using var connection = await Database.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    internal async Task<T> ScalarAsync<T>(string sql, CancellationToken ct)
    {
        await using var connection = await Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, cancellationToken: ct)) ?? default!;
    }

    public async ValueTask DisposeAsync()
    {
        try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
        finally { await administration.DisposeAsync(); }
    }
}
