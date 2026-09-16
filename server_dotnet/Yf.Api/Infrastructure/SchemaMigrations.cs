using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Infrastructure;

/// <summary>Explicit, restartable .NET-owned schema upgrades. Startup never changes schema.</summary>
public static class SchemaMigrations
{
    public const int CurrentVersion = 9;
    private const string PreviousBaseline = "m20260910_000016_project_workflow";
    private const string FirstMigrationName = "000001_adopt_schema_sessions_supplier_boundary";
    private const string CollaborationMigrationName = "000002_collaboration_notification_reads";
    private const string InternalAcceptanceMigrationName = "000003_internal_project_acceptance";
    private const string AcceptanceNotificationMigrationName = "000004_versioned_acceptance_notifications";
    private const string MessageImagesMigrationName = "000005_message_images";
    private const string ProjectMetadataMigrationName = "000006_project_metadata_dictionaries";
    private const string ProjectCopyMigrationName = "000007_project_copy_history";
    private const string ProjectDictionaryCodeRemovalMigrationName = "000008_remove_project_dictionary_code";
    private const string ProjectGroupsMigrationName = "000009_project_groups";
    internal const string InternalAcceptanceCancelledMailReason = "项目验收已调整为公司内部确认，旧供应商确认通知已取消";
    internal const string AcceptanceNotificationRebuiltMailReason = "验收通知已按当前待验收申请和验收人重新生成";
    private const string MigrationTableSql = """
        CREATE TABLE `yf_schema_migrations` (
          `version` int NOT NULL,
          `name` varchar(128) NOT NULL,
          `checksum` char(64) NOT NULL,
          `applied_at` datetime(6) NOT NULL,
          PRIMARY KEY (`version`)
        ) ENGINE=InnoDB
        """;
    internal const string CollaborationReadsTableSql = """
        CREATE TABLE `collaboration_reads` (
          `activity_id` bigint unsigned NOT NULL,
          `user_id` bigint unsigned NOT NULL,
          `read_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`activity_id`,`user_id`),
          KEY `idx_collaboration_reads_user_activity` (`user_id`,`activity_id`),
          CONSTRAINT `fk_collaboration_reads_activity` FOREIGN KEY (`activity_id`) REFERENCES `project_activities` (`id`) ON DELETE CASCADE,
          CONSTRAINT `fk_collaboration_reads_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string MessageImagesTableSql = """
        CREATE TABLE `message_images` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `message_id` bigint unsigned NOT NULL,
          `original_name` varchar(255) NOT NULL,
          `stored_name` varchar(64) NOT NULL,
          `ext` varchar(8) NOT NULL,
          `size_bytes` bigint unsigned NOT NULL,
          `mime_type` varchar(32) NOT NULL,
          `storage_path` varchar(512) NOT NULL,
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_message_images_stored_name` (`stored_name`),
          KEY `idx_message_images_message` (`message_id`,`id`),
          CONSTRAINT `fk_message_images_message` FOREIGN KEY (`message_id`) REFERENCES `messages` (`id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectDictionariesTableSql = """
        CREATE TABLE `project_dictionaries` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `type` varchar(32) NOT NULL,
          `name` varchar(128) NOT NULL,
          `parent_id` bigint unsigned DEFAULT NULL,
          `sort_no` int NOT NULL DEFAULT 0,
          `status` varchar(16) NOT NULL DEFAULT 'ACTIVE',
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          `updated_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_dictionaries_type_name` (`type`,`name`),
          KEY `idx_project_dictionaries_type_status_sort` (`type`,`status`,`sort_no`,`id`),
          KEY `idx_project_dictionaries_parent` (`parent_id`),
          CONSTRAINT `fk_project_dictionaries_parent` FOREIGN KEY (`parent_id`) REFERENCES `project_dictionaries` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    private const string LegacyProjectDictionariesTableSql = """
        CREATE TABLE `project_dictionaries` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `type` varchar(32) NOT NULL,
          `code` varchar(64) NOT NULL,
          `name` varchar(128) NOT NULL,
          `parent_id` bigint unsigned DEFAULT NULL,
          `sort_no` int NOT NULL DEFAULT 0,
          `status` varchar(16) NOT NULL DEFAULT 'ACTIVE',
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          `updated_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_dictionaries_type_code` (`type`,`code`),
          KEY `idx_project_dictionaries_type_status_sort` (`type`,`status`,`sort_no`,`id`),
          KEY `idx_project_dictionaries_parent` (`parent_id`),
          CONSTRAINT `fk_project_dictionaries_parent` FOREIGN KEY (`parent_id`) REFERENCES `project_dictionaries` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectWorkOrdersTableSql = """
        CREATE TABLE `project_work_orders` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `project_id` bigint unsigned NOT NULL,
          `work_order_no` varchar(128) NOT NULL,
          `sort_no` int NOT NULL DEFAULT 0,
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_work_orders_project_no` (`project_id`,`work_order_no`),
          KEY `idx_project_work_orders_project_sort` (`project_id`,`sort_no`,`id`),
          CONSTRAINT `fk_project_work_orders_project` FOREIGN KEY (`project_id`) REFERENCES `projects` (`id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectCopiesTableSql = """
        CREATE TABLE `project_copies` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `source_project_id` bigint unsigned NOT NULL,
          `target_project_id` bigint unsigned NOT NULL,
          `source_project_name` varchar(128) NOT NULL,
          `target_project_name` varchar(128) NOT NULL,
          `copied_by` bigint unsigned NOT NULL,
          `copied_by_name` varchar(128) NOT NULL,
          `file_count` bigint unsigned NOT NULL,
          `total_bytes` bigint unsigned NOT NULL,
          `created_at` datetime(6) NOT NULL,
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_copies_target` (`target_project_id`),
          KEY `idx_project_copies_source_time` (`source_project_id`,`created_at`,`id`),
          KEY `idx_project_copies_copied_by` (`copied_by`),
          CONSTRAINT `fk_project_copies_source` FOREIGN KEY (`source_project_id`) REFERENCES `projects` (`id`),
          CONSTRAINT `fk_project_copies_target` FOREIGN KEY (`target_project_id`) REFERENCES `projects` (`id`),
          CONSTRAINT `fk_project_copies_copied_by` FOREIGN KEY (`copied_by`) REFERENCES `users` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string FileCopyRefsTableSql = """
        CREATE TABLE `file_copy_refs` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `copy_id` bigint unsigned NOT NULL,
          `source_file_id` bigint unsigned NOT NULL,
          `target_file_id` bigint unsigned NOT NULL,
          `source_file_name` varchar(255) NOT NULL,
          `target_file_name` varchar(255) NOT NULL,
          `created_at` datetime(6) NOT NULL,
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_file_copy_refs_target` (`target_file_id`),
          KEY `idx_file_copy_refs_copy` (`copy_id`,`id`),
          KEY `idx_file_copy_refs_source` (`source_file_id`),
          CONSTRAINT `fk_file_copy_refs_copy` FOREIGN KEY (`copy_id`) REFERENCES `project_copies` (`id`) ON DELETE CASCADE,
          CONSTRAINT `fk_file_copy_refs_source` FOREIGN KEY (`source_file_id`) REFERENCES `files` (`id`),
          CONSTRAINT `fk_file_copy_refs_target` FOREIGN KEY (`target_file_id`) REFERENCES `files` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectGroupsTableSql = """
        CREATE TABLE `project_groups` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `name` varchar(128) NOT NULL,
          `description` varchar(1024) DEFAULT NULL,
          `supplier_id` bigint unsigned NOT NULL,
          `status` varchar(24) NOT NULL DEFAULT 'DRAFT',
          `created_by` bigint unsigned NOT NULL,
          `machine_model` varchar(128) DEFAULT NULL,
          `robot_vendor_id` bigint unsigned DEFAULT NULL,
          `robot_model_id` bigint unsigned DEFAULT NULL,
          `responsible_user_id` bigint unsigned DEFAULT NULL,
          `section_id` bigint unsigned DEFAULT NULL,
          `priority_id` bigint unsigned DEFAULT NULL,
          `expected_completion_date` date DEFAULT NULL,
          `completed_at` datetime(3) DEFAULT NULL,
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          `updated_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_groups_name` (`name`),
          KEY `idx_project_groups_supplier` (`supplier_id`),
          KEY `idx_project_groups_status` (`status`),
          KEY `idx_project_groups_responsible_user` (`responsible_user_id`),
          KEY `idx_project_groups_expected_completion` (`expected_completion_date`),
          CONSTRAINT `fk_project_groups_supplier` FOREIGN KEY (`supplier_id`) REFERENCES `suppliers` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectGroupWorkOrdersTableSql = """
        CREATE TABLE `project_group_work_orders` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `project_group_id` bigint unsigned NOT NULL,
          `work_order_no` varchar(128) NOT NULL,
          `sort_no` int NOT NULL DEFAULT 0,
          `created_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uk_project_group_work_orders_group_no` (`project_group_id`,`work_order_no`),
          KEY `idx_project_group_work_orders_group_sort` (`project_group_id`,`sort_no`,`id`),
          CONSTRAINT `fk_project_group_work_orders_group` FOREIGN KEY (`project_group_id`) REFERENCES `project_groups` (`id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    internal const string ProjectGroupStatusLogsTableSql = """
        CREATE TABLE `project_group_status_logs` (
          `id` bigint unsigned NOT NULL AUTO_INCREMENT,
          `project_group_id` bigint unsigned NOT NULL,
          `from_status` varchar(24) DEFAULT NULL,
          `to_status` varchar(24) NOT NULL,
          `action` varchar(32) NOT NULL,
          `trigger_project_id` bigint unsigned DEFAULT NULL,
          `operator_id` bigint unsigned NOT NULL,
          `created_at` datetime(3) NOT NULL,
          PRIMARY KEY (`id`),
          KEY `idx_project_group_status_logs_group_time` (`project_group_id`,`created_at`,`id`),
          KEY `idx_project_group_status_logs_project` (`trigger_project_id`),
          KEY `idx_project_group_status_logs_operator` (`operator_id`),
          CONSTRAINT `fk_project_group_status_logs_group` FOREIGN KEY (`project_group_id`) REFERENCES `project_groups` (`id`) ON DELETE CASCADE,
          CONSTRAINT `fk_project_group_status_logs_project` FOREIGN KEY (`trigger_project_id`) REFERENCES `projects` (`id`) ON DELETE SET NULL,
          CONSTRAINT `fk_project_group_status_logs_operator` FOREIGN KEY (`operator_id`) REFERENCES `users` (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;
    private static string FirstChecksum => Checksum(FirstMigrationName);
    private static string CollaborationChecksum => Checksum(CollaborationMigrationName);
    private static string InternalAcceptanceChecksum => Checksum(InternalAcceptanceMigrationName);
    private static string AcceptanceNotificationChecksum => Checksum(AcceptanceNotificationMigrationName);
    private static string MessageImagesChecksum => Checksum(MessageImagesMigrationName);
    private static string ProjectMetadataChecksum => Checksum(ProjectMetadataMigrationName);
    private static string ProjectCopyChecksum => Checksum(ProjectCopyMigrationName);
    private static string ProjectDictionaryCodeRemovalChecksum => Checksum(ProjectDictionaryCodeRemovalMigrationName);
    private static string ProjectGroupsChecksum => Checksum(ProjectGroupsMigrationName);

    public static async Task ApplyAsync(AppDb db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var database = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT DATABASE()", cancellationToken: ct));
        await using var migrationLock = await MySqlNamedLock.TryAcquireAsync(
            conn, MySqlNamedLock.Name("schema", database!), 10, ct)
            ?? throw new InvalidOperationException("Another database migration is running. Retry after it completes.");
        var legacy = await HasTableAsync(conn, "seaql_migrations", ct)
            ? await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT version FROM seaql_migrations ORDER BY version DESC LIMIT 1", cancellationToken: ct)) : null;
        if (legacy is not (PreviousBaseline or SchemaBootstrap.Version))
            throw new InvalidOperationException("Migration refused: supported import baselines are project_workflow (16) and auth_session_families (17). Older or unknown schemas need a separately reviewed data conversion; no changes were made.");

        // Validate all existing structures and histories before the first DDL or data write.
        // Baseline 16 may only be missing the session-family additions repaired below.
        await SchemaShapeValidator.ValidateBaselineAsync(conn,
            legacy == PreviousBaseline
                ? SchemaShapeValidationMode.LegacyV16BeforeSessionMigration
                : SchemaShapeValidationMode.Strict,
            ct);
        await ValidatePermissionGateAsync(conn, ct);
        var hasMigrationTable = await HasTableAsync(conn, "yf_schema_migrations", ct);
        var hasCollaborationReads = await HasTableAsync(conn, "collaboration_reads", ct);
        var hasMessageImages = await HasTableAsync(conn, "message_images", ct);
        var hasProjectDictionaries = await HasTableAsync(conn, "project_dictionaries", ct);
        var hasProjectWorkOrders = await HasTableAsync(conn, "project_work_orders", ct);
        var hasProjectCopies = await HasTableAsync(conn, "project_copies", ct);
        var hasFileCopyRefs = await HasTableAsync(conn, "file_copy_refs", ct);
        var hasProjectGroups = await HasTableAsync(conn, "project_groups", ct);
        var hasProjectGroupWorkOrders = await HasTableAsync(conn, "project_group_work_orders", ct);
        var hasProjectGroupStatusLogs = await HasTableAsync(conn, "project_group_status_logs", ct);
        MigrationRow[] migrationRows;
        if (hasMigrationTable)
        {
            await SchemaShapeValidator.ValidateTableAsync(conn, "yf_schema_migrations", MigrationTableSql, ct);
            migrationRows = await ReadMigrationRowsAsync(conn, ct);
            ValidateMigrationRows(migrationRows, requireCurrent: false);
        }
        else
        {
            migrationRows = [];
        }
        if (hasCollaborationReads)
            await SchemaShapeValidator.ValidateTableAsync(conn, "collaboration_reads", CollaborationReadsTableSql, ct);
        if (hasMessageImages)
            await SchemaShapeValidator.ValidateTableAsync(conn, "message_images", MessageImagesTableSql, ct);
        if (hasProjectDictionaries)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_dictionaries",
                migrationRows.Any(row => row.Version >= 8) || !await HasColumnAsync(conn, "project_dictionaries", "code", ct)
                    ? ProjectDictionariesTableSql
                    : LegacyProjectDictionariesTableSql, ct);
        if (hasProjectWorkOrders)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_work_orders", ProjectWorkOrdersTableSql, ct);
        if (hasProjectCopies)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_copies", ProjectCopiesTableSql, ct);
        if (hasFileCopyRefs)
            await SchemaShapeValidator.ValidateTableAsync(conn, "file_copy_refs", FileCopyRefsTableSql, ct);
        if (hasProjectGroups)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_groups", ProjectGroupsTableSql, ct);
        if (hasProjectGroupWorkOrders)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_group_work_orders", ProjectGroupWorkOrdersTableSql, ct);
        if (hasProjectGroupStatusLogs)
            await SchemaShapeValidator.ValidateTableAsync(conn, "project_group_status_logs", ProjectGroupStatusLogsTableSql, ct);
        if (migrationRows.Any(row => row.Version >= 2) && !hasCollaborationReads)
            throw new InvalidOperationException("Migration history says collaboration reads are applied, but the required table is missing. Restore the matching schema before retrying.");
        if (migrationRows.Any(row => row.Version >= 5) && !hasMessageImages)
            throw new InvalidOperationException("Migration history says message images are applied, but the required table is missing. Restore the matching schema before retrying.");
        if (migrationRows.Any(row => row.Version >= 6) && (!hasProjectDictionaries || !hasProjectWorkOrders))
            throw new InvalidOperationException("Migration history says project metadata is applied, but a required table is missing. Restore the matching schema before retrying.");
        if (migrationRows.Any(row => row.Version >= 7) && (!hasProjectCopies || !hasFileCopyRefs))
            throw new InvalidOperationException("Migration history says project copy history is applied, but a required table is missing. Restore the matching schema before retrying.");
        if (migrationRows.Any(row => row.Version >= 9)
            && (!hasProjectGroups || !hasProjectGroupWorkOrders || !hasProjectGroupStatusLogs
                || !await HasColumnAsync(conn, "projects", "project_group_id", ct)))
            throw new InvalidOperationException("Migration history says project groups are applied, but a required table or column is missing. Restore the matching schema before retrying.");
        if (!hasMigrationTable)
            await conn.ExecuteAsync(new CommandDefinition(MigrationTableSql, cancellationToken: ct));

        // MySQL DDL commits implicitly. Every step is repeatable after a crash,
        // and history is recorded only after the resulting schema is validated.
        if (!await HasColumnAsync(conn, "refresh_tokens", "session_id", ct))
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE refresh_tokens ADD COLUMN session_id VARCHAR(36) NULL AFTER user_id", cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("UPDATE refresh_tokens SET session_id=LPAD(LOWER(HEX(id)),36,'0') WHERE session_id IS NULL OR session_id=''", cancellationToken: ct));
        var nullable = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT IS_NULLABLE FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='refresh_tokens' AND column_name='session_id'", cancellationToken: ct));
        if (nullable == "YES")
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE refresh_tokens MODIFY session_id VARCHAR(36) NOT NULL", cancellationToken: ct));
        var index = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='refresh_tokens' AND index_name='idx_refresh_tokens_session_state'", cancellationToken: ct));
        if (index == 0)
            await conn.ExecuteAsync(new CommandDefinition("CREATE INDEX idx_refresh_tokens_session_state ON refresh_tokens(session_id,user_id,revoked,expires_at)", cancellationToken: ct));
        await SchemaShapeValidator.ValidateBaselineAsync(conn, SchemaShapeValidationMode.Strict, ct);
        await ValidatePermissionGateAsync(conn, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "yf_schema_migrations", MigrationTableSql, ct);
        if (!hasCollaborationReads)
            await conn.ExecuteAsync(new CommandDefinition(CollaborationReadsTableSql, cancellationToken: ct));
        await SchemaShapeValidator.ValidateTableAsync(conn, "collaboration_reads", CollaborationReadsTableSql, ct);
        if (!hasMessageImages)
            await conn.ExecuteAsync(new CommandDefinition(MessageImagesTableSql, cancellationToken: ct));
        await SchemaShapeValidator.ValidateTableAsync(conn, "message_images", MessageImagesTableSql, ct);
        if (!hasProjectDictionaries)
            await conn.ExecuteAsync(new CommandDefinition(ProjectDictionariesTableSql, cancellationToken: ct));
        else if (!migrationRows.Any(row => row.Version >= 8))
            await MigrateProjectDictionaryCodeRemovalAsync(conn, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_dictionaries", ProjectDictionariesTableSql, ct);
        if (!hasProjectWorkOrders)
            await conn.ExecuteAsync(new CommandDefinition(ProjectWorkOrdersTableSql, cancellationToken: ct));
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_work_orders", ProjectWorkOrdersTableSql, ct);
        if (!hasProjectCopies)
            await conn.ExecuteAsync(new CommandDefinition(ProjectCopiesTableSql, cancellationToken: ct));
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_copies", ProjectCopiesTableSql, ct);
        if (!hasFileCopyRefs)
            await conn.ExecuteAsync(new CommandDefinition(FileCopyRefsTableSql, cancellationToken: ct));
        await SchemaShapeValidator.ValidateTableAsync(conn, "file_copy_refs", FileCopyRefsTableSql, ct);
        await EnsureProjectMetadataColumnsAsync(conn, ct);
        await ValidateProjectMetadataColumnsAsync(conn, ct);
        await EnsureProjectGroupsAsync(conn, ct);
        await ValidateProjectGroupsAsync(conn, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var supplierGrants = await conn.QueryAsync<SupplierGrant>(new CommandDefinition(
            "SELECT rp.role_id RoleId,rp.permission_id PermissionId,p.code Code FROM role_permissions rp JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id WHERE r.is_built_in=1 AND r.name='供应商人员'", transaction: tx, cancellationToken: ct));
        foreach (var grant in supplierGrants.Where(x => !RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员", [x.Code])))
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM role_permissions WHERE role_id=@RoleId AND permission_id=@PermissionId", grant, tx, cancellationToken: ct));
            await new AuditService([]).WriteAsync(conn, tx, null, "ROLE_ASSIGN_PERMS", "role", grant.RoleId,
                new { migration = InternalAcceptanceMigrationName, removedPermission = grant.Code, reason = "internal acceptance boundary" }, null, ct);
        }
        if (legacy == PreviousBaseline)
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO seaql_migrations(version,applied_at) VALUES (@version,UNIX_TIMESTAMP())", new { version = SchemaBootstrap.Version }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (1,@name,@checksum,UTC_TIMESTAMP(6))", new { name = FirstMigrationName, checksum = FirstChecksum }, tx, cancellationToken: ct));
        // No receipt backfill is intentional: after v2, existing meaningful visible
        // activities are initially unread for each user, with no arbitrary cutoff.
        await conn.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (2,@name,@checksum,UTC_TIMESTAMP(6))", new { name = CollaborationMigrationName, checksum = CollaborationChecksum }, tx, cancellationToken: ct));
        var supplierConfirmationProjects = (await conn.QueryAsync<PendingAcceptanceMigration>(new CommandDefinition(
            """
            SELECT p.id AS ProjectId,
                   (SELECT MAX(psl.id) FROM project_status_logs psl
                    WHERE psl.project_id=p.id AND psl.action='SUBMIT') AS LatestSubmissionId
            FROM projects p
            WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='SUPPLIER'
            FOR UPDATE
            """,
            transaction: tx,
            cancellationToken: ct))).ToArray();
        if (supplierConfirmationProjects.Any(project => project.LatestSubmissionId is null))
        {
            throw new InvalidOperationException("待迁移的供应商确认项目缺少提交记录；未进行任何验收数据迁移。");
        }
        if (supplierConfirmationProjects.Length > 0)
        {
            var projectIds = supplierConfirmationProjects.Select(project => project.ProjectId).ToArray();
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE project_status_logs psl
                INNER JOIN (
                    SELECT project_id,MAX(id) AS id
                    FROM project_status_logs
                    WHERE project_id IN @ProjectIds AND action='SUBMIT'
                    GROUP BY project_id
                ) latest ON latest.id=psl.id
                SET psl.confirm_side='COMPANY'
                """,
                new { ProjectIds = projectIds }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET confirm_side='COMPANY' WHERE id IN @ProjectIds AND status='PENDING_CONFIRMATION' AND confirm_side='SUPPLIER'",
                new { ProjectIds = projectIds }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE email_outbox
                SET status='CANCELLED',next_attempt_at=NULL,last_error=@Reason
                WHERE project_id IN @ProjectIds AND event_type='PROJECT_SUBMITTED'
                  AND sent_at IS NULL AND status IN ('PENDING','SENDING','FAILED')
                """,
                new { ProjectIds = projectIds, Reason = InternalAcceptanceCancelledMailReason }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE email_outbox eo
            INNER JOIN projects p ON p.id=eo.project_id
            INNER JOIN users recipient ON recipient.id=eo.recipient_user_id
            SET eo.status='CANCELLED',eo.next_attempt_at=NULL,eo.last_error=@Reason
            WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='COMPANY'
              AND eo.event_type='PROJECT_SUBMITTED'
              AND eo.sent_at IS NULL
              AND eo.status IN ('PENDING','SENDING','FAILED')
              AND recipient.user_type='SUPPLIER'
            """,
            new { Reason = InternalAcceptanceCancelledMailReason }, tx, cancellationToken: ct));
        foreach (var project in supplierConfirmationProjects)
        {
            var cancelledLegacyNotices = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM email_outbox
                WHERE project_id=@ProjectId AND event_type='PROJECT_SUBMITTED'
                  AND sent_at IS NULL AND status='CANCELLED' AND last_error=@Reason
                """,
                new { project.ProjectId, Reason = InternalAcceptanceCancelledMailReason }, tx, cancellationToken: ct));
            await new AuditService([]).WriteAsync(conn, tx, null, "PROJECT_ACCEPTANCE_MIGRATE", "project", project.ProjectId,
                new
                {
                    migration = InternalAcceptanceMigrationName,
                    fromConfirmSide = "SUPPLIER",
                    toConfirmSide = "COMPANY",
                    statusLogId = project.LatestSubmissionId,
                    cancelledLegacyNotices,
                }, null, ct);
        }
        await ValidateInternalAcceptanceBoundaryAsync(conn, tx, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (3,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = InternalAcceptanceMigrationName, checksum = InternalAcceptanceChecksum }, tx, cancellationToken: ct));
        if (!migrationRows.Any(row => row.Version == 4))
        {
            var cancelledSubmissionNotices = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE email_outbox
                SET status='CANCELLED',next_attempt_at=NULL,last_error=@Reason
                WHERE event_type='PROJECT_SUBMITTED' AND sent_at IS NULL
                  AND status IN ('PENDING','SENDING','FAILED')
                """,
                new { Reason = AcceptanceNotificationRebuiltMailReason }, tx, cancellationToken: ct));

            var pendingAcceptances = (await conn.QueryAsync<PendingAcceptanceNotificationMigration>(new CommandDefinition(
                """
                SELECT p.id AS ProjectId,p.name AS ProjectName,p.supplier_id AS SupplierId,
                       p.status AS ProjectStatus,p.confirm_side AS ConfirmSide,p.created_by AS CreatedBy,
                       p.created_at AS ProjectCreatedAt,p.updated_at AS ProjectUpdatedAt,
                       latest.id AS LatestSubmissionId,latest.operator_id AS SubmitterId,
                       submitter.employee_no AS SubmitterEmployeeNo,submitter.user_type AS SubmitterUserType,
                       submitter.supplier_id AS SubmitterSupplierId
                FROM projects p
                INNER JOIN project_status_logs latest ON latest.id=(
                    SELECT MAX(psl.id) FROM project_status_logs psl
                    WHERE psl.project_id=p.id AND psl.action='SUBMIT'
                )
                INNER JOIN users submitter ON submitter.id=latest.operator_id
                WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='COMPANY'
                ORDER BY p.id
                FOR UPDATE
                """,
                transaction: tx,
                cancellationToken: ct))).ToArray();
            var migrationAudit = new AuditService([]);
            foreach (var pending in pendingAcceptances)
            {
                var project = new ProjectRow
                {
                    Id = pending.ProjectId,
                    Name = pending.ProjectName,
                    SupplierId = pending.SupplierId,
                    Status = pending.ProjectStatus,
                    ConfirmSide = pending.ConfirmSide,
                    CreatedBy = pending.CreatedBy,
                    CreatedAt = pending.ProjectCreatedAt,
                    UpdatedAt = pending.ProjectUpdatedAt,
                };
                var submitter = new CurrentUser(
                    pending.SubmitterId,
                    pending.SubmitterEmployeeNo,
                    pending.SubmitterUserType,
                    pending.SubmitterSupplierId);
                await ProjectNotificationService.EnqueuePendingAcceptanceAsync(
                    conn,
                    tx,
                    project,
                    pending.LatestSubmissionId,
                    submitter,
                    db.WebBaseUrl,
                    migrationAudit,
                    ct);
            }
            if (cancelledSubmissionNotices > 0 || pendingAcceptances.Length > 0)
            {
                await migrationAudit.WriteAsync(conn, tx, null, "PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE", "schema", 4UL,
                    new
                    {
                        migration = AcceptanceNotificationMigrationName,
                        cancelledSubmissionNotices,
                        pendingProjectCount = pendingAcceptances.Length,
                    }, null, ct);
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (4,@name,@checksum,UTC_TIMESTAMP(6))",
                new { name = AcceptanceNotificationMigrationName, checksum = AcceptanceNotificationChecksum }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (5,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = MessageImagesMigrationName, checksum = MessageImagesChecksum }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO project_dictionaries(type,name,parent_id,sort_no,status,created_at,updated_at) VALUES
              ('PRIORITY','高',NULL,10,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('PRIORITY','普通',NULL,20,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              ('PRIORITY','低',NULL,30,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (6,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = ProjectMetadataMigrationName, checksum = ProjectMetadataChecksum }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (7,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = ProjectCopyMigrationName, checksum = ProjectCopyChecksum }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (8,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = ProjectDictionaryCodeRemovalMigrationName, checksum = ProjectDictionaryCodeRemovalChecksum }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (9,@name,@checksum,UTC_TIMESTAMP(6))",
            new { name = ProjectGroupsMigrationName, checksum = ProjectGroupsChecksum }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        ValidateMigrationRows(await ReadMigrationRowsAsync(conn, ct), requireCurrent: true);
        Console.WriteLine("Database is at .NET schema version " + CurrentVersion + ". Project groups are available; existing project content was preserved.");
    }

    public static async Task ValidateAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasTableAsync(conn, "yf_schema_migrations", ct))
            throw new InvalidOperationException("Database has not been adopted by .NET. Back up the database and run --migrate-database explicitly before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "yf_schema_migrations", MigrationTableSql, ct);
        ValidateMigrationRows(await ReadMigrationRowsAsync(conn, ct), requireCurrent: true);
        if (!await HasTableAsync(conn, "collaboration_reads", ct))
            throw new InvalidOperationException("Database schema version 5 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "collaboration_reads", CollaborationReadsTableSql, ct);
        if (!await HasTableAsync(conn, "message_images", ct))
            throw new InvalidOperationException("Database schema version 6 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "message_images", MessageImagesTableSql, ct);
        if (!await HasTableAsync(conn, "project_dictionaries", ct) || !await HasTableAsync(conn, "project_work_orders", ct))
            throw new InvalidOperationException("Database schema version 6 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_dictionaries", ProjectDictionariesTableSql, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_work_orders", ProjectWorkOrdersTableSql, ct);
        if (!await HasTableAsync(conn, "project_copies", ct) || !await HasTableAsync(conn, "file_copy_refs", ct))
            throw new InvalidOperationException("Database schema version 7 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_copies", ProjectCopiesTableSql, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "file_copy_refs", FileCopyRefsTableSql, ct);
        await ValidateProjectMetadataColumnsAsync(conn, ct);
        await ValidateProjectGroupsAsync(conn, ct);
        await SchemaShapeValidator.ValidateBaselineAsync(conn, SchemaShapeValidationMode.Strict, ct);
        await ValidatePermissionGateAsync(conn, ct);
        await ValidateInternalAcceptanceBoundaryAsync(conn, null, ct);
    }

    private static async Task ValidatePermissionGateAsync(MySqlConnection conn, CancellationToken ct)
    {
        var gate = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM system_configs WHERE cfg_key='security.management_lock'", cancellationToken: ct));
        if (gate != 1) throw new InvalidOperationException("Database permission gate missing.");
    }

    private static async Task ValidateInternalAcceptanceBoundaryAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        CancellationToken ct)
    {
        var invalidPendingProjects = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM projects p
            WHERE p.status='PENDING_CONFIRMATION'
              AND (
                    p.confirm_side IS NULL OR p.confirm_side<>'COMPANY'
                    OR NOT EXISTS(
                        SELECT 1
                        FROM project_status_logs latest
                        WHERE latest.id=(
                            SELECT MAX(psl.id)
                            FROM project_status_logs psl
                            WHERE psl.project_id=p.id AND psl.action='SUBMIT'
                        ) AND latest.confirm_side='COMPANY'
                    )
              )
            """,
            transaction: tx,
            cancellationToken: ct));
        if (invalidPendingProjects > 0)
            throw new InvalidOperationException("Database contains pending projects outside the internal acceptance boundary. Run the matching application migration command.");

        var supplierConfirmationGrants = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM role_permissions rp
            INNER JOIN roles r ON r.id=rp.role_id
            INNER JOIN permissions p ON p.id=rp.permission_id
            WHERE r.is_built_in=1 AND r.name='供应商人员' AND p.code='project:confirm'
            """,
            transaction: tx,
            cancellationToken: ct));
        if (supplierConfirmationGrants > 0)
            throw new InvalidOperationException("The built-in supplier role still grants project confirmation. Run the matching application migration command.");

        var activeSupplierNotices = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM email_outbox eo
            INNER JOIN projects p ON p.id=eo.project_id
            INNER JOIN users recipient ON recipient.id=eo.recipient_user_id
            WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='COMPANY'
              AND eo.event_type='PROJECT_SUBMITTED' AND eo.sent_at IS NULL
              AND eo.status IN ('PENDING','SENDING') AND recipient.user_type='SUPPLIER'
            """,
            transaction: tx,
            cancellationToken: ct));
        if (activeSupplierNotices > 0)
            throw new InvalidOperationException("Database contains unsent supplier confirmation notices for internal acceptance. Run the matching application migration command.");
    }

    private static async Task<MigrationRow[]> ReadMigrationRowsAsync(MySqlConnection conn, CancellationToken ct) =>
        (await conn.QueryAsync<MigrationRow>(new CommandDefinition(
            "SELECT version,name,checksum FROM yf_schema_migrations ORDER BY version",
            cancellationToken: ct))).ToArray();

    private static void ValidateMigrationRows(MigrationRow[] rows, bool requireCurrent)
    {
        var expected = new[]
        {
            new MigrationRow(1, FirstMigrationName, FirstChecksum),
            new MigrationRow(2, CollaborationMigrationName, CollaborationChecksum),
            new MigrationRow(3, InternalAcceptanceMigrationName, InternalAcceptanceChecksum),
            new MigrationRow(4, AcceptanceNotificationMigrationName, AcceptanceNotificationChecksum),
            new MigrationRow(5, MessageImagesMigrationName, MessageImagesChecksum),
            new MigrationRow(6, ProjectMetadataMigrationName, ProjectMetadataChecksum),
            new MigrationRow(7, ProjectCopyMigrationName, ProjectCopyChecksum),
            new MigrationRow(8, ProjectDictionaryCodeRemovalMigrationName, ProjectDictionaryCodeRemovalChecksum),
            new MigrationRow(9, ProjectGroupsMigrationName, ProjectGroupsChecksum),
        };
        if (rows.Length > expected.Length || rows.Where((row, index) => row != expected[index]).Any())
            throw new InvalidOperationException("Unknown or modified .NET migration history; upgrade this application or restore the correct migration definitions.");
        if (requireCurrent && rows.Length != CurrentVersion)
            throw new InvalidOperationException("Unsupported .NET schema version or modified migration history; run the matching application migration command.");
    }

    private static string Checksum(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name + ":1")));

    private static Task<bool> HasTableAsync(MySqlConnection conn, string table, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table)", new { table }, cancellationToken: ct));
    private static Task<bool> HasColumnAsync(MySqlConnection conn, string table, string column, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)", new { table, column }, cancellationToken: ct));
    private static Task<bool> HasIndexAsync(MySqlConnection conn, string table, string index, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name=@table AND index_name=@index)", new { table, index }, cancellationToken: ct));
    private static Task<bool> HasConstraintAsync(MySqlConnection conn, string table, string constraint, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema=DATABASE() AND table_name=@table AND constraint_name=@constraint)", new { table, constraint }, cancellationToken: ct));
    private static async Task MigrateProjectDictionaryCodeRemovalAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasColumnAsync(conn, "project_dictionaries", "code", ct))
        {
            if (await HasIndexAsync(conn, "project_dictionaries", "uk_project_dictionaries_type_name", ct)) return;
            throw new InvalidOperationException("Project dictionary schema is missing code before migration 8; restore the matching schema before retrying.");
        }
        var duplicates = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM (SELECT type,name FROM project_dictionaries GROUP BY type,name HAVING COUNT(*)>1) duplicates",
            cancellationToken: ct));
        if (duplicates > 0)
            throw new InvalidOperationException("Project dictionary migration 8 found duplicate names within a dictionary type; resolve duplicates before retrying.");
        if (!await HasIndexAsync(conn, "project_dictionaries", "uk_project_dictionaries_type_name", ct))
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE project_dictionaries ADD UNIQUE KEY uk_project_dictionaries_type_name (type,name)", cancellationToken: ct));
        if (await HasColumnAsync(conn, "project_dictionaries", "code", ct))
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE project_dictionaries DROP INDEX uk_project_dictionaries_type_code, DROP COLUMN code", cancellationToken: ct));
        if (await HasColumnAsync(conn, "project_dictionaries", "code", ct))
            throw new InvalidOperationException("Project dictionary migration 8 did not remove code; no application restart was attempted.");
    }
    private static async Task EnsureProjectMetadataColumnsAsync(MySqlConnection conn, CancellationToken ct)
    {
        var columns = new (string Name, string Definition)[]
        {
            ("machine_model", "VARCHAR(128) NULL"),
            ("robot_vendor_id", "BIGINT UNSIGNED NULL"),
            ("robot_model_id", "BIGINT UNSIGNED NULL"),
            ("responsible_user_id", "BIGINT UNSIGNED NULL"),
            ("section_id", "BIGINT UNSIGNED NULL"),
            ("priority_id", "BIGINT UNSIGNED NULL"),
            ("expected_completion_date", "DATE NULL"),
        };
        foreach (var column in columns)
        {
            if (!await HasColumnAsync(conn, "projects", column.Name, ct))
                await conn.ExecuteAsync(new CommandDefinition($"ALTER TABLE projects ADD COLUMN `{column.Name}` {column.Definition}", cancellationToken: ct));
        }
        var indexes = new (string Name, string Columns)[]
        {
            ("idx_projects_robot_vendor", "robot_vendor_id"),
            ("idx_projects_robot_model", "robot_model_id"),
            ("idx_projects_responsible_user", "responsible_user_id"),
            ("idx_projects_section", "section_id"),
            ("idx_projects_priority", "priority_id"),
            ("idx_projects_expected_completion", "expected_completion_date"),
        };
        foreach (var index in indexes)
        {
            var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='projects' AND index_name=@Name)",
                new { index.Name }, cancellationToken: ct));
            if (!exists)
                await conn.ExecuteAsync(new CommandDefinition($"CREATE INDEX `{index.Name}` ON projects({index.Columns})", cancellationToken: ct));
        }
    }

    private static async Task ValidateProjectMetadataColumnsAsync(MySqlConnection conn, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<ProjectMetadataColumn>(new CommandDefinition(
            """
            SELECT column_name AS Name,column_type AS ColumnType,is_nullable AS IsNullable
            FROM information_schema.columns
            WHERE table_schema=DATABASE() AND table_name='projects'
              AND column_name IN ('machine_model','robot_vendor_id','robot_model_id','responsible_user_id','section_id','priority_id','expected_completion_date')
            """, cancellationToken: ct))).ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["machine_model"] = "varchar(128)",
            ["robot_vendor_id"] = "bigint unsigned",
            ["robot_model_id"] = "bigint unsigned",
            ["responsible_user_id"] = "bigint unsigned",
            ["section_id"] = "bigint unsigned",
            ["priority_id"] = "bigint unsigned",
            ["expected_completion_date"] = "date",
        };
        foreach (var item in expected)
        {
            if (!rows.TryGetValue(item.Key, out var row)
                || !NormalizeIntegerDisplayWidth(row.ColumnType).Equals(item.Value, StringComparison.OrdinalIgnoreCase)
                || row.IsNullable != "YES")
                throw new InvalidOperationException($"Database schema version 6 has an unsupported projects.{item.Key} definition.");
        }
    }

    private static async Task EnsureProjectGroupsAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasTableAsync(conn, "project_groups", ct))
            await conn.ExecuteAsync(new CommandDefinition(ProjectGroupsTableSql, cancellationToken: ct));
        if (!await HasTableAsync(conn, "project_group_work_orders", ct))
            await conn.ExecuteAsync(new CommandDefinition(ProjectGroupWorkOrdersTableSql, cancellationToken: ct));
        if (!await HasColumnAsync(conn, "projects", "project_group_id", ct))
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE projects ADD COLUMN project_group_id BIGINT UNSIGNED NULL AFTER id", cancellationToken: ct));
        if (!await HasIndexAsync(conn, "projects", "idx_projects_group", ct))
            await conn.ExecuteAsync(new CommandDefinition("CREATE INDEX idx_projects_group ON projects(project_group_id,id)", cancellationToken: ct));
        if (!await HasConstraintAsync(conn, "projects", "fk_projects_group", ct))
            await conn.ExecuteAsync(new CommandDefinition("ALTER TABLE projects ADD CONSTRAINT fk_projects_group FOREIGN KEY(project_group_id) REFERENCES project_groups(id)", cancellationToken: ct));
        if (!await HasTableAsync(conn, "project_group_status_logs", ct))
            await conn.ExecuteAsync(new CommandDefinition(ProjectGroupStatusLogsTableSql, cancellationToken: ct));

        await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
            INSERT IGNORE INTO project_groups(
                name,description,supplier_id,status,created_by,machine_model,robot_vendor_id,robot_model_id,
                responsible_user_id,section_id,priority_id,expected_completion_date,completed_at,created_at,updated_at)
            SELECT p.name,p.description,p.supplier_id,
                   CASE
                     WHEN p.status='COMPLETED' THEN 'COMPLETED'
                     WHEN p.status='TERMINATED' THEN 'TERMINATED'
                     WHEN p.status='DRAFT' THEN 'DRAFT'
                     ELSE 'IN_PROGRESS'
                   END,
                   p.created_by,p.machine_model,p.robot_vendor_id,p.robot_model_id,p.responsible_user_id,
                   p.section_id,p.priority_id,p.expected_completion_date,
                   CASE WHEN p.status='COMPLETED' THEN p.updated_at ELSE NULL END,
                   p.created_at,p.updated_at
            FROM projects p
            WHERE p.project_group_id IS NULL
            """, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
            UPDATE projects p
            INNER JOIN project_groups g ON g.name=p.name
            SET p.project_group_id=g.id,p.updated_at=p.updated_at
            WHERE p.project_group_id IS NULL
            """, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
            INSERT IGNORE INTO project_group_work_orders(project_group_id,work_order_no,sort_no,created_at)
            SELECT p.project_group_id,pwo.work_order_no,pwo.sort_no,pwo.created_at
            FROM project_work_orders pwo
            INNER JOIN projects p ON p.id=pwo.project_id
            WHERE p.project_group_id IS NOT NULL
            """, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
            INSERT INTO project_group_status_logs(project_group_id,from_status,to_status,action,trigger_project_id,operator_id,created_at)
            SELECT g.id,NULL,g.status,'MIGRATE',MIN(p.id),g.created_by,g.created_at
            FROM project_groups g
            INNER JOIN projects p ON p.project_group_id=g.id
            WHERE NOT EXISTS(SELECT 1 FROM project_group_status_logs log WHERE log.project_group_id=g.id)
            GROUP BY g.id,g.status,g.created_by,g.created_at
            """, transaction: tx, cancellationToken: ct));
            if (await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                    "SELECT COUNT(*) FROM projects WHERE project_group_id IS NULL", transaction: tx, cancellationToken: ct)) > 0)
                throw new InvalidOperationException("Project group migration could not assign every existing project to a main project.");
            await tx.CommitAsync(ct);
        }
        var nullable = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='projects' AND column_name='project_group_id'",
            cancellationToken: ct));
        if (nullable == "YES")
            await conn.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE projects MODIFY project_group_id BIGINT UNSIGNED NOT NULL",
                cancellationToken: ct));
    }

    private static async Task ValidateProjectGroupsAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasTableAsync(conn, "project_groups", ct)
            || !await HasTableAsync(conn, "project_group_work_orders", ct)
            || !await HasTableAsync(conn, "project_group_status_logs", ct)
            || !await HasColumnAsync(conn, "projects", "project_group_id", ct))
            throw new InvalidOperationException("Database schema version 9 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_groups", ProjectGroupsTableSql, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_group_work_orders", ProjectGroupWorkOrdersTableSql, ct);
        await SchemaShapeValidator.ValidateTableAsync(conn, "project_group_status_logs", ProjectGroupStatusLogsTableSql, ct);
        var relation = await conn.QuerySingleOrDefaultAsync<ProjectMetadataColumn>(new CommandDefinition(
            """
            SELECT column_name AS Name,column_type AS ColumnType,is_nullable AS IsNullable
            FROM information_schema.columns
            WHERE table_schema=DATABASE() AND table_name='projects' AND column_name='project_group_id'
            """, cancellationToken: ct));
        if (relation is null
            || !NormalizeIntegerDisplayWidth(relation.ColumnType).Equals("bigint unsigned", StringComparison.OrdinalIgnoreCase)
            || relation.IsNullable != "NO")
            throw new InvalidOperationException("Database schema version 9 requires a non-null projects.project_group_id relation.");
        if (!await HasIndexAsync(conn, "projects", "idx_projects_group", ct)
            || !await HasConstraintAsync(conn, "projects", "fk_projects_group", ct))
            throw new InvalidOperationException("Database schema version 9 is missing the project-to-group relationship.");
        if (await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE project_group_id IS NULL", cancellationToken: ct)) > 0)
            throw new InvalidOperationException("Database contains projects without a main project.");
    }

    private static string NormalizeIntegerDisplayWidth(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        const string prefix = "bigint(";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return normalized;
        var close = normalized.IndexOf(')', prefix.Length);
        if (close < 0 || !normalized[prefix.Length..close].All(char.IsAsciiDigit)) return normalized;
        return "bigint" + normalized[(close + 1)..];
    }
    private sealed record MigrationRow(int Version, string Name, string Checksum);
    private sealed class ProjectMetadataColumn
    {
        public string Name { get; init; } = string.Empty;
        public string ColumnType { get; init; } = string.Empty;
        public string IsNullable { get; init; } = string.Empty;
    }
    private sealed record SupplierGrant(ulong RoleId, ulong PermissionId, string Code);
    private sealed record PendingAcceptanceMigration(ulong ProjectId, ulong? LatestSubmissionId);
    private sealed record PendingAcceptanceNotificationMigration(
        ulong ProjectId,
        string ProjectName,
        ulong SupplierId,
        string ProjectStatus,
        string ConfirmSide,
        ulong CreatedBy,
        DateTime ProjectCreatedAt,
        DateTime ProjectUpdatedAt,
        ulong LatestSubmissionId,
        ulong SubmitterId,
        string SubmitterEmployeeNo,
        string SubmitterUserType,
        ulong? SubmitterSupplierId);
}
