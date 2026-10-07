using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

/// <summary>
/// Down/Up round trips for the OEM restore and the newest migrations, plus the upgrade path of a
/// pre-OEM database: schema returns to the exact shape, reference data is re-seeded idempotently,
/// and the irreversible validation migration refuses to roll back explicitly.
/// </summary>
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class MigrationRoundTripTests
{
    private const string BeforeOem = "20260928063650_AddRefreshTokenRevokeReason";
    private const string RestoreOem = "20261006015013_RestoreOemPlatform";
    private const string ReplaceScanning = "20261006031500_ReplaceOemMalwareScanningWithValidation";
    private const string DirectoryDelete = "20261006032324_AddOemDirectoryDeletePermissions";
    private const string HardenIdentity = "20261006075638_HardenOemIdentityAndIndexes";
    private const string Latest = "20261007003510_AllowMacroAndBinaryExcelUploads";

    /// <summary>Every permission the upgrade must add (the fresh-install catalog's OEM/leader codes).</summary>
    private static readonly string[] OemAndLeaderCodes =
    [
        "dept:leader_manage", "oem", "oem:account_delete", "oem:account_manage", "oem:approval_recover", "oem:audit_view",
        "oem:company_delete", "oem:company_manage", "oem:file_download", "oem:file_policy_manage", "oem:flow_approve",
        "oem:flow_template_manage", "oem:notify_manage", "oem:retention_template_manage", "oem:transfer_create", "oem:transfer_view",
    ];

    [Fact(Timeout = 180_000)]
    public async Task RestoreOemPlatformDownThenUpReturnsToTheSameSchemaAndReferenceData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("restore_oem_round_trip", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOem, ct);
        await SeedPreOemCatalogAsync(database, ct);
        var preOemSchema = await SchemaAsync(database, ct);
        var preOemData = await ReferenceDataAsync(database, ct);

        await migrator.MigrateAsync(RestoreOem, ct);
        var restoredSchema = await SchemaAsync(database, ct);
        var restoredData = await ReferenceDataAsync(database, ct);
        Assert.Contains(restoredSchema, line => line.StartsWith("table|oem_transfers", StringComparison.Ordinal));
        Assert.NotEqual(preOemData, restoredData);

        await migrator.MigrateAsync(BeforeOem, ct);
        Assert.Equal(preOemSchema, await SchemaAsync(database, ct));
        Assert.Equal(preOemData, await ReferenceDataAsync(database, ct));

        await migrator.MigrateAsync(RestoreOem, ct);
        await migrator.MigrateAsync(RestoreOem, ct);
        Assert.Equal(restoredSchema, await SchemaAsync(database, ct));
        Assert.Equal(restoredData, await ReferenceDataAsync(database, ct));
        Assert.Equal(RestoreOem, await LastMigrationAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task NewestMigrationsRoundTripAndTheValidationMigrationRefusesToRollBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("newest_round_trip", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        Assert.Equal(Latest, await LastMigrationAsync(database, ct));
        var latestSchema = await SchemaAsync(database, ct);
        var latestData = await ReferenceDataAsync(database, ct);

        // AllowMacroAndBinaryExcelUploads (Down keeps the whitelist) + AddOemApprovalTaskActivatedAt.
        await migrator.MigrateAsync(HardenIdentity, ct);
        Assert.DoesNotContain(await SchemaAsync(database, ct), line => line.StartsWith("column|oem_flow_tasks|activated_at|", StringComparison.Ordinal));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));

        // ... + HardenOemIdentityAndIndexes.
        await migrator.MigrateAsync(DirectoryDelete, ct);
        Assert.DoesNotContain(await SchemaAsync(database, ct), line => line.StartsWith("index|oem_upload_sessions|idx_oem_upload_sessions_uploader|", StringComparison.Ordinal));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));

        // ... + AddOemDirectoryDeletePermissions: its codes and grants go, and come back once.
        await migrator.MigrateAsync(ReplaceScanning, ct);
        await using (var connection = await database.Database.OpenAsync(ct))
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM permissions WHERE code IN ('oem:company_delete','oem:account_delete')"));
        var replaceSchema = await SchemaAsync(database, ct);
        var replaceData = await ReferenceDataAsync(database, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));

        // ReplaceOemMalwareScanningWithValidation.Down is deliberately unsupported (validation is
        // not a malware verdict). From its own state the rollback fails before anything changes.
        await migrator.MigrateAsync(ReplaceScanning, ct);
        var refused = await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(RestoreOem, ct));
        Assert.Contains("backup", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReplaceScanning, await LastMigrationAsync(database, ct));
        Assert.Equal(replaceSchema, await SchemaAsync(database, ct));
        Assert.Equal(replaceData, await ReferenceDataAsync(database, ct));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task RollingBackPastTheValidationMigrationFromLatestIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("refuse_scan_rollback", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();

        var latestSchema = await SchemaAsync(database, ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(BeforeOem, ct));
        // EF reverts migration by migration, each in its own transaction: the reversible newer
        // migrations are rolled back, then the irreversible one refuses. The history never drops
        // below it and stays a consistent prefix that a forward migration completes again.
        Assert.Equal(ReplaceScanning, await LastMigrationAsync(database, ct));
        Assert.Contains(RestoreOem, await HistoryAsync(database, ct));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(Latest, await LastMigrationAsync(database, ct));
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task UpgradeGrantsLeaderAndOemPermissionsOnlyToTheBuiltInAdministratorExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("upgrade_admin_permissions", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOem, ct);
        await SeedPreOemCatalogAsync(database, ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (2,'自定义管理员',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (3,'项目管理员',1,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 2,id FROM permissions;
            INSERT INTO role_permissions(role_id,permission_id) SELECT 3,id FROM permissions WHERE code='dashboard';
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        var catalog = (await connection.QueryAsync<(string Code, int Count)>("""
            SELECT code, COUNT(*) FROM permissions
            WHERE code='oem' OR code LIKE 'oem:%' OR code='dept:leader_manage' GROUP BY code ORDER BY code
            """)).ToArray();
        Assert.Equal(OemAndLeaderCodes, catalog.Select(row => row.Code).ToArray());
        Assert.All(catalog, row => Assert.Equal(1, row.Count));

        var adminGrants = (await connection.QueryAsync<(string Code, int Count)>("""
            SELECT p.code, COUNT(*) FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=1 GROUP BY p.code ORDER BY p.code
            """)).ToArray();
        Assert.All(adminGrants, row => Assert.Equal(1, row.Count));
        Assert.Equal(PreOemCodes.Concat(OemAndLeaderCodes).Order(StringComparer.Ordinal).ToArray(),
            adminGrants.Select(row => row.Code).Order(StringComparer.Ordinal).ToArray());
        // Neither a custom role (even one holding every pre-upgrade permission) nor another built-in role is widened.
        Assert.Equal(PreOemCodes.Order(StringComparer.Ordinal).ToArray(), (await connection.QueryAsync<string>(
            "SELECT p.code FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=2")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["dashboard"], (await connection.QueryAsync<string>(
            "SELECT p.code FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=3")).ToArray());
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM permissions child JOIN permissions parent ON parent.id=child.parent_id
            WHERE child.code='dept:leader_manage' AND parent.code='org:dept'
            """));
        Assert.Equal(OemAndLeaderCodes.Length - 2, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM permissions child JOIN permissions parent ON parent.id=child.parent_id
            WHERE child.code LIKE 'oem:%' AND parent.code='oem'
            """));
    }

    private static readonly string[] PreOemCodes = ["dashboard", "org:dept", "org:user", "user:manage", "dept:manage"];

    /// <summary>What a database bootstrapped before the OEM restore holds: its catalog, the built-in admin and its role.</summary>
    private static Task SeedPreOemCatalogAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct) =>
        database.ExecuteAsync("""
            INSERT INTO permissions(id,code,name,type,parent_id,sort_no) VALUES
              (1,'dashboard','工作台','MENU',NULL,0),
              (4,'org:user','用户管理','MENU',NULL,3),
              (5,'org:dept','组织架构','MENU',NULL,4),
              (28,'user:manage','用户管理','ACTION',4,19),
              (30,'dept:manage','组织架构管理','ACTION',5,21);
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at)
            VALUES(1,'系统管理员',1,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 1,id FROM permissions;
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(1,'admin','$argon2id$placeholder','系统管理员','admin@example.invalid','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO user_roles(user_id,role_id) VALUES(1,1);
            """, ct);

    /// <summary>Tables, columns, indexes, foreign keys and triggers of the scratch schema (ordinal positions excluded).</summary>
    private static async Task<string[]> SchemaAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT CONCAT_WS('|','table',table_name,engine,table_collation) FROM information_schema.tables WHERE table_schema=DATABASE()
            UNION ALL
            SELECT CONCAT_WS('|','column',table_name,column_name,column_type,is_nullable,IFNULL(column_default,'<null>'),extra,IFNULL(collation_name,'<none>'))
            FROM information_schema.columns WHERE table_schema=DATABASE()
            UNION ALL
            SELECT CONCAT_WS('|','index',table_name,index_name,non_unique,seq_in_index,column_name,IFNULL(sub_part,'<all>'))
            FROM information_schema.statistics WHERE table_schema=DATABASE()
            UNION ALL
            SELECT CONCAT_WS('|','fk',rc.table_name,rc.constraint_name,rc.referenced_table_name,rc.update_rule,rc.delete_rule)
            FROM information_schema.referential_constraints rc WHERE rc.constraint_schema=DATABASE()
            UNION ALL
            SELECT CONCAT_WS('|','trigger',trigger_name,event_object_table,action_timing,event_manipulation)
            FROM information_schema.triggers WHERE trigger_schema=DATABASE()
            """, cancellationToken: ct));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Seeded reference data compared by natural keys (auto-increment ids may differ after a re-seed).</summary>
    private static async Task<string[]> ReferenceDataAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        var hasOem = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='oem_flow_templates')");
        var sql = """
            SELECT CONCAT_WS('|','permission',p.code,p.name,p.type,IFNULL(parent.code,'<root>'),p.sort_no)
            FROM permissions p LEFT JOIN permissions parent ON parent.id=p.parent_id
            UNION ALL
            SELECT CONCAT_WS('|','grant',rp.role_id,p.code) FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
            UNION ALL
            SELECT CONCAT_WS('|','config',cfg_key,cfg_value) FROM system_configs
            """;
        if (hasOem)
            sql += """

                UNION ALL
                SELECT CONCAT_WS('|','flow_template',id,name,is_default,status) FROM oem_flow_templates
                UNION ALL
                SELECT CONCAT_WS('|','flow_node',template_id,sort_no,name,approver_source,approval_mode,self_policy,enabled) FROM oem_flow_template_nodes
                UNION ALL
                SELECT CONCAT_WS('|','retention',id,name,mode,status) FROM oem_retention_templates
                """;
        var rows = (await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))).ToArray();
        // Duplicate rows would collapse in a set comparison; keep them so a double seed is caught.
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> HistoryAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return (await connection.QueryAsync<string>("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId")).ToArray();
    }

    private static async Task<string> LastMigrationAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct) =>
        (await HistoryAsync(database, ct)).Last();
}
