using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

/// <summary>
/// Hand-written minimal schemas used by tests that cannot (or deliberately do not) run the EF migrations.
/// They are shared constants so <see cref="HandwrittenSchemaGuardTests"/> can compare them with the real EF
/// model: a table may carry fewer columns than the model, but never a column the model no longer has.
/// </summary>
internal static class HandwrittenTestSchemas
{
    internal const string FilesRecovery = """
        CREATE TABLE system_configs(
            cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL
        );
        CREATE TABLE users(
            id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(50) NOT NULL,
            user_type VARCHAR(20) NOT NULL, supplier_id BIGINT UNSIGNED NULL,
            status VARCHAR(20) NOT NULL, must_change_password BOOLEAN NOT NULL,
            email VARCHAR(255) NULL, real_name VARCHAR(100) NOT NULL,
            password_hash VARCHAR(255) NOT NULL DEFAULT '', department_id BIGINT UNSIGNED NULL,
            last_login_at DATETIME(6) NULL, created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        );
        CREATE TABLE suppliers(
            id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(128) NOT NULL,
            status VARCHAR(16) NOT NULL DEFAULT 'ACTIVE'
        );
        CREATE TABLE roles(
            id BIGINT UNSIGNED PRIMARY KEY, name VARCHAR(100) NOT NULL,
            status VARCHAR(20) NOT NULL, is_built_in BOOLEAN NOT NULL
        );
        CREATE TABLE permissions(
            id BIGINT UNSIGNED PRIMARY KEY,
            code VARCHAR(100) NOT NULL,
            type VARCHAR(16) NOT NULL DEFAULT 'BUTTON',
            sort_no INT NOT NULL DEFAULT 0
        );
        CREATE TABLE user_roles(user_id BIGINT UNSIGNED NOT NULL, role_id BIGINT UNSIGNED NOT NULL);
        CREATE TABLE role_permissions(role_id BIGINT UNSIGNED NOT NULL, permission_id BIGINT UNSIGNED NOT NULL);
        CREATE TABLE project_groups(id BIGINT UNSIGNED PRIMARY KEY, created_by BIGINT UNSIGNED NOT NULL DEFAULT 0);
        CREATE TABLE projects(
            id BIGINT UNSIGNED PRIMARY KEY, project_group_id BIGINT UNSIGNED NOT NULL,
            supplier_id BIGINT UNSIGNED NOT NULL,
            created_by BIGINT UNSIGNED NOT NULL, status VARCHAR(30) NOT NULL,
            name VARCHAR(100) NOT NULL,
            responsible_user_id BIGINT UNSIGNED NULL,
            material_review_status VARCHAR(16) NULL, material_review_file_id BIGINT UNSIGNED NULL,
            material_review_reason VARCHAR(500) NULL, material_file_floor BIGINT UNSIGNED NULL,
            due_soon_notified_for DATE NULL, overdue_notified_for DATE NULL
        );
        CREATE TABLE audit_logs(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            user_id BIGINT UNSIGNED NULL, employee_no VARCHAR(50) NULL,
            action VARCHAR(100) NOT NULL, target_type VARCHAR(100) NULL,
            target_id VARCHAR(100) NULL, detail JSON NULL, ip VARCHAR(100) NULL,
            created_at DATETIME(6) NOT NULL,
            actor_realm VARCHAR(16) NULL, actor_account_id BIGINT UNSIGNED NULL
        );
        CREATE TABLE supplier_contacts(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY, supplier_id BIGINT UNSIGNED NOT NULL,
            name VARCHAR(32) NOT NULL, email VARCHAR(128) NOT NULL, status VARCHAR(16) NOT NULL,
            sort_no INT NOT NULL DEFAULT 0, created_by BIGINT UNSIGNED NULL,
            created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
        );
        CREATE TABLE upload_sessions(
            id VARCHAR(36) PRIMARY KEY, project_id BIGINT UNSIGNED NOT NULL,
            uploader_id BIGINT UNSIGNED NOT NULL, file_name VARCHAR(255) NOT NULL,
            file_size BIGINT UNSIGNED NOT NULL, file_last_modified BIGINT NOT NULL,
            file_fingerprint VARCHAR(64) NOT NULL, file_md5 VARCHAR(32) NULL,
            chunk_size INT UNSIGNED NOT NULL, total_chunks INT UNSIGNED NOT NULL,
            temp_dir VARCHAR(512) NOT NULL, status VARCHAR(16) NOT NULL,
            result_file_id BIGINT UNSIGNED NULL, expires_at DATETIME NOT NULL,
            signer_contact_id BIGINT UNSIGNED NULL, signer_name VARCHAR(32) NULL,
            created_at DATETIME NOT NULL, updated_at DATETIME NOT NULL
        );
        CREATE TABLE file_blobs(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
            size_bytes BIGINT UNSIGNED NOT NULL,
            storage_path VARCHAR(512) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
            state VARCHAR(16) NOT NULL DEFAULT 'READY',
            gc_started_at DATETIME(6) NULL,
            created_at DATETIME(6) NOT NULL,
            UNIQUE KEY uk_file_blobs_sha256(sha256),
            UNIQUE KEY uk_file_blobs_storage_path(storage_path),
            KEY idx_file_blobs_state(state,id)
        );
        CREATE TABLE files(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            project_id BIGINT UNSIGNED NOT NULL, uploader_id BIGINT UNSIGNED NOT NULL,
            blob_id BIGINT UNSIGNED NULL,
            direction VARCHAR(10) NOT NULL, original_name VARCHAR(255) NOT NULL,
            stored_name VARCHAR(255) NOT NULL, ext VARCHAR(32) NOT NULL,
            size_bytes BIGINT UNSIGNED NOT NULL, mime_type VARCHAR(255) NULL,
            sha256 VARCHAR(64) NULL, storage_path VARCHAR(1024) NOT NULL,
            status VARCHAR(20) NOT NULL, deleted_at DATETIME(6) NULL,
            signer_contact_id BIGINT UNSIGNED NULL, signer_name VARCHAR(32) NULL,
            created_at DATETIME(6) NOT NULL,
            KEY idx_files_blob(blob_id),
            CONSTRAINT fk_files_blob FOREIGN KEY(blob_id) REFERENCES file_blobs(id) ON DELETE RESTRICT
        );
        -- The maintenance entry point also sweeps soft-deleted message images.
        -- Keep these empty in file-recovery fixtures; image retention has dedicated tests.
        CREATE TABLE messages(
            id BIGINT UNSIGNED PRIMARY KEY, project_id BIGINT UNSIGNED NOT NULL,
            sender_id BIGINT UNSIGNED NOT NULL, content TEXT NOT NULL,
            status VARCHAR(16) NOT NULL, deleted_by BIGINT UNSIGNED NULL,
            deleted_at DATETIME NULL, created_at DATETIME NOT NULL
        );
        CREATE TABLE message_images(
            id BIGINT UNSIGNED PRIMARY KEY, message_id BIGINT UNSIGNED NOT NULL,
            original_name VARCHAR(255) NOT NULL, stored_name VARCHAR(64) NOT NULL,
            ext VARCHAR(8) NOT NULL, size_bytes BIGINT UNSIGNED NOT NULL,
            mime_type VARCHAR(32) NOT NULL, storage_path VARCHAR(512) NOT NULL,
            created_at DATETIME(3) NOT NULL,
            UNIQUE KEY uk_message_images_stored_name(stored_name),
            KEY idx_message_images_message(message_id,id)
        );
        """;

    internal const string MailDelivery = """
        CREATE TABLE system_configs(
            cfg_key VARCHAR(100) PRIMARY KEY,
            cfg_value TEXT NOT NULL
        );
        CREATE TABLE email_outbox(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            event_type VARCHAR(32) NOT NULL,
            project_id BIGINT UNSIGNED NULL,
            dedupe_key VARCHAR(128) NULL,
            recipient_user_id BIGINT UNSIGNED NULL,
            recipient_realm VARCHAR(16) NULL,
            recipient_account_id BIGINT UNSIGNED NULL,
            oem_transfer_id BIGINT UNSIGNED NULL,
            recipient_email VARCHAR(128) NOT NULL,
            subject VARCHAR(255) NOT NULL,
            body TEXT NOT NULL,
            status VARCHAR(16) NOT NULL,
            retry_count INT NOT NULL,
            next_attempt_at DATETIME(3) NULL,
            last_error VARCHAR(1024) NULL,
            sent_at DATETIME(6) NULL,
            created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            UNIQUE KEY uk_outbox_dedupe_key(dedupe_key)
        );
        CREATE TABLE audit_logs(
            id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            user_id BIGINT UNSIGNED NULL,
            actor_realm VARCHAR(16) NULL,
            actor_account_id BIGINT UNSIGNED NULL,
            employee_no VARCHAR(64) NULL,
            action VARCHAR(100) NOT NULL,
            target_type VARCHAR(100) NULL,
            target_id VARCHAR(100) NULL,
            detail JSON NULL,
            ip VARCHAR(64) NULL,
            created_at DATETIME(6) NOT NULL
        );
        CREATE TABLE users(
            id BIGINT UNSIGNED PRIMARY KEY,
            employee_no VARCHAR(64) NOT NULL,
            real_name VARCHAR(64) NOT NULL DEFAULT '',
            email VARCHAR(128) NOT NULL DEFAULT '',
            user_type VARCHAR(16) NOT NULL,
            supplier_id BIGINT UNSIGNED NULL,
            status VARCHAR(16) NOT NULL,
            must_change_password TINYINT(1) NOT NULL DEFAULT 0
        );
        CREATE TABLE permissions(
            id BIGINT UNSIGNED PRIMARY KEY,
            code VARCHAR(100) NOT NULL,
            type VARCHAR(16) NOT NULL DEFAULT 'BUTTON',
            sort_no INT NOT NULL DEFAULT 0
        );
        CREATE TABLE roles(
            id BIGINT UNSIGNED PRIMARY KEY,
            status VARCHAR(16) NOT NULL
        );
        CREATE TABLE user_roles(
            user_id BIGINT UNSIGNED NOT NULL,
            role_id BIGINT UNSIGNED NOT NULL
        );
        CREATE TABLE role_permissions(
            role_id BIGINT UNSIGNED NOT NULL,
            permission_id BIGINT UNSIGNED NOT NULL
        );
        CREATE TABLE suppliers(
            id BIGINT UNSIGNED PRIMARY KEY,
            status VARCHAR(16) NOT NULL
        );
        CREATE TABLE supplier_contacts(
            id BIGINT UNSIGNED PRIMARY KEY,
            supplier_id BIGINT UNSIGNED NOT NULL,
            email VARCHAR(128) NOT NULL,
            status VARCHAR(16) NOT NULL
        );
        CREATE TABLE project_groups(
            id BIGINT UNSIGNED PRIMARY KEY,
            created_by BIGINT UNSIGNED NOT NULL DEFAULT 0
        );
        CREATE TABLE projects(
            id BIGINT UNSIGNED PRIMARY KEY,
            project_group_id BIGINT UNSIGNED NULL,
            supplier_id BIGINT UNSIGNED NOT NULL,
            created_by BIGINT UNSIGNED NOT NULL,
            responsible_user_id BIGINT UNSIGNED NULL,
            status VARCHAR(32) NOT NULL,
            material_review_status VARCHAR(16) NULL, material_review_file_id BIGINT UNSIGNED NULL,
            material_review_reason VARCHAR(500) NULL, material_file_floor BIGINT UNSIGNED NULL,
            due_soon_notified_for DATE NULL, overdue_notified_for DATE NULL
        );
        """;

    internal const string AuthenticationConnection = """
        CREATE TABLE users(
            id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(32) NOT NULL,
            user_type VARCHAR(16) NOT NULL, supplier_id BIGINT UNSIGNED NULL,
            status VARCHAR(16) NOT NULL, must_change_password BOOLEAN NOT NULL
        );
        CREATE TABLE suppliers(id BIGINT UNSIGNED PRIMARY KEY, status VARCHAR(16) NOT NULL);
        CREATE TABLE refresh_tokens(
            user_id BIGINT UNSIGNED NOT NULL, session_id VARCHAR(36) NOT NULL,
            revoked BOOLEAN NOT NULL, expires_at DATETIME NOT NULL,
            session_created_at DATETIME NOT NULL, session_expires_at DATETIME NOT NULL
        );
        CREATE TABLE system_configs(cfg_key VARCHAR(100) PRIMARY KEY, cfg_value TEXT NOT NULL);
        """;

    internal const string EndpointFilter = """
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
        """;
}

/// <summary>
/// Drift guard for <see cref="HandwrittenTestSchemas"/>: every column a hand-written table declares must still
/// exist in the EF model (the model is the schema authority), so a retired column such as
/// <c>projects.confirm_side</c> cannot linger in fixtures. A hand-written table may omit model columns; a column a
/// tested code path needs but the fixture lacks surfaces as an unknown-column error in that DB test itself.
/// </summary>
public sealed class HandwrittenSchemaGuardTests
{
    private static readonly IReadOnlyDictionary<string, string> Schemas = new Dictionary<string, string>
    {
        ["FilesRecovery"] = HandwrittenTestSchemas.FilesRecovery,
        ["MailDelivery"] = HandwrittenTestSchemas.MailDelivery,
        ["AuthenticationConnection"] = HandwrittenTestSchemas.AuthenticationConnection,
        ["EndpointFilter"] = HandwrittenTestSchemas.EndpointFilter,
    };

    private static readonly string[] MaterialReviewColumns =
    [
        "material_review_status", "material_review_file_id", "material_review_reason",
        "material_file_floor", "due_soon_notified_for", "overdue_notified_for",
    ];

    private static readonly HashSet<string> NonColumnClauses = new(StringComparer.Ordinal)
    {
        "PRIMARY", "UNIQUE", "KEY", "CONSTRAINT", "INDEX", "FOREIGN", "FULLTEXT", "CHECK",
    };

    private static readonly Regex CreateTable = new(
        @"CREATE\s+TABLE\s+`?(?<name>\w+)`?\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FirstWord = new(@"^`?(?<word>\w+)`?", RegexOptions.CultureInvariant);

    public static TheoryData<string> SchemaNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Schemas.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void HandwrittenColumnsExistInTheEfModel(string schema)
    {
        var tables = ParseTables(Schemas[schema]);
        Assert.NotEmpty(tables);

        var problems = FindProblems(Schemas[schema], ModelColumns());

        Assert.True(problems.Count == 0,
            $"Hand-written schema '{schema}' drifted from the EF model: {string.Join(", ", problems)}");
    }

    [Theory]
    [InlineData("FilesRecovery")]
    [InlineData("MailDelivery")]
    public void HandwrittenProjectsTableFollowsTheCurrentMaterialReviewShape(string schema)
    {
        var projects = ParseTables(Schemas[schema])["projects"];

        Assert.All(MaterialReviewColumns, column => Assert.Contains(column, projects));
        Assert.DoesNotContain("confirm_side", projects);
    }

    [Fact]
    public void GuardFlagsAColumnTheModelNoLongerHasAndUnknownTables()
    {
        var model = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["projects"] = new HashSet<string>(["id", "name"], StringComparer.Ordinal),
        };

        var problems = FindProblems("""
            -- comments are ignored: CREATE TABLE ghost(x INT);
            CREATE TABLE projects(
                id BIGINT UNSIGNED PRIMARY KEY, confirm_side VARCHAR(16) NULL,
                name VARCHAR(10) NOT NULL DEFAULT 'a,b(', UNIQUE KEY uk_name(name,id)
            );
            CREATE TABLE ghost_table(id INT);
            """, model);

        Assert.Equal(2, problems.Count);
        Assert.Contains("projects.confirm_side", problems);
        Assert.Contains(problems, problem => problem.StartsWith("table ghost_table", StringComparison.Ordinal));
    }

    private static List<string> FindProblems(string sql, IReadOnlyDictionary<string, HashSet<string>> model)
    {
        var problems = new List<string>();
        foreach (var (table, columns) in ParseTables(sql))
        {
            if (!model.TryGetValue(table, out var known))
            {
                problems.Add($"table {table} is not in the EF model");
                continue;
            }
            problems.AddRange(columns.Where(column => !known.Contains(column)).Select(column => $"{table}.{column}"));
        }
        return problems;
    }

    private static Dictionary<string, string[]> ParseTables(string sql)
    {
        var text = Regex.Replace(sql, @"--[^\r\n]*", string.Empty);
        var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match match in CreateTable.Matches(text))
        {
            var columns = new List<string>();
            var depth = 1;
            var quoted = false;
            var itemStart = match.Index + match.Length;
            for (var index = itemStart; index < text.Length && depth > 0; index++)
            {
                var current = text[index];
                if (current == '\'') quoted = !quoted;
                if (quoted) continue;
                if (current == '(') depth++;
                else if (current == ')')
                {
                    depth--;
                    if (depth == 0) AddColumn(columns, text[itemStart..index]);
                }
                else if (current == ',' && depth == 1)
                {
                    AddColumn(columns, text[itemStart..index]);
                    itemStart = index + 1;
                }
            }
            tables[match.Groups["name"].Value.ToLowerInvariant()] = columns.ToArray();
        }
        return tables;
    }

    private static void AddColumn(List<string> columns, string definition)
    {
        var word = FirstWord.Match(definition.Trim());
        if (!word.Success || NonColumnClauses.Contains(word.Groups["word"].Value.ToUpperInvariant())) return;
        columns.Add(word.Groups["word"].Value.ToLowerInvariant());
    }

    private static Dictionary<string, HashSet<string>> ModelColumns()
    {
        // The model is built without a database round trip: a fixed server version and no connection is opened.
        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql("Server=127.0.0.1;Database=model_probe;SslMode=None", EfDb.ServerVersion)
            .Options;
        using var context = new YfDbContext(options);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            if (!result.TryGetValue(table, out var columns))
                result[table] = columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in entity.GetProperties())
                if (property.GetColumnName(store) is { } column) columns.Add(column);
        }
        return result;
    }
}
