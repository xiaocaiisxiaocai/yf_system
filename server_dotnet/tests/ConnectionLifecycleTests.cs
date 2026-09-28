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
public sealed class ConnectionLifecycleCollectionDefinition
{
    public const string Name = "Connection lifecycle database tests";
}

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class ConnectionLifecycleTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("/api/v1/admin/system/mail-settings")]
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
            CREATE TABLE permissions(
                id BIGINT UNSIGNED PRIMARY KEY,
                code VARCHAR(64) NOT NULL,
                type VARCHAR(16) NOT NULL DEFAULT 'BUTTON',
                sort_no INT NOT NULL DEFAULT 0
            );
            CREATE TABLE role_permissions(role_id BIGINT UNSIGNED NOT NULL, permission_id BIGINT UNSIGNED NOT NULL);
            CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
            CREATE TABLE users(id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(64) NOT NULL, real_name VARCHAR(64) NOT NULL);
            CREATE TABLE projects(id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(255) NOT NULL);
            CREATE TABLE departments(id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(64) NOT NULL);
            CREATE TABLE suppliers(id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(255) NOT NULL);
            CREATE TABLE files(id BIGINT UNSIGNED PRIMARY KEY, original_name VARCHAR(255) NOT NULL);
            CREATE TABLE system_configs(cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL);
            CREATE TABLE audit_logs(
                id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT UNSIGNED NULL, employee_no VARCHAR(64) NULL,
                action VARCHAR(100) NOT NULL, target_type VARCHAR(100) NULL,
                target_id VARCHAR(100) NULL, detail JSON NULL, ip VARCHAR(64) NULL,
                created_at DATETIME(6) NOT NULL,
                actor_realm VARCHAR(16) NULL, actor_account_id BIGINT UNSIGNED NULL
            );
            INSERT INTO roles VALUES(1,'系统管理员','ACTIVE',1);
            INSERT INTO permissions(id,code) VALUES(1,'config:manage'),(2,'log:view');
            INSERT INTO role_permissions VALUES(1,1),(1,2);
            INSERT INTO user_roles VALUES(1,1);
            INSERT INTO users VALUES(1,'pool_admin','连接池测试员');
            """, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.Database);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<SystemService>();
        builder.Services.AddSingleton<SmtpSettingsService>();
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

    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationReleasesSeedConnectionAfterEfMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("bootstrap", ct);
        await SchemaBootstrap.InitializeEmptyAsync(database.Database, "PoolLifecycle#2026!", ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(EfDatabaseLifecycle.InitialMigrationId,
            await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT MigrationId FROM __EFMigrationsHistory", cancellationToken: ct)));
        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='yf_schema_migrations')",
            cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
                SELECT 1 FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='refresh_tokens'
                    AND column_name='session_id' AND is_nullable='NO'
            )
            """, cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task MinimalInitializationAndExplicitResetPreserveAdminPasswordAndSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("reset", ct);
        var root = Path.Combine(Path.GetTempPath(), "yf_reset_" + Guid.NewGuid().ToString("N"));
        database.Options.StorageRoot = root;
        database.Options.JwtSecret = new string('z', 48);
        await SchemaBootstrap.InitializeEmptyAsync(database.Database, "Reset#" + Guid.NewGuid().ToString("N")[..12], ct);
        Directory.CreateDirectory(Path.Combine(root, "files", "2026"));
        Directory.CreateDirectory(Path.Combine(root, "tmp", "owned"));
        await File.WriteAllTextAsync(Path.Combine(root, "files", "2026", "sample.pdf"), "owned file", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "tmp", "owned", "0.part"), "owned chunk", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "unmanaged file", ct);
        try
        {
            string passwordHash;
            int permissionCount;
            await using (var conn = await database.Database.OpenAsync(ct))
            {
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users"));
                Assert.Equal("admin", await conn.ExecuteScalarAsync<string>("SELECT employee_no FROM users"));
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM roles"));
                Assert.Equal("系统管理员", await conn.ExecuteScalarAsync<string>("SELECT name FROM roles"));
                passwordHash = await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users") ?? throw new InvalidOperationException("Missing initialized password hash");
                permissionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions");
                await conn.ExecuteAsync("""
                    INSERT INTO roles(name,is_built_in,status) VALUES('待清理角色',0,'ACTIVE');
                    INSERT INTO users(employee_no,password_hash,real_name,email,user_type,status) SELECT 'discard',password_hash,'待清理用户','','INTERNAL','ACTIVE' FROM users WHERE employee_no='admin';
                    INSERT INTO user_roles(user_id,role_id) SELECT u.id,r.id FROM users u JOIN roles r ON r.name='待清理角色' WHERE u.employee_no='discard';
                    INSERT INTO suppliers(name) VALUES('待清理供应商');
                    INSERT INTO project_groups(name,supplier_id,created_by)
                    SELECT '待清理主项目',s.id,u.id FROM suppliers s JOIN users u ON u.employee_no='discard'
                    WHERE s.name='待清理供应商';
                    INSERT INTO projects(project_group_id,name,supplier_id,created_by)
                    SELECT g.id,'待清理子项目',s.id,u.id
                    FROM project_groups g JOIN suppliers s ON s.name='待清理供应商' JOIN users u ON u.employee_no='discard'
                    WHERE g.name='待清理主项目';
                    INSERT INTO messages(project_id,sender_id,content) SELECT p.id,p.created_by,'待清理留言' FROM projects p;
                    UPDATE system_configs SET cfg_value='false' WHERE cfg_key='notify.enabled';
                    INSERT INTO system_configs(cfg_key,cfg_value) VALUES('smtp.reset-test-secret','retained-test-value');
                    """);
            }
            var plan = await DevelopmentDataReset.InspectAsync(database.Options, ct);
            Assert.True(plan.RobotCatalogWillBeReinitialized);
            Assert.Equal(7, plan.RobotCatalogSupplierCount);
            Assert.Equal(27, plan.RobotCatalogPartCount);
            Assert.Equal(8, plan.Counts["suppliers"]);
            Assert.Equal(27, plan.Counts["robot_parts"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentDataReset.ResetAsync(database.Options, "wrong-db", plan.StorageRoot, ct));
            Assert.True(File.Exists(Path.Combine(root, "files", "2026", "sample.pdf")));
            // Like the site, the lease holder uses its own non-pooled connection.
            await using (var app = new MySqlConnection(new MySqlConnectionStringBuilder(database.Options.ConnectionString) { Pooling = false }.ConnectionString))
            {
                await app.OpenAsync(ct);
                // A running site holds the application lease; reset must refuse before touching anything.
                await using var lease = await AppRunningLease.TryAcquireAsync(app, ct);
                Assert.NotNull(lease);
                var running = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct));
                Assert.Contains("still running", running.Message);
                Assert.True(File.Exists(Path.Combine(root, "files", "2026", "sample.pdf")));
                await using var check = await database.Database.OpenAsync(ct);
                Assert.Equal(2, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users"));
            }
            var result = await DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct);
            Assert.True(result.ResetCompleted);
            Assert.Equal(1, result.Counts["users"]);
            Assert.Equal(1, result.Counts["roles"]);
            foreach (var table in new[] { "projects", "messages", "departments", "refresh_tokens", "audit_logs" }) Assert.Equal(0, result.Counts[table]);
            Assert.Equal(7, result.Counts["suppliers"]);
            Assert.Equal(27, result.Counts["robot_parts"]);
            Assert.True(result.RobotCatalogWillBeReinitialized);
            Assert.Equal(7, result.RobotCatalogSupplierCount);
            Assert.Equal(27, result.RobotCatalogPartCount);
            Assert.Equal(permissionCount, result.Counts["permissions"]);
            Assert.Equal(permissionCount, result.Counts["role_permissions"]);
            Assert.Equal(1, result.Counts["user_roles"]);
            Assert.False(Directory.Exists(Path.Combine(root, "files")));
            Assert.False(Directory.Exists(Path.Combine(root, "tmp")));
            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
            await using (var conn = await database.Database.OpenAsync(ct))
            {
                Assert.Equal(passwordHash, await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users WHERE employee_no='admin'"));
                Assert.Equal("retained-test-value", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='smtp.reset-test-secret'"));
                Assert.Equal("false", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='notify.enabled'"));
                Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"));
                Assert.Equal(27, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"));
            }
            var repeated = await DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct);
            Assert.True(repeated.ResetCompleted);
            Assert.Equal(7, repeated.Counts["suppliers"]);
            Assert.Equal(27, repeated.Counts["robot_parts"]);
            await SchemaBootstrap.ValidateAsync(database.Database, ct);
        }
        finally
        {
            Yf.Api.Modules.Files.FileStorage.DeleteDirectoryTree(Path.GetTempPath(), root, CancellationToken.None);
        }
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
            var databaseName = $"yf_t_{Guid.NewGuid():N}";
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
