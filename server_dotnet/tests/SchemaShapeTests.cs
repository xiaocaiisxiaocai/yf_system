using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class SchemaShapeTests
{
    public static IEnumerable<object[]> StructuralMutations()
    {
        var mutations = new[]
        {
            new object[] { "missing-primary-key", "ALTER TABLE message_reads ADD KEY idx_shape_message_id(message_id), DROP PRIMARY KEY" },
            new object[] { "wrong-column-definition", "ALTER TABLE messages MODIFY status varchar(32) COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'NORMAL'" },
            new object[] { "missing-unique-index", "ALTER TABLE user_roles DROP INDEX uk_user_roles_user_id" },
            new object[] { "missing-foreign-key", "ALTER TABLE message_reads DROP FOREIGN KEY fk_mr_user" },
            new object[] { "wrong-foreign-key-rule", "ALTER TABLE message_reads DROP FOREIGN KEY fk_mr_user; ALTER TABLE message_reads ADD CONSTRAINT fk_mr_user_shape FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE" },
            new object[] { "reordered-business-index", "CREATE INDEX idx_shape_message_project ON messages(project_id); ALTER TABLE messages DROP INDEX idx_msg_project_time, ADD INDEX idx_msg_project_time(created_at,project_id)" },
            new object[] { "wrong-engine", "ALTER TABLE audit_logs ENGINE=MyISAM" }
        };
        foreach (var mutation in mutations)
        {
            yield return [mutation[0], mutation[1], false];
            yield return [mutation[0], mutation[1], true];
        }
    }

    public static IEnumerable<object[]> InvalidLegacySessionShapes()
    {
        foreach (var startup in new[] { false, true })
        {
            yield return ["wrong-session-length", startup];
            yield return ["wrong-session-index", startup];
        }
    }

    public static IEnumerable<object[]> InvalidDotNetHistoryShapes()
    {
        var mutations = new[]
        {
            "ALTER TABLE yf_schema_migrations DROP PRIMARY KEY",
            "ALTER TABLE yf_schema_migrations MODIFY name varchar(64) NOT NULL"
        };
        foreach (var mutation in mutations)
        {
            yield return [mutation, false];
            yield return [mutation, true];
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationProducesStrictlyValidSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_empty", ct);
        var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
        Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "Shape#" + Guid.NewGuid().ToString("N")[..12] + "!");
        try
        {
            await SchemaBootstrap.InitializeEmptyAsync(database.Database, ct);
            await SchemaBootstrap.ValidateAsync(database.Database, ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task V17AndCurrentDotNetMigrationsRemainRestartable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_v17", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(SchemaMigrations.CurrentVersion, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM yf_schema_migrations", cancellationToken: ct)));
        Assert.True(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='collaboration_reads')",
            cancellationToken: ct)));
        Assert.False(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='project_members')",
            cancellationToken: ct)));
        Assert.False(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM permissions WHERE code='project:member')", cancellationToken: ct)));
        Assert.True(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM role_permissions rp
              JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id
              WHERE r.is_built_in=1 AND r.name='供应商人员' AND p.code='project:withdraw')
            """, cancellationToken: ct)));
        Assert.Equal(11, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM information_schema.table_constraints
            WHERE constraint_schema=DATABASE() AND constraint_type='FOREIGN KEY'
              AND constraint_name IN (
                'fk_project_groups_created_by','fk_project_groups_robot_vendor','fk_project_groups_robot_model',
                'fk_project_groups_responsible_user','fk_project_groups_section','fk_project_groups_priority',
                'fk_projects_robot_vendor','fk_projects_robot_model','fk_projects_responsible_user',
                'fk_projects_section','fk_projects_priority')
            """, cancellationToken: ct)));
    }

    [Fact(Timeout = 60_000)]
    public async Task InternalAcceptanceMigrationsAreScopedAuditedAndRestartable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_acceptance_v3", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync(
            """
            DELETE FROM yf_schema_migrations WHERE version>=3;
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT r.id,p.id FROM roles r CROSS JOIN permissions p
            WHERE r.name='供应商人员' AND p.code='project:confirm';

            INSERT INTO suppliers(id,name,status) VALUES(9101,'v3迁移供应商','ACTIVE');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password)
            VALUES
              (9201,'v3-internal','unused','内部验收人','internal-v3@example.test','INTERNAL',NULL,'ACTIVE',0),
              (9202,'v3-supplier','unused','供应商提交人','supplier-v3@example.test','SUPPLIER',9101,'ACTIVE',0),
              (9203,'v4-global-reviewer','unused','全局验收人','global-v4@example.test','INTERNAL',NULL,'ACTIVE',0),
              (9204,'v4-confirm-only','unused','缺项目菜单验收人','confirm-only-v4@example.test','INTERNAL',NULL,'ACTIVE',0),
              (9205,'v4-shared-inbox','unused','共享收件邮箱验收人','global-v4@example.test','INTERNAL',NULL,'ACTIVE',0);
            INSERT INTO roles(id,name,description,is_built_in,status)
            VALUES
              (9001,'v4全局验收人','迁移测试角色',0,'ACTIVE'),
              (9002,'v4缺项目菜单','迁移测试角色',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9001,id FROM permissions WHERE code IN ('project:list','project:confirm','project:view_all');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9002,id FROM permissions WHERE code='project:confirm';
            INSERT INTO user_roles(user_id,role_id)
            SELECT 9201,id FROM roles WHERE name='内部成员';
            INSERT INTO user_roles(user_id,role_id) VALUES(9203,9001),(9204,9002),(9205,9001);

            INSERT INTO project_groups(id,name,supplier_id,status,created_by)
            VALUES
              (9601,'v3待供应商确认主项目',9101,'IN_PROGRESS',9201),
              (9602,'v3已待公司确认主项目',9101,'IN_PROGRESS',9201),
              (9603,'v3已完成历史主项目',9101,'COMPLETED',9201),
              (9604,'v3进行中历史主项目',9101,'IN_PROGRESS',9201);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,confirm_side,created_by)
            VALUES
              (9301,9601,'v3待供应商确认',9101,'PENDING_CONFIRMATION','SUPPLIER',9201),
              (9302,9602,'v3已待公司确认',9101,'PENDING_CONFIRMATION','COMPANY',9201),
              (9303,9603,'v3已完成历史',9101,'COMPLETED','SUPPLIER',9201),
              (9304,9604,'v3进行中历史',9101,'IN_PROGRESS','SUPPLIER',9201);

            INSERT INTO project_status_logs(id,project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at)
            VALUES
              (9401,9301,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',9201,'SUPPLIER',NULL,'2026-09-01 00:00:01.000'),
              (9402,9301,'PENDING_CONFIRMATION','IN_PROGRESS','REJECT',9202,'SUPPLIER','旧周期驳回','2026-09-01 00:00:02.000'),
              (9403,9301,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',9202,'SUPPLIER',NULL,'2026-09-01 00:00:03.000'),
              (9411,9302,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',9202,'COMPANY',NULL,'2026-09-01 00:00:04.000'),
              (9421,9303,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',9201,'SUPPLIER',NULL,'2026-09-01 00:00:05.000'),
              (9422,9303,'PENDING_CONFIRMATION','COMPLETED','CONFIRM',9202,'SUPPLIER',NULL,'2026-09-01 00:00:06.000');

            INSERT INTO email_outbox(id,event_type,project_id,recipient_user_id,recipient_email,subject,body,status,retry_count,next_attempt_at,sent_at)
            VALUES
              (9501,'PROJECT_SUBMITTED',9301,9202,'supplier-v3@example.test','旧待发','旧供应商确认','PENDING',0,NULL,NULL),
              (9502,'PROJECT_SUBMITTED',9301,9202,'supplier-v3@example.test','旧失败','旧供应商确认','FAILED',3,NULL,NULL),
              (9503,'PROJECT_SUBMITTED',9301,9202,'supplier-v3@example.test','旧发送中','旧供应商确认','SENDING',0,UTC_TIMESTAMP()+INTERVAL 10 MINUTE,NULL),
              (9504,'PROJECT_SUBMITTED',9301,9202,'supplier-v3@example.test','旧已发送','旧供应商确认','SENT',0,NULL,UTC_TIMESTAMP()),
              (9505,'PROJECT_REJECTED',9301,9202,'supplier-v3@example.test','其他事件','保留','PENDING',0,NULL,NULL),
              (9506,'PROJECT_SUBMITTED',9303,9202,'supplier-v3@example.test','已完成历史','保留','PENDING',0,NULL,NULL),
              (9507,'PROJECT_SUBMITTED',9301,9201,'internal-v3@example.test','内部通知','保留','PENDING',0,NULL,NULL),
              (9508,'PROJECT_SUBMITTED',9302,9202,'supplier-v3@example.test','旧错误收件人','旧供应商确认','PENDING',0,NULL,NULL),
              (9509,'PROJECT_SUBMITTED',9301,NULL,'legacy-v3@example.test','旧无用户收件人','旧供应商确认','PENDING',0,NULL,NULL);
            """, ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("DELETE FROM yf_schema_migrations WHERE version>=4", ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(
            ["9301:PENDING_CONFIRMATION:COMPANY", "9302:PENDING_CONFIRMATION:COMPANY", "9303:COMPLETED:SUPPLIER", "9304:IN_PROGRESS:SUPPLIER"],
            (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT CONCAT(id,':',status,':',COALESCE(confirm_side,'NULL')) FROM projects WHERE id BETWEEN 9301 AND 9304 ORDER BY id",
                cancellationToken: ct))).ToArray());
        Assert.Equal(
            ["9401:SUPPLIER", "9402:SUPPLIER", "9403:COMPANY", "9411:COMPANY", "9421:SUPPLIER", "9422:SUPPLIER"],
            (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT CONCAT(id,':',COALESCE(confirm_side,'NULL')) FROM project_status_logs WHERE id BETWEEN 9401 AND 9422 ORDER BY id",
                cancellationToken: ct))).ToArray());
        Assert.Equal(
            ["9501:CANCELLED", "9502:CANCELLED", "9503:CANCELLED", "9504:SENT", "9505:PENDING", "9506:CANCELLED", "9507:CANCELLED", "9508:CANCELLED", "9509:CANCELLED"],
            (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT CONCAT(id,':',status) FROM email_outbox WHERE id BETWEEN 9501 AND 9509 ORDER BY id",
                cancellationToken: ct))).ToArray());
        Assert.Equal(
            [
                "project-acceptance:9301:9403:9203",
                "project-acceptance:9302:9411:9203",
            ],
            (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT dedupe_key FROM email_outbox WHERE status='PENDING' AND dedupe_key LIKE 'project-acceptance:%' ORDER BY dedupe_key",
                cancellationToken: ct))).ToArray());
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE' AND target_type='schema' AND target_id='4'",
            cancellationToken: ct)));
        Assert.Equal(SchemaMigrations.InternalAcceptanceCancelledMailReason,
            await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT last_error FROM email_outbox WHERE id=9501", cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_ACCEPTANCE_MIGRATE' AND target_type='project' AND target_id=9301",
            cancellationToken: ct)));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM role_permissions rp JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id WHERE r.name='供应商人员' AND p.code='project:confirm'",
            cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM role_permissions rp JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id WHERE r.name='内部成员' AND p.code='project:confirm'",
            cancellationToken: ct)));
        Assert.Equal(SchemaMigrations.CurrentVersion, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM yf_schema_migrations", cancellationToken: ct)));
    }

    [Fact(Timeout = 60_000)]
    public async Task LegacyDotNetHistoryTableInNonUnicodeDefaultDatabaseRemainsSupported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync(
            "shape_history_collation", ct, "utf8mb4_general_ci");
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.RecreateMigrationTableWithLegacyDdlAsync(ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V16AndInterruptedSessionColumnRemainRestartable(bool interruptedAfterColumn)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_v16", ct);
        await database.CreateBaselineAsync(legacyV16: true, ct);
        if (interruptedAfterColumn)
        {
            await database.ExecuteAsync(
                "ALTER TABLE refresh_tokens ADD COLUMN session_id VARCHAR(36) NULL AFTER user_id", ct);
            await database.InsertRefreshTokenAsync(includeSessionColumn: true, ct);
        }
        else
        {
            await database.InsertRefreshTokenAsync(includeSessionColumn: false, ct);
        }

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(SchemaBootstrap.Version, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT version FROM seaql_migrations ORDER BY version DESC LIMIT 1", cancellationToken: ct)));
        Assert.False(string.IsNullOrWhiteSpace(await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT session_id FROM refresh_tokens WHERE id=7001", cancellationToken: ct))));
    }

    [Fact(Timeout = 60_000)]
    public async Task HarmlessAdditionalColumnAndOrdinaryIndexAreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_extra", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await database.ExecuteAsync("""
            ALTER TABLE projects ADD COLUMN integration_note varchar(64) NULL;
            CREATE INDEX idx_projects_integration_note ON projects(integration_note);
            """, ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task GeneratedColumnFormattingDifferencesAreNormalized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_generated", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await database.ExecuteAsync("""
            ALTER TABLE departments MODIFY business_parent_scope bigint unsigned
                GENERATED ALWAYS AS (((IFNULL(`parent_id`, 0)))) STORED
            """, ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await SchemaBootstrap.ValidateAsync(database.Database, ct);
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(StructuralMutations))]
    public async Task StructuralDriftIsRejectedWithoutFurtherSchemaHistoryOrDataChanges(
        string _, string mutationSql, bool startupValidation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_drift", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        if (startupValidation) await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync(mutationSql, ct);
        var before = await database.SnapshotAsync(ct);

        if (startupValidation)
        {
            await using var conn = await database.Database.OpenAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ValidateAsync(conn, ct));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(database.Database, ct));
        }

        Assert.Equal(before, await database.SnapshotAsync(ct));
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(InvalidLegacySessionShapes))]
    public async Task ExistingInvalidV16SessionShapeIsRejectedBeforeAnyWrite(string mutation, bool startupValidation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_session", ct);
        await database.CreateBaselineAsync(legacyV16: !startupValidation, ct);
        if (startupValidation)
        {
            await SchemaMigrations.ApplyAsync(database.Database, ct);
            await database.ExecuteAsync(
                $"DELETE FROM seaql_migrations WHERE version='{SchemaBootstrap.Version}'", ct);
        }

        if (mutation == "wrong-session-length")
        {
            if (startupValidation)
                await database.ExecuteAsync("ALTER TABLE refresh_tokens DROP INDEX idx_refresh_tokens_session_state, MODIFY session_id varchar(12) NULL", ct);
            else
                await database.ExecuteAsync("ALTER TABLE refresh_tokens ADD COLUMN session_id varchar(12) NULL AFTER user_id", ct);
        }
        else
        {
            if (startupValidation)
                await database.ExecuteAsync("ALTER TABLE refresh_tokens DROP INDEX idx_refresh_tokens_session_state, ADD INDEX idx_refresh_tokens_session_state(session_id,user_id)", ct);
            else
                await database.ExecuteAsync("ALTER TABLE refresh_tokens ADD COLUMN session_id varchar(36) NULL AFTER user_id, ADD INDEX idx_refresh_tokens_session_state(session_id,user_id)", ct);
        }
        await database.InsertRefreshTokenAsync(includeSessionColumn: true, ct);
        var before = await database.SnapshotAsync(ct);

        if (startupValidation)
        {
            await using var conn = await database.Database.OpenAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ValidateAsync(conn, ct));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(database.Database, ct));
        }

        Assert.Equal(before, await database.SnapshotAsync(ct));
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(InvalidDotNetHistoryShapes))]
    public async Task DamagedDotNetHistoryTableIsRejectedBeforeAnyWrite(
        string mutationSql, bool startupValidation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_history", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync(mutationSql, ct);
        var before = await database.SnapshotAsync(ct);

        if (startupValidation)
        {
            await using var conn = await database.Database.OpenAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ValidateAsync(conn, ct));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(database.Database, ct));
        }

        Assert.Equal(before, await database.SnapshotAsync(ct));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModifiedDotNetHistoryRowIsRejectedBeforeAnyWrite(bool startupValidation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaDatabaseScope.CreateOrSkipAsync("shape_history_row", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("UPDATE yf_schema_migrations SET checksum=REPEAT('0',64)", ct);
        var before = await database.SnapshotAsync(ct);

        if (startupValidation)
        {
            await using var conn = await database.Database.OpenAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ValidateAsync(conn, ct));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrations.ApplyAsync(database.Database, ct));
        }

        Assert.Equal(before, await database.SnapshotAsync(ct));
    }

    internal sealed class SchemaDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database) : IAsyncDisposable
    {
        private static readonly string[] DataSnapshotTables =
        [
            "roles", "permissions", "role_permissions", "system_configs", "seaql_migrations",
            "yf_schema_migrations", "collaboration_reads", "message_images", "project_copies", "file_copy_refs",
            "refresh_tokens", "audit_logs"
        ];

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
                throw new InvalidOperationException("Schema shape tests only allow local MySQL");
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
                AllowUserVariables = true
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_ss_{purpose}_{Guid.NewGuid():N}";
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
                        MinimumPoolSize = 0
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_schema_shape_storage"),
                    WorkerEnabled = false
                };
                return new(administration, databaseName, new AppDb(options));
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async Task CreateBaselineAsync(bool legacyV16, CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            using var resource = typeof(SchemaBootstrap).Assembly.GetManifestResourceStream(
                "Yf.Api.Infrastructure.schema-baseline.json")
                ?? throw new InvalidOperationException("Embedded schema baseline missing.");
            using var baseline = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
            await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", cancellationToken: ct));
            try
            {
                foreach (var table in baseline.RootElement.GetProperty("tables").EnumerateArray())
                    await conn.ExecuteAsync(new CommandDefinition(
                        table.GetProperty("sql").GetString()!, cancellationToken: ct));
            }
            finally
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "SET FOREIGN_KEY_CHECKS=1", cancellationToken: CancellationToken.None));
            }

            foreach (var table in baseline.RootElement.GetProperty("seeds").EnumerateObject())
            {
                foreach (var row in table.Value.EnumerateArray())
                    await InsertJsonRowAsync(conn, table.Name, row, ct);
            }
            if (legacyV16)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM seaql_migrations WHERE version='{SchemaBootstrap.Version}'",
                    cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "ALTER TABLE refresh_tokens DROP INDEX idx_refresh_tokens_session_state, DROP COLUMN session_id",
                    cancellationToken: ct));
            }
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async Task RecreateMigrationTableWithLegacyDdlAsync(CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            var rows = await conn.QueryAsync<MigrationHistoryRow>(new CommandDefinition(
                "SELECT version,name,checksum,applied_at AppliedAt FROM yf_schema_migrations ORDER BY version",
                cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("DROP TABLE yf_schema_migrations", cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                CREATE TABLE yf_schema_migrations (
                    version INT NOT NULL PRIMARY KEY,
                    name VARCHAR(128) NOT NULL,
                    checksum CHAR(64) NOT NULL,
                    applied_at DATETIME(6) NOT NULL
                )
                """, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES(@Version,@Name,@Checksum,@AppliedAt)",
                rows, cancellationToken: ct));
        }

        public async Task InsertRefreshTokenAsync(bool includeSessionColumn, CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", cancellationToken: ct));
            try
            {
                var columns = includeSessionColumn ? "id,user_id,session_id,token_hash,expires_at,revoked" : "id,user_id,token_hash,expires_at,revoked";
                var values = includeSessionColumn ? "7001,9001,'sentinel','shape-token',UTC_TIMESTAMP() + INTERVAL 1 DAY,0" : "7001,9001,'shape-token',UTC_TIMESTAMP() + INTERVAL 1 DAY,0";
                await conn.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO refresh_tokens({columns}) VALUES({values})", cancellationToken: ct));
            }
            finally
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "SET FOREIGN_KEY_CHECKS=1", cancellationToken: CancellationToken.None));
            }
        }

        public async Task<DatabaseSnapshot> SnapshotAsync(CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            var names = (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND table_type='BASE TABLE' ORDER BY table_name",
                cancellationToken: ct))).ToArray();
            var schema = new StringBuilder();
            foreach (var name in names)
            {
                await using var command = new MySqlCommand($"SHOW CREATE TABLE `{name}`", conn);
                await using var reader = await command.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                schema.Append(name).Append('\n').Append(reader.GetString(1)).Append("\n--\n");
            }

            var data = new StringBuilder();
            foreach (var table in DataSnapshotTables)
            {
                if (!names.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    data.Append(table).Append(":<missing>\n");
                    continue;
                }
                data.Append(table).Append(':').Append(await ReadRowsAsync(conn, table, ct)).Append('\n');
            }
            return new(schema.ToString(), data.ToString());
        }

        private static async Task InsertJsonRowAsync(
            MySqlConnection conn, string table, JsonElement row, CancellationToken ct)
        {
            var fields = row.EnumerateObject().ToArray();
            var parameters = new DynamicParameters();
            for (var i = 0; i < fields.Length; i++)
            {
                var value = fields[i].Value;
                parameters.Add("p" + i, value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => value.GetInt64(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => fields[i].Name.EndsWith("_at", StringComparison.Ordinal)
                        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                            ? at
                            : value.GetString()
                });
            }
            var sql = $"INSERT INTO `{table}` ({string.Join(',', fields.Select(field => $"`{field.Name}`"))}) VALUES ({string.Join(',', fields.Select((_, i) => "@p" + i))})";
            await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }

        private static async Task<string> ReadRowsAsync(
            MySqlConnection conn, string table, CancellationToken ct)
        {
            await using var command = new MySqlCommand($"SELECT * FROM `{table}`", conn);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    values[i] = value switch
                    {
                        null => "<null>",
                        byte[] bytes => Convert.ToBase64String(bytes),
                        DateTime date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                    };
                }
                rows.Add(string.Join('\u001f', values));
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join('\u001e', rows);
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }

    internal sealed record DatabaseSnapshot(string Schema, string Data);
    private sealed record MigrationHistoryRow(int Version, string Name, string Checksum, DateTime AppliedAt);
}
