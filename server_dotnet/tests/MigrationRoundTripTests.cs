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
    private const string BeforeElectricalHandoff = "20261124003700_CorrectOutsourceDueDatesFromCustomerDate";
    private const string BeforeMechanicalSupervisor = "20261124010000_AddOutsourceElectricalHandoff";
    private const string RenameSupervisor = "20261124032519_RenameOutsourceMechanicalSupervisor";
    private const string BeforeOutsourceTransferLink = "20261124075430_RestoreOutsourceSectionLabel";
    private const string BeforeOutsourceFilesField = "20261124083731_AddOutsourceTransferLink";
    private const string BeforeOutsourceTypeElectricalHandoff = "20261124093646_AddOutsourceFilesField";
    private const string RenameHandoffLabel = "20261125030000_RenameOutsourceHandoffLabel";
    private const string WidenRecipientEmail = "20261125140406_WidenOutsourceMailRecipientEmail";
    private const string BeforeOutsourceContacts = "20261128092337_AddAnnouncementRichContent";
    private const string Latest = "20261128114631_AddOutsourceContacts";

    /// <summary>Every permission the upgrade must add (the fresh-install catalog's OEM/leader codes).</summary>
    private static readonly string[] OemAndLeaderCodes =
    [
        "dept:leader_manage", "oem", "oem:account_delete", "oem:account_manage", "oem:approval_recover", "oem:audit_view",
        "oem:company_delete", "oem:company_manage", "oem:directory_view", "oem:file_download", "oem:file_policy_manage",
        "oem:flow_approve", "oem:flow_template_manage", "oem:notify_manage", "oem:retention_template_manage",
        "oem:transfer_create", "oem:transfer_delete", "oem:transfer_update", "oem:transfer_view",
    ];

    /// <summary>CRUD codes outside the OEM prefix that the data-scope migration adds to the administrator.</summary>
    private static readonly string[] NonOemCrudCodes = ["dict:delete", "role:view", "supplier:view", "user:view"];

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
    public async Task MechanicalSupervisorRenamePreservesFieldIdentityAndSettingsOnUpgradeAndRollback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("supervisor_field_label", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeMechanicalSupervisor, ct);
        async Task<(ulong Id, string Label, int Sort, bool Visible, string Status)> ReadFieldAsync()
        {
            await using var conn = await database.Database.OpenAsync(ct);
            return await conn.QuerySingleAsync<(ulong, string, int, bool, string)>("""
                SELECT id, label, sort_no, vendor_visible, status FROM outsource_fields
                WHERE field_key='mechanicalSection' AND built_in=1
                """);
        }

        await database.ExecuteAsync("""
            UPDATE outsource_fields SET sort_no=321, vendor_visible=0, status='DISABLED'
            WHERE field_key='mechanicalSection' AND built_in=1
            """, ct);
        var before = await ReadFieldAsync();
        Assert.Equal("机构课别", before.Label);

        await migrator.MigrateAsync(RenameSupervisor, ct);
        Assert.Equal((before.Id, "上级主管", 321, false, "DISABLED"), await ReadFieldAsync());
        await migrator.MigrateAsync(BeforeMechanicalSupervisor, ct);
        Assert.Equal(before, await ReadFieldAsync());
    }

    [Fact(Timeout = 180_000)]
    public async Task AddOutsourceTransferLinkAddsColumnAndPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_transfer_link", ct);
        await database.InitializeWithMigrationsAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOutsourceTransferLink, ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (2,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (3,'项目管理员',1,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT r.id,p.id FROM roles r CROSS JOIN permissions p
            WHERE r.id IN (2,3) AND (p.code='outsource' OR p.code LIKE 'outsource:%');
            INSERT INTO permissions(id,code,name,type,sort_no)
            VALUES(234,'custom:permission','自定义权限','ACTION',1);
            """, ct);
        var beforeSchema = await SchemaAsync(database, ct);
        var beforeData = await ReferenceDataAsync(database, ct);

        await migrator.MigrateAsync(BeforeOutsourceFilesField, ct);
        await using (var connection = await database.Database.OpenAsync(ct))
        {
            var column = await connection.QuerySingleOrDefaultAsync<(string Type, string Nullable)>(new CommandDefinition("""
                SELECT column_type, is_nullable FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='oem_transfers' AND column_name='outsource_item_id'
                """, cancellationToken: ct));
            Assert.NotNull(column.Type);
            Assert.Matches(@"^bigint(?:\(20\))? unsigned$", column.Type);
            Assert.Equal("YES", column.Nullable);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM information_schema.statistics
                WHERE table_schema=DATABASE() AND table_name='oem_transfers'
                  AND column_name='outsource_item_id' AND seq_in_index=1
                """, cancellationToken: ct)));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM information_schema.key_column_usage k
                JOIN information_schema.referential_constraints r
                  ON r.constraint_schema=k.constraint_schema AND r.constraint_name=k.constraint_name AND r.table_name=k.table_name
                WHERE k.table_schema=DATABASE() AND k.table_name='oem_transfers' AND k.column_name='outsource_item_id'
                  AND k.referenced_table_schema=DATABASE() AND k.referenced_table_name='outsource_items'
                  AND k.referenced_column_name='id' AND r.delete_rule='RESTRICT'
                """, cancellationToken: ct)));
            Assert.Equal(("外协档案", "ACTION", "outsource"),
                await connection.QuerySingleAsync<(string, string, string)>(new CommandDefinition("""
                    SELECT child.name, child.type, parent.code FROM permissions child
                    JOIN permissions parent ON parent.id=child.parent_id WHERE child.code='outsource:file_view'
                    """, cancellationToken: ct)));
            Assert.Equal(("系统管理员", true),
                Assert.Single(await connection.QueryAsync<(string, bool)>(new CommandDefinition("""
                    SELECT r.name, r.is_built_in FROM roles r JOIN role_permissions rp ON rp.role_id=r.id
                    JOIN permissions p ON p.id=rp.permission_id WHERE p.code='outsource:file_view'
                    """, cancellationToken: ct))));
        }

        await migrator.MigrateAsync(BeforeOutsourceTransferLink, ct);
        Assert.Equal(beforeSchema, await SchemaAsync(database, ct));
        Assert.Equal(beforeData, await ReferenceDataAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task AddOutsourceFilesFieldInsertsTheColumnAfterWorkOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_files_field", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOutsourceFilesField, ct);
        // Exercise an upgraded database with a previously customised order, including a tie.
        await database.ExecuteAsync("UPDATE outsource_fields SET sort_no=204 WHERE field_key IN ('workOrderNo','customer')", ct);

        async Task<string[]> KeysAsync()
        {
            await using var conn = await database.Database.OpenAsync(ct);
            return (await conn.QueryAsync<string>("SELECT field_key FROM outsource_fields ORDER BY sort_no,id")).ToArray();
        }

        var before = await KeysAsync();
        await migrator.MigrateAsync(BeforeOutsourceTypeElectricalHandoff, ct);
        var after = await KeysAsync();
        Assert.Equal(before.ToList().IndexOf("workOrderNo") + 1, Array.IndexOf(after, "files"));
        Assert.Equal(before, after.Where(x => x != "files"));
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal((true, "档案", "TEXT", "ACTIVE", true, false, "NONE"),
                await conn.QuerySingleAsync<(bool, string, string, string, bool, bool, string)>("""
                    SELECT built_in,label,data_type,status,vendor_visible,vendor_editable,editor_scope
                    FROM outsource_fields WHERE field_key='files'
                    """));
        }

        await migrator.MigrateAsync(BeforeOutsourceFilesField, ct);
        Assert.Equal(before, await KeysAsync());
    }

    [Fact(Timeout = 180_000)]
    public async Task AddOutsourceTypeElectricalHandoffDefaultsExistingAndNewTypesToAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_type_handoff", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOutsourceTypeElectricalHandoff, ct);
        await database.ExecuteAsync("""
            INSERT INTO outsource_types(code, name, status) VALUES ('LEGACY-ACTIVE', '启用类型', 'ACTIVE'),
                ('LEGACY-DISABLED', '停用类型', 'DISABLED')
            """, ct);
        var before = await SchemaAsync(database, ct);

        await migrator.MigrateAsync(Latest, ct);
        await database.ExecuteAsync("INSERT INTO outsource_types(code, name) VALUES ('NEW-TYPE', '新增类型')", ct);
        await using (var connection = await database.Database.OpenAsync(ct))
        {
            var allowed = await connection.QueryAsync<bool>(new CommandDefinition("""
                SELECT allow_electrical_handoff FROM outsource_types
                WHERE code IN ('LEGACY-ACTIVE', 'LEGACY-DISABLED', 'NEW-TYPE')
                """, cancellationToken: ct));
            Assert.Equal([true, true, true], allowed);
        }

        await migrator.MigrateAsync(BeforeOutsourceTypeElectricalHandoff, ct);
        Assert.Equal(before, await SchemaAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task RenameOutsourceHandoffLabelUpdatesTheBuiltInFieldAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_handoff_label", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        const string before = "20261125015533_AddOutsourceTypeElectricalHandoff";
        await migrator.MigrateAsync(before, ct);
        Assert.Equal("是否转迅得电控", await HandoffLabelAsync(database, ct));

        await migrator.MigrateAsync(Latest, ct);
        Assert.Equal("转迅得研发电控设计", await HandoffLabelAsync(database, ct));

        await migrator.MigrateAsync(before, ct);
        Assert.Equal("是否转迅得电控", await HandoffLabelAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task WidenOutsourceMailRecipientEmailUpgradesAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("widen_recipient_email", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(RenameHandoffLabel, ct);
        var before = await SchemaAsync(database, ct);
        Assert.Contains(before, line => line.StartsWith("column|outsource_mail_recipients|email|varchar(128)|", StringComparison.Ordinal));

        await migrator.MigrateAsync(WidenRecipientEmail, ct);
        var after = await SchemaAsync(database, ct);
        Assert.Contains(after, line => line.StartsWith("column|outsource_mail_recipients|email|varchar(254)|", StringComparison.Ordinal));

        await migrator.MigrateAsync(RenameHandoffLabel, ct);
        Assert.Equal(before, await SchemaAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task AccountNetworkScopeDefaultsExistingUsersAndPreservesSettingsOnRollback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("account_network_scope", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(WidenRecipientEmail, ct);
        await database.ExecuteAsync("""
            INSERT INTO users(employee_no,password_hash,real_name,email,user_type)
            VALUES ('legacy-internal','placeholder','旧账号','','INTERNAL');
            """, ct);
        var before = await SchemaAsync(database, ct);

        await migrator.MigrateAsync(Latest, ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("ANY", await conn.ExecuteScalarAsync<string>("SELECT network_scope FROM users WHERE employee_no='legacy-internal'"));
            Assert.Equal("183.238.37.108:8090", await conn.ExecuteScalarAsync<string>(
                "SELECT cfg_value FROM system_configs WHERE cfg_key='security.external_hosts'"));
            Assert.Equal("10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1", await conn.ExecuteScalarAsync<string>(
                "SELECT cfg_value FROM system_configs WHERE cfg_key='security.internal_networks'"));
            var revision = await conn.ExecuteScalarAsync<ulong>("SELECT CAST(cfg_value AS UNSIGNED) FROM system_configs WHERE cfg_key='security.identity_revision'");
            await conn.ExecuteAsync("UPDATE users SET network_scope='INTERNAL_ONLY' WHERE employee_no='legacy-internal'");
            Assert.Equal(revision + 1, await conn.ExecuteScalarAsync<ulong>("SELECT CAST(cfg_value AS UNSIGNED) FROM system_configs WHERE cfg_key='security.identity_revision'"));
            await conn.ExecuteAsync("UPDATE system_configs SET cfg_value='outside.example:443' WHERE cfg_key='security.external_hosts'");
        }

        await migrator.MigrateAsync(WidenRecipientEmail, ct);
        Assert.Equal(before, await SchemaAsync(database, ct));
        await migrator.MigrateAsync(Latest, ct);
        await using var restored = await database.Database.OpenAsync(ct);
        Assert.Equal("outside.example:443", await restored.ExecuteScalarAsync<string>(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='security.external_hosts'"));
        Assert.Equal("ANY", await restored.ExecuteScalarAsync<string>("SELECT network_scope FROM users WHERE employee_no='legacy-internal'"));
    }

    private static async Task<string?> HandoffLabelAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT label FROM outsource_fields WHERE field_key='electricalHandoff'", cancellationToken: ct));
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
        var latestCatalog = await CatalogCountsAsync(database, ct);
        Assert.Equal((6, 40, 5), latestCatalog);

        await migrator.MigrateAsync(BeforeElectricalHandoff, ct);
        var beforeElectricalHandoff = await SchemaAsync(database, ct);
        foreach (var name in new[] { "announcements", "announcement_reads", "announcement_companies", "announcement_departments",
                     "announcement_assets", "announcement_asset_links" })
        {
            Assert.Contains(latestSchema, line => line.StartsWith($"table|{name}|", StringComparison.Ordinal));
            Assert.DoesNotContain(beforeElectricalHandoff, line => line.StartsWith($"table|{name}|", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(beforeElectricalHandoff, line => line.StartsWith("table|outsource_default_cc_modules|", StringComparison.Ordinal));
        Assert.DoesNotContain(beforeElectricalHandoff, line => line.StartsWith("column|outsource_items|electrical_handoff", StringComparison.Ordinal));
        Assert.DoesNotContain(beforeElectricalHandoff, line => line.StartsWith("column|users|network_scope|", StringComparison.Ordinal));
        Assert.Contains(beforeElectricalHandoff, line => line.StartsWith("column|outsource_mails|kind|varchar(16)|", StringComparison.Ordinal));
        Assert.Contains(beforeElectricalHandoff, line => line.StartsWith("column|outsource_mails|created_by|bigint(20) unsigned|NO|", StringComparison.Ordinal)
            || line.StartsWith("column|outsource_mails|created_by|bigint unsigned|NO|", StringComparison.Ordinal));
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_fields WHERE field_key='electricalHandoff'"));
            await conn.ExecuteAsync("INSERT INTO outsource_default_cc(user_id) SELECT id FROM users WHERE employee_no='admin'");
        }
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("OUTSOURCE_NOTICE", await conn.QuerySingleAsync<string>("SELECT module FROM outsource_default_cc_modules"));
            Assert.Equal(new[] { "status", "electricalHandoff", "workOrderNo" }, await conn.QueryAsync<string>(
                "SELECT field_key FROM outsource_fields WHERE field_key IN ('status','electricalHandoff','workOrderNo') ORDER BY sort_no, id"));
            await conn.ExecuteAsync("DELETE FROM outsource_default_cc");
        }

        // AddProjectRobotOwnerName + SeedArmCatalogAndRobotTypes (Down keeps the catalog, Up skips existing rows) + AddProjectRobotType
        // + AllowMacroAndBinaryExcelUploads (Down keeps the whitelist) + AddOemApprovalTaskActivatedAt + AddMessageClientId
        // + AddProjectMaterialReview (Down keeps its notification switch row) + AddSupplierContactsAndSignatures
        // + RetireAcceptanceWorkflow (Down brings back only projects.confirm_side, not the retired permission codes)
        // + AddProjectNotificationsAndDueReminders (Down drops only the reminder columns, keeping the config rows)
        // + AddStorageLocations (Down drops storage_locations, keeping the storage:manage permission and grants)
        // + AddProjectMaterialRound (Down drops projects.material_file_floor)
        // + AddOemCompanyContacts (Down drops the OEM additional-contact directory)
        // + RemoveProjectGroups (Down rebuilds one main project per project, not the retired project:view_all grant)
        // + AddOutsourceSettings (Down drops the outsourcing tables, keeping the outsource permission codes and grants)
        // + AddOutsourceItems (Down drops outsource_items)
        // + AddOutsourceSyncState (Down drops outsource_sync_state)
        // + AddOutsourceItemChanges (Down drops outsource_item_changes)
        // + AddOutsourceMails (Down drops the mail tables and the sent-snapshot columns)
        // + AddOemInboundRecipients (Down drops the notification-list removals and per-transfer chosen recipients).
        // + AddOemTransferSigner (Down drops the vendor signer columns on oem_transfers).
        // + DropOemCompanyRecipientExclusions (one notification list for every vendor; the per-vendor table is gone again).
        // + RemoveOemArchiveLimitSettings (Down restores nothing; the older version reads the missing archive limits as defaults).
        // + AddIpmsConnectionSettings (Down drops the page-set IPMS account, encrypted password and fetch time).
        // + AddIpmsScheduleSwitches (Down drops the on/off switches of the daily fetch and the daily outsourcing update).
        // + AddMesStagingOrder (Down drops the MES row order of staged rows).
        // + AddEmailOutboxCopyOnly (Down drops email_outbox.copy_only).
        // + AddOutsourceItemModifiedBy (Down drops outsource_items.modified_by and modified_at).
        // + UseOemCompaniesAsOutsourceVendors (Down brings back the empty outsourcing vendor tables).
        // + AddOutsourceCustomerAliases (Down drops outsource_customer_aliases).
        // + AddDataScopesAndCrudPermissions (Down drops the two data-range tables, keeping the CRUD permission
        //   rows and grants so reference data still matches the freshest catalog).
        // + AddOutsourceFieldSettings (Down drops the field settings tables and the vendor-update columns, keeping
        //   the outsource:field_settings permission and its grant).
        // + AddOutsourceFieldEditorUsers (Down drops outsource_field_editor_users).
        // + SplitOutsourceSendPermission (Down keeps outsource:send, its grants and the new outsource:manage name).
        // + SimplifyOutsourcePermissions (Down does not bring back outsource:view/import/field_settings or the old names).
        // + AddOutsourceDateImportPermission (Down keeps outsource:date_import and its administrator grant).
        // + StopInternalRobotVEditing (Down leaves 是否有RobotV at 内部不填写).
        // + AddOutsourceVendorEmails (Down drops outsource_vendor_emails).
        // + CorrectOutsourceDueDatesFromCustomerDate (data only; Down keeps the corrected due dates).
        // + AddOutsourceElectricalHandoff (Down drops handoff fields and default CC module assignments).
        // + AddOutsourceTransferLink (Down drops the link and the outsource:file_view permission and grant).
        // + AddOutsourceFilesField (Down removes the files column from the field catalog).
        await migrator.MigrateAsync(HardenIdentity, ct);
        Assert.Equal(latestCatalog, await CatalogCountsAsync(database, ct));
        var rolledBack = await SchemaAsync(database, ct);
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|oem_flow_tasks|activated_at|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|project_groups|robot_type_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|robot_type_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|project_groups|robot_owner_name|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|robot_owner_name|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|messages|client_message_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|message_images|sha256|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|material_review_status|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|supplier_contacts", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|messages|signer_name|", StringComparison.Ordinal));
        Assert.Contains(rolledBack, line => line.StartsWith("column|projects|confirm_side|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|overdue_notified_for|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|material_file_floor|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|oem_company_contacts", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|outsource_vendor_emails", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|project_robot_owners", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|projects|completed_at|", StringComparison.Ordinal));
        Assert.DoesNotContain(latestSchema, line => line.StartsWith("table|project_group", StringComparison.Ordinal));
        Assert.Contains(rolledBack, line => line.StartsWith("table|project_groups", StringComparison.Ordinal));
        Assert.DoesNotContain(latestSchema, line => line.StartsWith("column|projects|project_group_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|outsource_", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("table|outsource_fields", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("table|outsource_field_editor_users", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("column|outsource_items|vendor_updated_at|", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("table|oem_internal_recipient_exclusions", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|oem_internal_recipient_exclusions", StringComparison.Ordinal));
        Assert.DoesNotContain(latestSchema, line => line.StartsWith("table|oem_company_recipient_exclusions", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("table|oem_transfer_internal_recipients", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|oem_transfer_internal_recipients", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("column|oem_transfers|signer_name|", StringComparison.Ordinal));
        Assert.DoesNotContain(rolledBack, line => line.StartsWith("column|oem_transfers|signer_name|", StringComparison.Ordinal));
        // Outsourcing vendors are the ODM/OEMA companies since UseOemCompaniesAsOutsourceVendors.
        Assert.Contains(latestSchema, line => line.StartsWith("table|outsource_items", StringComparison.Ordinal));
        Assert.DoesNotContain(latestSchema, line => line.StartsWith("table|outsource_vendors", StringComparison.Ordinal));
        // AddMesMirror (Down drops the MES mirror tables) + AddMesMirrorBaseUrl (Down drops mes_mirror_state.base_url).
        Assert.Contains(latestSchema, line => line.StartsWith("column|mes_mirror_state|base_url|", StringComparison.Ordinal));
        Assert.Contains(latestSchema, line => line.StartsWith("column|mes_mirror_state|protected_password|", StringComparison.Ordinal));
        foreach (var table in new[] { "mes_work_orders", "mes_work_orders_staging", "mes_mirror_state" })
        {
            Assert.Contains(latestSchema, line => line.StartsWith("table|" + table, StringComparison.Ordinal));
            Assert.DoesNotContain(rolledBack, line => line.StartsWith("table|" + table, StringComparison.Ordinal));
        }
        Assert.Equal(latestData.Where(line => !line.Contains("|outsource:file_view", StringComparison.Ordinal)),
            await ReferenceDataAsync(database, ct));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestData, await ReferenceDataAsync(database, ct));
        Assert.Equal(latestCatalog, await CatalogCountsAsync(database, ct));

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

    [Theory(Timeout = 180_000)]
    [InlineData(BeforeOem, false)]
    [InlineData(BeforeOem, true)]
    [InlineData(RestoreOem, false)]
    [InlineData(RestoreOem, true)]
    [InlineData("0", false)]
    [InlineData("0", true)]
    public async Task RollingBackPastTheValidationMigrationFromLatestIsRefusedBeforeAnyChanges(
        string targetMigration, bool useAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("refuse_scan_rollback", ct);
        await database.InitializeAsync(ct);
        await SeedRollbackSentinelsAsync(database, ct);
        var latestSchema = await SchemaAsync(database, ct);
        var latestReferenceData = await ReferenceDataAsync(database, ct);
        var latestHistory = await HistoryAsync(database, ct);
        var latestBusinessData = await RollbackSentinelDataAsync(database, ct);

        var refused = await AssertRollbackRefusedAsync(database, targetMigration, useAsync, ct);

        Assert.Contains(ReplaceScanning, refused.Message, StringComparison.Ordinal);
        Assert.Contains("backup", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(latestSchema, await SchemaAsync(database, ct));
        Assert.Equal(latestReferenceData, await ReferenceDataAsync(database, ct));
        Assert.Equal(latestHistory, await HistoryAsync(database, ct));
        Assert.Equal(latestBusinessData, await RollbackSentinelDataAsync(database, ct));

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(new DateTime(2026, 10, 6, 12, 34, 56, 789),
            await connection.ExecuteScalarAsync<DateTime>(
                "SELECT activated_at FROM oem_flow_tasks WHERE id=910001"));
        Assert.Equal("ADMIN_REVOKED", await connection.ExecuteScalarAsync<string>(
            "SELECT revoke_reason FROM oem_refresh_tokens WHERE id=910001"));
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
        Assert.Equal(PreOemCodes.Concat(OemAndLeaderCodes).Concat(OutsourceCodes).Concat(NonOemCrudCodes).Concat(AnnouncementCodes)
                .Order(StringComparer.Ordinal).ToArray(),
            adminGrants.Select(row => row.Code).Order(StringComparer.Ordinal).ToArray());
        var outsourceCatalog = (await connection.QueryAsync<(string Code, int Count)>("""
            SELECT code, COUNT(*) FROM permissions
            WHERE code='outsource' OR code LIKE 'outsource:%' GROUP BY code ORDER BY code
            """)).ToArray();
        Assert.Equal(OutsourceCodes, outsourceCatalog.Select(row => row.Code).ToArray());
        Assert.All(outsourceCatalog, row => Assert.Equal(1, row.Count));
        Assert.Equal(OutsourceCodes.Length - 1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM permissions child JOIN permissions parent ON parent.id=child.parent_id
            WHERE child.code LIKE 'outsource:%' AND parent.code='outsource'
            """));
        // Neither a custom role (even one holding every pre-upgrade permission) nor another built-in role is widened.
        // The CRUD split preserves ability: a role that could already manage users also gains the read-only code.
        Assert.Equal(PreOemCodes.Append("user:view").Order(StringComparer.Ordinal).ToArray(),
            (await connection.QueryAsync<string>(
            "SELECT p.code FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=2")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["dashboard"], (await connection.QueryAsync<string>(
            "SELECT p.code FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=3")).ToArray());
        // A role that only held the user MENU could not read users before the split and must not gain the read code.
        Assert.Equal(["org:user"], (await connection.QueryAsync<string>(
            "SELECT p.code FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=4"))
            .Order(StringComparer.Ordinal).ToArray());
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

    private static readonly string[] AnnouncementCodes =
        ["announcement", "announcement:pin", "announcement:publish", "announcement:reads", "announcement:withdraw"];

    /// <summary>The outsourcing codes the built-in administrator holds after an upgrade.</summary>
    private static readonly string[] OutsourceCodes =
        ["outsource", "outsource:contact_manage", "outsource:contact_settings", "outsource:date_import", "outsource:file_view", "outsource:manage", "outsource:send", "outsource:settings"];

    private static async Task<NotSupportedException> AssertRollbackRefusedAsync(
        SchemaShapeTests.SchemaDatabaseScope database,
        string targetMigration,
        bool useAsync,
        CancellationToken ct)
    {
        if (useAsync)
        {
            await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
            var migrator = context.GetService<IMigrator>();
            return await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(targetMigration, ct));
        }

        await using var connection = await database.Database.OpenAsync(ct);
        await using var contextFromOpenConnection = EfDb.Use(connection);
        var synchronousMigrator = contextFromOpenConnection.GetService<IMigrator>();
        return Assert.Throws<NotSupportedException>(() => synchronousMigrator.Migrate(targetMigration));
    }

    /// <summary>
    /// Rows that a partial rollback would mutate or remove: custom destructive grants, OEM history,
    /// and values introduced by reversible migrations that sit above the irreversible boundary.
    /// </summary>
    private static Task SeedRollbackSentinelsAsync(
        SchemaShapeTests.SchemaDatabaseScope database,
        CancellationToken ct) => database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at)
            VALUES(910001,'迁移回滚哨兵角色',0,'ACTIVE','2026-10-06 01:02:03.000','2026-10-06 01:02:04.000');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 910001,id FROM permissions WHERE code IN ('oem:company_delete','oem:account_delete');

            INSERT INTO oem_companies(id,name,status,created_by,created_at,updated_at)
            VALUES(910001,'迁移回滚哨兵厂商','ACTIVE',1,'2026-10-06 02:00:00.000','2026-10-06 02:01:00.000');
            INSERT INTO oem_accounts(id,employee_no,password_hash,real_name,email,oem_company_id,status,must_change_password,
                failed_login_attempts,created_by,created_at,updated_at)
            VALUES(910001,'rollback-sentinel','$argon2id$sentinel','迁移回滚哨兵账号','rollback-sentinel@example.invalid',
                910001,'ACTIVE',0,0,1,'2026-10-06 03:00:00.000','2026-10-06 03:01:00.000');
            INSERT INTO oem_refresh_tokens(id,account_id,session_id,token_hash,session_expires_at,expires_at,revoked,ip,created_at,revoke_reason)
            VALUES(910001,910001,'rollback-sentinel-session',REPEAT('a',64),'2026-10-07 03:00:00.000',
                '2026-10-07 04:00:00.000',1,'127.0.0.1','2026-10-06 03:02:00.000','ADMIN_REVOKED');

            INSERT INTO oem_transfers(id,direction,oem_company_id,title,internal_sender_user_id,lifecycle_status,retention_template_id,
                created_at,updated_at,concurrency_version)
            VALUES(910001,'INTERNAL_TO_OEM',910001,'迁移回滚哨兵传递',1,'APPROVING',1,
                '2026-10-06 04:00:00.000','2026-10-06 04:01:00.000',7);
            INSERT INTO oem_flow_instances(id,transfer_id,template_id,initiator_user_id,initiator_section_id,status,
                template_snapshot,current_sort_no,concurrency_version,created_at,updated_at)
            VALUES(910001,910001,1,1,1,'IN_PROGRESS','{}',1,5,
                '2026-10-06 05:00:00.000','2026-10-06 05:01:00.000');
            INSERT INTO oem_flow_instance_nodes(id,instance_id,sort_no,name,approver_source,approval_mode,status,used_fallback)
            VALUES(910001,910001,1,'迁移回滚哨兵节点','SECTION_LEADER','SINGLE','PENDING',0);
            INSERT INTO oem_flow_tasks(id,instance_id,instance_node_id,approver_user_id,status,reason,concurrency_version,created_at,activated_at)
            VALUES(910001,910001,910001,1,'PENDING','必须原样保留',3,'2026-10-06 06:00:00.000','2026-10-06 12:34:56.789');
            """, ct);

    private static async Task<string[]> RollbackSentinelDataAsync(
        SchemaShapeTests.SchemaDatabaseScope database,
        CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT CONCAT_WS('|','role',id,name,is_built_in,status,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM roles WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','grant',rp.role_id,p.code) FROM role_permissions rp
            INNER JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=910001
            UNION ALL
            SELECT CONCAT_WS('|','company',id,name,status,created_by,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM oem_companies WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','account',id,employee_no,password_hash,real_name,email,oem_company_id,status,must_change_password,
                failed_login_attempts,created_by,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM oem_accounts WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','refresh',id,account_id,session_id,token_hash,DATE_FORMAT(session_expires_at,'%Y-%m-%d %H:%i:%s.%f'),
                DATE_FORMAT(expires_at,'%Y-%m-%d %H:%i:%s.%f'),revoked,ip,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),revoke_reason)
            FROM oem_refresh_tokens WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','transfer',id,direction,oem_company_id,title,internal_sender_user_id,lifecycle_status,retention_template_id,
                concurrency_version,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM oem_transfers WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','instance',id,transfer_id,template_id,initiator_user_id,initiator_section_id,status,template_snapshot,
                current_sort_no,concurrency_version,DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM oem_flow_instances WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','node',id,instance_id,sort_no,name,approver_source,approval_mode,status,used_fallback)
            FROM oem_flow_instance_nodes WHERE id=910001
            UNION ALL
            SELECT CONCAT_WS('|','task',id,instance_id,instance_node_id,approver_user_id,status,reason,concurrency_version,
                DATE_FORMAT(created_at,'%Y-%m-%d %H:%i:%s.%f'),DATE_FORMAT(activated_at,'%Y-%m-%d %H:%i:%s.%f'))
            FROM oem_flow_tasks WHERE id=910001
            """, cancellationToken: ct));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

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
            VALUES(1,'系统管理员',1,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
                  (4,'菜单角色',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 1,id FROM permissions;
            -- 只有用户菜单、没有 user:manage 的角色：旧读接口本来就拒绝它，回填不得给它 user:view。
            INSERT INTO role_permissions(role_id,permission_id) VALUES(4,4);
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
    private static async Task<(int Suppliers, int Parts, int RobotTypes)> CatalogCountsAsync(
        SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"),
            await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"),
            await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_dictionaries WHERE type='ROBOT_TYPE'"));
    }

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

    [Fact(Timeout = 180_000)]
    public async Task DueDateCorrectionTakesTheIpmsCustomerDateAndKeepsDatesPurchasingChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("due_date_fix", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261120071443_AddOutsourceVendorEmails", ct);
        await database.ExecuteAsync("""
            INSERT INTO oem_companies(id,name,status,created_at,updated_at) VALUES (8801,'交期厂',  'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,raw_json) VALUES
              ('W1','b',UTC_TIMESTAMP(3),'{"deliveryDate":"2026-09-30","customerEndDate":"2026-11-20"}'),
              ('W2','b',UTC_TIMESTAMP(3),'{"deliveryDate":"2026-09-30","customerEndDate":"2026-12-05 00:00:00"}'),
              ('W3','b',UTC_TIMESTAMP(3),'{"deliveryDate":"2026-09-30","customerEndDate":null}'),
              ('W4','b',UTC_TIMESTAMP(3),'{"deliveryDate":"2026-09-30","customerEndDate":"garbage"}');
            INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,original_due_date,latest_due_date,status,send_status,created_at,updated_at) VALUES
              ('W1',1,'2026-11-01',8801,'2026-09-30','2026-09-30','NORMAL','UNSENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('W2',2,'2026-11-01',8801,'2026-09-30','2026-10-15','NORMAL','SENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('W3',3,'2026-11-01',8801,'2026-09-30','2026-09-30','NORMAL','UNSENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('W4',4,'2026-11-01',8801,'2026-09-30','2026-09-30','NORMAL','UNSENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('W5',5,'2026-11-01',8801,'2026-09-30','2026-09-30','NORMAL','UNSENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            """, ct);

        await migrator.MigrateAsync(Latest, ct);

        await using var conn = await database.Database.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string No, DateTime Original, DateTime Latest, string Send)>(
            "SELECT work_order_no, original_due_date, latest_due_date, send_status FROM outsource_items ORDER BY work_order_no"))
            .ToDictionary(x => x.No);
        // W1 was never changed by purchasing: both dates move. W2's latest date was changed by hand and stays.
        // No usable customer date (null, garbage, not in the mirror) leaves the item alone. Send status never changes.
        Assert.Equal((new DateTime(2026, 11, 20), new DateTime(2026, 11, 20)), (rows["W1"].Original, rows["W1"].Latest));
        Assert.Equal((new DateTime(2026, 12, 5), new DateTime(2026, 10, 15), "SENT"), (rows["W2"].Original, rows["W2"].Latest, rows["W2"].Send));
        foreach (var no in new[] { "W3", "W4", "W5" })
            Assert.Equal((new DateTime(2026, 9, 30), new DateTime(2026, 9, 30)), (rows[no].Original, rows[no].Latest));
    }

    [Fact(Timeout = 180_000)]
    public async Task AddOutsourceContactsCreatesSchemaAndPermissionsAndRollsBackCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_contacts_rt", ct);
        await database.InitializeWithMigrationsAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeOutsourceContacts, ct);
        var beforeSchema = await SchemaAsync(database, ct);

        await migrator.MigrateAsync(Latest, ct);
        var afterSchema = await SchemaAsync(database, ct);
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_columns|", StringComparison.Ordinal));
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_people|", StringComparison.Ordinal));
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_rows|", StringComparison.Ordinal));
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_row_vendors|", StringComparison.Ordinal));
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_row_values|", StringComparison.Ordinal));
        Assert.Contains(afterSchema, line => line.StartsWith("table|outsource_contact_row_people|", StringComparison.Ordinal));

        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_contact_columns"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_contact_rows"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_contact_people"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM permissions p JOIN permissions parent ON parent.id=p.parent_id
                WHERE parent.code='outsource' AND p.code IN ('outsource:contact_manage','outsource:contact_settings')
                """));
        }

        await migrator.MigrateAsync(BeforeOutsourceContacts, ct);
        Assert.Equal(beforeSchema, await SchemaAsync(database, ct));

        await migrator.MigrateAsync(Latest, ct);
        Assert.Equal(afterSchema, await SchemaAsync(database, ct));
    }

    private static async Task<string> LastMigrationAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct) =>
        (await HistoryAsync(database, ct)).Last();
}
