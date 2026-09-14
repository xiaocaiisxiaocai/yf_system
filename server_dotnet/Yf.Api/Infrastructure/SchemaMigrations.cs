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
    public const int CurrentVersion = 4;
    private const string PreviousBaseline = "m20260910_000016_project_workflow";
    private const string FirstMigrationName = "000001_adopt_schema_sessions_supplier_boundary";
    private const string CollaborationMigrationName = "000002_collaboration_notification_reads";
    private const string InternalAcceptanceMigrationName = "000003_internal_project_acceptance";
    private const string AcceptanceNotificationMigrationName = "000004_versioned_acceptance_notifications";
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
    private static string FirstChecksum => Checksum(FirstMigrationName);
    private static string CollaborationChecksum => Checksum(CollaborationMigrationName);
    private static string InternalAcceptanceChecksum => Checksum(InternalAcceptanceMigrationName);
    private static string AcceptanceNotificationChecksum => Checksum(AcceptanceNotificationMigrationName);

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
        if (migrationRows.Any(row => row.Version >= 2) && !hasCollaborationReads)
            throw new InvalidOperationException("Migration history says collaboration reads are applied, but the required table is missing. Restore the matching schema before retrying.");
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
        await tx.CommitAsync(ct);
        ValidateMigrationRows(await ReadMigrationRowsAsync(conn, ct), requireCurrent: true);
        Console.WriteLine("Database is at .NET schema version " + CurrentVersion + ". Pending acceptance records were normalized; no project history was removed.");
    }

    public static async Task ValidateAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasTableAsync(conn, "yf_schema_migrations", ct))
            throw new InvalidOperationException("Database has not been adopted by .NET. Back up the database and run --migrate-database explicitly before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "yf_schema_migrations", MigrationTableSql, ct);
        ValidateMigrationRows(await ReadMigrationRowsAsync(conn, ct), requireCurrent: true);
        if (!await HasTableAsync(conn, "collaboration_reads", ct))
            throw new InvalidOperationException("Database schema version 4 is incomplete. Run the matching application migration command before startup.");
        await SchemaShapeValidator.ValidateTableAsync(conn, "collaboration_reads", CollaborationReadsTableSql, ct);
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
    private sealed record MigrationRow(int Version, string Name, string Checksum);
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
