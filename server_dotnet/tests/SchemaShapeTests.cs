using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class SchemaShapeTests
{
    [Fact]
    public async Task RobotPartCatalogEmbeddedSnapshotIsCompleteAndPinned()
    {
        var assembly = typeof(AppDb).Assembly;
        var resourceName = Assert.Single(assembly.GetManifestResourceNames(),
            name => name.EndsWith("robot-parts-20260923.json", StringComparison.Ordinal));
        await using var resource = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(resource);
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var bytes = buffer.ToArray();

        Assert.Equal("daced1ae638796f5a4f097b9e1025834ab6abe409266d20096f0ff73d26de384",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal("aa2256021de680a4b03c8bff32d9df2684ef56d5b47efadb86c47583429b11ab",
            document.RootElement.GetProperty("source").GetProperty("sha256").GetString());
        var parts = document.RootElement.GetProperty("parts").EnumerateArray().ToArray();
        Assert.Equal(27, parts.Length);
        Assert.Equal(7, parts.Select(part => part.GetProperty("supplierName").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.All(parts, part =>
        {
            Assert.False(string.IsNullOrWhiteSpace(part.GetProperty("supplierName").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(part.GetProperty("partNumber").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(part.GetProperty("model").GetString()));
            Assert.True(part.GetProperty("sourceRow").GetInt32() > 1);
        });
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDatabaseWarnsAboutBootstrapSecretWithoutPrintingIt(bool autoInitialize)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("bootstrap_warning", ct);
        await database.InitializeAsync(ct);
        database.Options.AutoInitializeDatabase = autoInitialize;
        database.Options.BootstrapPassword = "PrivateBootstrap#42";
        var original = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            await EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct);
            Assert.Contains("WARNING", captured.ToString());
            Assert.Contains("App:BootstrapPassword", captured.ToString());
            Assert.DoesNotContain(database.Options.BootstrapPassword, captured.ToString());
            captured.GetStringBuilder().Clear();
            database.Options.BootstrapPassword = "";
            await EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct);
            Assert.Empty(captured.ToString());
        }
        finally { Console.SetError(original); }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstStartupInitializesMissingOrEmptyDatabaseOnce(bool createDatabase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("auto_init", ct, createDatabase: createDatabase);
        database.Options.AutoInitializeDatabase = true;
        database.Options.BootstrapPassword = "FirstStart#2026";
        var cs = new MySqlConnectionStringBuilder(database.Options.ConnectionString) { MaximumPoolSize = 5 };
        database.Options.ConnectionString = cs.ConnectionString;
        await Task.WhenAll(EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct),
            EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct));
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT must_change_password FROM users WHERE employee_no='admin'"));
        var hash = await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users WHERE employee_no='admin'");
        Assert.True(await Yf.Api.Modules.Identity.PasswordService.VerifyAsync("FirstStart#2026", hash!, ct));
        await conn.ExecuteAsync("UPDATE users SET real_name='RestartSentinel', must_change_password=0 WHERE employee_no='admin'");
        database.Options.BootstrapPassword = ""; // Existing installs no longer require bootstrap credentials.
        await EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct);
        Assert.Equal(hash, await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users WHERE employee_no='admin'"));
        Assert.Equal("RestartSentinel", await conn.ExecuteScalarAsync<string>("SELECT real_name FROM users WHERE employee_no='admin'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT must_change_password FROM users WHERE employee_no='admin'"));
        Assert.Equal(9, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }

    [Fact(Timeout = 120_000)]
    public async Task AutomaticStartupRefusesUnmanagedAndPendingMigrationWithoutChangingData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("auto_refuse", ct);
        database.Options.AutoInitializeDatabase = true;
        database.Options.BootstrapPassword = "FirstStart#2026";
        await database.ExecuteAsync("CREATE TABLE sentinel(id INT PRIMARY KEY); INSERT INTO sentinel VALUES (17)", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct));
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(17, await conn.ExecuteScalarAsync<int>("SELECT id FROM sentinel"));
            Assert.False(await TableExistsAsync(conn, "__EFMigrationsHistory", ct));
        }
        await database.ExecuteAsync("DROP TABLE sentinel", ct);
        await using (var db = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct))
            await db.GetService<IMigrator>().MigrateAsync("20260918153503_AddOemPlatform", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct));
        await using var after = await database.Database.OpenAsync(ct);
        Assert.True(await TableExistsAsync(after, "oem_transfers", ct));
        Assert.Equal(2, await after.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }

    [Fact(Timeout = 120_000)]
    public async Task DisabledInitializationOrInvalidPasswordDoesNotCreateDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("auto_disabled", ct, createDatabase: false);
        var missing = await Assert.ThrowsAsync<MySqlException>(() => EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct));
        Assert.Equal(1049, missing.Number);
        database.Options.AutoInitializeDatabase = true;
        database.Options.BootstrapPassword = "bad";
        await Assert.ThrowsAsync<ApiException>(() => EfDatabaseLifecycle.PrepareStartupAsync(database.Options, ct));
        missing = await Assert.ThrowsAsync<MySqlException>(async () => { await using var conn = await database.Database.OpenAsync(ct); });
        Assert.Equal(1049, missing.Number);
    }

    [Fact(Timeout = 120_000)]
    public async Task DropMigrationRemovesOnlyOemSharedRowsAndCanRestoreHistoricalSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_drop_oem", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260918153503_AddOemPlatform", ct);
        await database.ExecuteAsync("""
            INSERT INTO audit_logs(employee_no,action,target_type,target_id,detail,ip,created_at,actor_realm,actor_account_id)
            VALUES
              ('internal-user','PROJECT_CREATE','project','1',NULL,NULL,UTC_TIMESTAMP(6),NULL,NULL),
              ('oem-user','OEM_TRANSFER_CREATE','oem_transfer','1',NULL,NULL,UTC_TIMESTAMP(6),'oem',1);
            INSERT INTO email_outbox(event_type,dedupe_key,project_id,recipient_user_id,recipient_email,subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at,recipient_realm,recipient_account_id,oem_transfer_id)
            VALUES
              ('PROJECT_SUBMITTED','normal-mail',NULL,NULL,'normal@example.invalid','normal','normal','PENDING',0,NULL,NULL,NULL,UTC_TIMESTAMP(6),NULL,NULL,NULL),
              ('OEM_TRANSFER_RELEASED','oem-mail',NULL,NULL,'oem@example.invalid','oem','oem','PENDING',0,NULL,NULL,NULL,UTC_TIMESTAMP(6),'oem',1,1);
            """, ct);

        await migrator.MigrateAsync("20260923005853_DropOemPlatform", ct);
        await using (var connection = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs"));
            Assert.Equal("PROJECT_CREATE", await connection.ExecuteScalarAsync<string>("SELECT action FROM audit_logs"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM email_outbox"));
            Assert.Equal("PROJECT_SUBMITTED", await connection.ExecuteScalarAsync<string>("SELECT event_type FROM email_outbox"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions WHERE code='oem' OR code LIKE 'oem:%' OR code='dept:leader_manage'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_configs WHERE cfg_key LIKE 'oem.%'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name LIKE 'oem\\_%'"));
        }

        await migrator.MigrateAsync("20260918153503_AddOemPlatform", ct);
        await using var restored = await database.Database.OpenAsync(ct);
        Assert.True(await TableExistsAsync(restored, "oem_transfers", ct));
        Assert.Equal(14, await restored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions WHERE code='oem' OR code LIKE 'oem:%' OR code='dept:leader_manage'"));
        Assert.Equal(30, await restored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_configs WHERE cfg_key LIKE 'oem.%'"));
    }

    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationUsesEfHistoryAndAdminOnlySeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("ef_empty_init", ct);
        await database.InitializeAsync(ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(new[] {
                    EfDatabaseLifecycle.InitialMigrationId,
                    "20260918153503_AddOemPlatform",
                    "20260923005853_DropOemPlatform",
                    "20260923032837_FileListIndex",
                    "20260923064746_UnreadWindowIndexes",
                    "20260923071427_RefreshTokenExpiryIndex",
                    "20260923094642_AddRobotPartCatalog",
                    "20260923131700_HardenSessionsAndQueryIndexes",
                    "20260923141854_AddProjectCopyJobs",
                },
                (await conn.QueryAsync<string>("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId")).ToArray());
            Assert.False(await TableExistsAsync(conn, "yf_schema_migrations", ct));
            Assert.False(await TableExistsAsync(conn, "seaql_migrations", ct));
            Assert.Equal("admin", await conn.ExecuteScalarAsync<string>("SELECT employee_no FROM users"));
            Assert.Equal("系统管理员", await conn.ExecuteScalarAsync<string>("SELECT name FROM roles"));
            Assert.Equal(35, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions"));
            Assert.Equal(35, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM role_permissions"));
            Assert.Equal(13, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_configs"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name LIKE 'oem\\_%'"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND (column_name LIKE 'oem\\_%' OR column_name IN ('recipient_realm','recipient_account_id','actor_realm','actor_account_id','leader_account_id'))"));
            Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_dictionaries WHERE type='PRIORITY'"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM project_dictionaries WHERE type IN ('ROBOT_VENDOR','ROBOT_MODEL')"));
            Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"));
            Assert.Equal(27, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"));
            Assert.Equal(9, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM robot_parts rp
                INNER JOIN suppliers s ON s.id=rp.supplier_id
                WHERE BINARY s.name=BINARY '珞石'
                """));
        }
        await SchemaBootstrap.ValidateAsync(database.Database, ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task RobotPartCatalogUpgradePreservesLegacyProjectsAndExactSupplierIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("robot_catalog_upgrade", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260923071427_RefreshTokenExpiryIndex", ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8101,'珞石','DISABLED','2026-01-01 00:00:00','2026-01-01 00:00:00'),
                  (8102,'络石','ACTIVE','2026-01-01 00:00:00','2026-01-01 00:00:00');
            INSERT INTO departments(id,name,parent_id,sort_no,status,kind,created_at,updated_at)
            VALUES(8103,'旧项目课别',NULL,0,'ACTIVE','SECTION','2026-01-01 00:00:00','2026-01-01 00:00:00');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8104,'legacy-owner','unused','旧项目负责人','legacy@example.test','INTERNAL',8103,'ACTIVE',0,0,'2026-01-01 00:00:00','2026-01-01 00:00:00');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8105,'ROBOT_VENDOR','络石',NULL,1,'ACTIVE'),
                  (8106,'ROBOT_MODEL','旧 Robot 型号原文',8105,1,'ACTIVE'),
                  (8107,'PRIORITY','旧项目优先级',NULL,1,'ACTIVE');
            INSERT INTO project_groups
              (id,name,description,supplier_id,status,created_by,machine_model,robot_vendor_id,robot_model_id,
               responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
            VALUES(8108,'旧主项目','升级保护',8102,'DRAFT',8104,'旧机型',8105,8106,8104,8103,8107,
                   '2026-12-31','2026-01-02 03:04:05','2026-01-02 03:04:05');
            INSERT INTO projects
              (id,project_group_id,name,description,supplier_id,status,created_by,machine_model,robot_vendor_id,robot_model_id,
               responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
            VALUES(8109,8108,'旧子项目','升级保护',8102,'DRAFT',8104,'旧机型',8105,8106,8104,8103,8107,
                   '2026-12-31','2026-01-02 03:04:05','2026-01-02 03:04:05');
            """, ct);

        await migrator.MigrateAsync("20260923094642_AddRobotPartCatalog", ct);
        await migrator.MigrateAsync("20260923094642_AddRobotPartCatalog", ct);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(27, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"));
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"));
        Assert.Equal(8101UL, await conn.ExecuteScalarAsync<ulong>("SELECT id FROM suppliers WHERE BINARY name=BINARY '珞石'"));
        Assert.Equal("DISABLED", await conn.ExecuteScalarAsync<string>("SELECT status FROM suppliers WHERE id=8101"));
        Assert.Equal(8102UL, await conn.ExecuteScalarAsync<ulong>("SELECT id FROM suppliers WHERE BINARY name=BINARY '络石'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM robot_parts rp INNER JOIN suppliers s ON s.id=rp.supplier_id
            WHERE BINARY s.name=BINARY '络石'
            """));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_dictionaries WHERE type IN ('ROBOT_VENDOR','ROBOT_MODEL')"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_dictionaries WHERE id=8107 AND type='PRIORITY'"));
        Assert.False(await ColumnExistsAsync(conn, "projects", "robot_vendor_id", ct));
        Assert.False(await ColumnExistsAsync(conn, "projects", "robot_model_id", ct));
        Assert.False(await ColumnExistsAsync(conn, "project_groups", "robot_vendor_id", ct));
        Assert.False(await ColumnExistsAsync(conn, "project_groups", "robot_model_id", ct));

        var group = await conn.QuerySingleAsync<LegacyProjectState>(new CommandDefinition("""
            SELECT supplier_id AS SupplierId,responsible_user_id AS ResponsibleUserId,section_id AS SectionId,
                   robot_part_id AS RobotPartId,legacy_robot_model_name AS LegacyRobotModelName,
                   expected_completion_date AS ExpectedCompletionDate,updated_at AS UpdatedAt
            FROM project_groups WHERE id=8108
            """, cancellationToken: ct));
        var project = await conn.QuerySingleAsync<LegacyProjectState>(new CommandDefinition("""
            SELECT supplier_id AS SupplierId,responsible_user_id AS ResponsibleUserId,section_id AS SectionId,
                   robot_part_id AS RobotPartId,legacy_robot_model_name AS LegacyRobotModelName,
                   expected_completion_date AS ExpectedCompletionDate,updated_at AS UpdatedAt
            FROM projects WHERE id=8109
            """, cancellationToken: ct));
        foreach (var row in new[] { group, project })
        {
            Assert.Equal(8102UL, row.SupplierId);
            Assert.Equal(8104UL, row.ResponsibleUserId);
            Assert.Equal(8103UL, row.SectionId);
            Assert.Null(row.RobotPartId);
            Assert.Equal("旧 Robot 型号原文", row.LegacyRobotModelName);
            Assert.Equal(new DateTime(2026, 12, 31), row.ExpectedCompletionDate);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), row.UpdatedAt);
        }
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
        Assert.Equal(9, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='admin'"));
        Assert.Equal(27, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"));
        Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"));
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

    private static async Task<bool> ColumnExistsAsync(
        MySqlConnection connection, string table, string column, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)",
            new { table, column }, cancellationToken: ct));

    private sealed class LegacyProjectState
    {
        public ulong SupplierId { get; init; }
        public ulong ResponsibleUserId { get; init; }
        public ulong SectionId { get; init; }
        public ulong? RobotPartId { get; init; }
        public string? LegacyRobotModelName { get; init; }
        public DateTime ExpectedCompletionDate { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

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
            string databaseCollation = "utf8mb4_unicode_ci",
            bool createDatabase = true)
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
                if (createDatabase) await administration.ExecuteAsync(new CommandDefinition(
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
