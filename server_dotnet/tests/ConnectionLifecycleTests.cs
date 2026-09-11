using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

[CollectionDefinition("Connection lifecycle database tests", DisableParallelization = true)]
public sealed class ConnectionLifecycleCollection
{
    public const string Name = "Connection lifecycle database tests";
}

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class ConnectionLifecycleTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("/api/v1/admin/system/storage")]
    [InlineData("/api/v1/admin/audit-logs")]
    public async Task SystemEndpointFilterReleasesPermissionConnectionBeforeHandler(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("endpoint", ct);
        await database.SeedAsync("""
            CREATE TABLE roles(
                id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(64) NOT NULL,
                status VARCHAR(16) NOT NULL, is_built_in BOOLEAN NOT NULL
            );
            CREATE TABLE permissions(id BIGINT UNSIGNED PRIMARY KEY, code VARCHAR(64) NOT NULL);
            CREATE TABLE role_permissions(role_id BIGINT UNSIGNED NOT NULL, permission_id BIGINT UNSIGNED NOT NULL);
            CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
            CREATE TABLE users(id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(64) NOT NULL);
            CREATE TABLE system_configs(cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL);
            CREATE TABLE audit_logs(
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT UNSIGNED NULL, employee_no VARCHAR(64) NULL,
                action VARCHAR(100) NOT NULL, target_type VARCHAR(100) NULL,
                target_id VARCHAR(100) NULL, detail JSON NULL, ip VARCHAR(64) NULL,
                created_at DATETIME(6) NOT NULL
            );
            INSERT INTO roles VALUES(1,'系统管理员','ACTIVE',1);
            INSERT INTO permissions VALUES(1,'config:manage'),(2,'log:view');
            INSERT INTO role_permissions VALUES(1,1),(1,2);
            INSERT INTO user_roles VALUES(1,1);
            INSERT INTO users VALUES(1,'pool_admin');
            INSERT INTO system_configs VALUES('storage.warn_percent','85');
            """, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.Database);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<SystemService>();
        builder.Services.AddSingleton<MailService>();
        await using var application = builder.Build();
        application.MapSystemModule();
        var endpoint = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText?.TrimEnd('/') == route.TrimEnd('/')
                && item.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) == true);
        Assert.NotNull(endpoint.RequestDelegate);

        await using var requestScope = application.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = route;
        context.Items[typeof(CurrentUser)] = new CurrentUser(1, "pool_admin", "INTERNAL", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        context.RequestAborted = deadline.Token;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.Body.Length > 0);
    }

    [Fact(Timeout = 30_000)]
    public async Task StorageWarningReleasesConfigurationConnectionBeforeStorageRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("storage_warning", ct);
        await database.SeedAsync("""
            CREATE TABLE system_configs(cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL);
            CREATE TABLE users(
                id BIGINT UNSIGNED PRIMARY KEY, email VARCHAR(128) NOT NULL,
                employee_no VARCHAR(64) NOT NULL, real_name VARCHAR(100) NOT NULL,
                status VARCHAR(16) NOT NULL
            );
            CREATE TABLE roles(
                id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(64) NOT NULL,
                status VARCHAR(16) NOT NULL, is_built_in BOOLEAN NOT NULL
            );
            CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
            CREATE TABLE email_outbox(
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                event_type VARCHAR(32) NOT NULL, dedupe_key VARCHAR(160) NOT NULL UNIQUE,
                recipient_user_id BIGINT UNSIGNED NULL, recipient_email VARCHAR(128) NOT NULL,
                subject VARCHAR(255) NOT NULL, body TEXT NOT NULL,
                status VARCHAR(16) NOT NULL, retry_count INT NOT NULL,
                created_at DATETIME(6) NOT NULL
            );
            INSERT INTO system_configs VALUES('notify.enabled','true'),('storage.warn_percent','0.000001');
            INSERT INTO users VALUES(1,'admin@example.invalid','pool_admin','Pool Admin','ACTIVE');
            INSERT INTO roles VALUES(1,'系统管理员','ACTIVE',1);
            INSERT INTO user_roles VALUES(1,1);
            """, ct);
        var audit = new AuditService(Array.Empty<IProjectAuditCapture>());
        var system = new SystemService(database.Database, audit, database.Options);
        var service = new MailService(database.Database, database.Options, audit, system,
            NullLogger<MailService>.Instance);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        await service.EnqueueStorageWarningAsync(deadline.Token);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE event_type='STORAGE_WARNING' AND recipient_user_id=1 AND status='PENDING'",
            cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationReleasesSeedConnectionBeforeMigrationAdoption()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("bootstrap", ct);
        var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
        Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "PoolLifecycle#2026!");
        try
        {
            await SchemaBootstrap.InitializeEmptyAsync(database.Database, ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword);
        }

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM yf_schema_migrations WHERE version=@version",
            new { version = SchemaMigrations.CurrentVersion }, cancellationToken: ct)));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM seaql_migrations WHERE version=@version",
            new { version = SchemaBootstrap.Version }, cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
                SELECT 1 FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='refresh_tokens'
                    AND column_name='session_id' AND is_nullable='NO'
            )
            """, cancellationToken: ct)));
    }

    private sealed class LocalDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        public AppDb Database { get; } = database;
        public AppOptions Options { get; } = options;

        public static async Task<LocalDatabaseScope> CreateOrSkipAsync(string purpose, CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Connection lifecycle tests only allow local MySQL");
            var credentials = uri.UserInfo.Split(':', 2);
            var administrationOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None
            };
            var administration = new MySqlConnection(administrationOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_test_dotnet_{purpose}_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var applicationOptions = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(administrationOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1,
                        MinimumPoolSize = 0
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_connection_lifecycle_storage"),
                    WorkerEnabled = false
                };
                return new(administration, databaseName, new AppDb(applicationOptions), applicationOptions);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task SeedAsync(string sql, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }
}
