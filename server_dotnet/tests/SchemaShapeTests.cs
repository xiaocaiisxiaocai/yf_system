using Dapper;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class SchemaShapeTests
{
    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationUsesEfHistoryAndAdminOnlySeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_empty_init", ct);
        await database.InitializeAsync(ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(EfDatabaseLifecycle.InitialMigrationId, await conn.ExecuteScalarAsync<string>(
                "SELECT MigrationId FROM __EFMigrationsHistory"));
            Assert.False(await TableExistsAsync(conn, "yf_schema_migrations", ct));
            Assert.False(await TableExistsAsync(conn, "seaql_migrations", ct));
            Assert.Equal("admin", await conn.ExecuteScalarAsync<string>("SELECT employee_no FROM users"));
            Assert.Equal("系统管理员", await conn.ExecuteScalarAsync<string>("SELECT name FROM roles"));
            Assert.Equal(35, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions"));
            Assert.Equal(35, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM role_permissions"));
            Assert.Equal(13, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_configs"));
            Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_dictionaries WHERE type='PRIORITY'"));
        }
        await SchemaBootstrap.ValidateAsync(database.Database, ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task InitializationRefusesNonemptyDatabaseWithoutChangingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_init_refuse", ct);
        await database.ExecuteAsync("CREATE TABLE sentinel(id INT NOT NULL PRIMARY KEY)", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.InitializeAsync(ct));
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.True(await TableExistsAsync(conn, "sentinel", ct));
        Assert.False(await TableExistsAsync(conn, "__EFMigrationsHistory", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ExplicitMigrationIsIdempotentForEfManagedDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_migrate_repeat", ct);
        await database.InitializeAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='admin'"));
    }

    [Fact(Timeout = 120_000)]
    public async Task MigrationRefusesEmptyOrUnmanagedNonemptyDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var empty = await SchemaDatabaseScope.CreateOrSkipAsync("ef_migrate_empty", ct))
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(empty.Database, ct));

        await using var unmanaged = await SchemaDatabaseScope.CreateOrSkipAsync("ef_migrate_unmanaged", ct);
        await unmanaged.ExecuteAsync("CREATE TABLE sentinel(id INT NOT NULL PRIMARY KEY)", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(unmanaged.Database, ct));
        await using var conn = await unmanaged.Database.OpenAsync(ct);
        Assert.True(await TableExistsAsync(conn, "sentinel", ct));
        Assert.False(await TableExistsAsync(conn, "__EFMigrationsHistory", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task StartupRejectsMissingEmptyOrUnknownEfHistoryReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_history_guard", ct);
        await database.InitializeAsync(ct);
        await database.ExecuteAsync("DELETE FROM __EFMigrationsHistory", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaBootstrap.ValidateAsync(database.Database, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(database.Database, ct));

        await database.ExecuteAsync(
            "INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES('99999999999999_Unknown','9.0.0')", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaBootstrap.ValidateAsync(database.Database, ct));
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal("99999999999999_Unknown", await conn.ExecuteScalarAsync<string>(
            "SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='admin'"));
    }

    private static async Task<bool> TableExistsAsync(
        MySqlConnection connection, string table, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table)",
            new { table }, cancellationToken: ct));

    internal sealed class SchemaDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        private static readonly SemaphoreSlim BootstrapEnvironmentLock = new(1, 1);
        public AppOptions Options { get; } = options;
        public AppDb Database { get; } = database;

        public static async Task<SchemaDatabaseScope> CreateOrSkipAsync(
            string purpose,
            CancellationToken ct,
            string databaseCollation = "utf8mb4_unicode_ci")
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Schema lifecycle tests only allow local MySQL");
            if (databaseCollation is not ("utf8mb4_unicode_ci" or "utf8mb4_general_ci"))
                throw new InvalidOperationException("Unsupported test database collation");
            var credentials = uri.UserInfo.Split(':', 2);
            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None,
                AllowUserVariables = true,
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            // Pomelo appends its own migration-lock suffix to the schema name;
            // keep the disposable name short enough for MySQL's 64-byte lock limit.
            var databaseName = $"yf_t_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE {databaseCollation}",
                    cancellationToken: ct));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1,
                        MinimumPoolSize = 0,
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_schema_shape_storage"),
                    WorkerEnabled = false,
                };
                return new(administration, databaseName, new AppDb(options), options);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task InitializeBusinessFixtureAsync(CancellationToken ct)
        {
            await InitializeAsync(ct);
            await PrepareBusinessFixtureAsync(ct);
        }

        private async Task PrepareBusinessFixtureAsync(CancellationToken ct)
        {
            await using var db = await EfTestSupport.DbContextFactory(Options).CreateDbContextAsync(ct);
            await db.UserRoles.ExecuteDeleteAsync(ct);
            await db.Users.ExecuteDeleteAsync(ct);

            var roles = new[]
            {
                new Role { Id = 2, Name = "项目管理员", IsBuiltIn = true, Status = "ACTIVE" },
                new Role { Id = 3, Name = "内部成员", IsBuiltIn = true, Status = "ACTIVE" },
                new Role { Id = 4, Name = "供应商人员", IsBuiltIn = true, Status = "ACTIVE" },
            };
            db.Roles.AddRange(roles);
            await db.SaveChangesAsync(ct);

            var grants = new Dictionary<ulong, string[]>
            {
                [2] = ["dashboard", "project:list", "project:create", "project:update", "project:status", "project:view_all", "file:upload", "file:download", "file:preview", "message:create", "project:submit", "project:confirm", "project:withdraw"],
                [3] = ["dashboard", "project:list", "file:upload", "file:download", "file:preview", "message:create", "project:submit", "project:confirm"],
                [4] = ["dashboard", "project:list", "file:upload", "file:download", "file:preview", "message:create", "project:submit", "project:withdraw"],
            };
            var permissions = await db.Permissions.AsNoTracking().ToDictionaryAsync(item => item.Code, ct);
            db.RolePermissions.AddRange(grants.SelectMany(role => role.Value.Select(code =>
                new RolePermission { RoleId = role.Key, PermissionId = permissions[code].Id })));
            await db.SaveChangesAsync(ct);
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            await BootstrapEnvironmentLock.WaitAsync(ct);
            var previous = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            try
            {
                Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "SchemaTest#2026");
                await SchemaBootstrap.InitializeEmptyAsync(Database, ct);
            }
            finally
            {
                Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previous);
                BootstrapEnvironmentLock.Release();
            }
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }
}
