using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Yf.Api.Modules.Admin;

namespace Yf.Api.Infrastructure;

/// <summary>Explicit, restartable .NET-owned schema upgrades. Startup never changes schema.</summary>
public static class SchemaMigrations
{
    public const int CurrentVersion = 1;
    private const string PreviousBaseline = "m20260910_000016_project_workflow";
    private const string MigrationName = "000001_adopt_schema_sessions_supplier_boundary";
    private static string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(MigrationName + ":1")));

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

        // Check business tables before any DDL. The one deliberately missing field
        // in baseline 16 is repaired below. This catches partial initialization too.
        await ValidateShapeAsync(conn, allowMissingSessionId: legacy == PreviousBaseline, ct);
        await conn.ExecuteAsync(new CommandDefinition("CREATE TABLE IF NOT EXISTS yf_schema_migrations (version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL, checksum CHAR(64) NOT NULL, applied_at DATETIME(6) NOT NULL)", cancellationToken: ct));
        var rows = (await conn.QueryAsync<MigrationRow>(new CommandDefinition("SELECT version,name,checksum FROM yf_schema_migrations ORDER BY version", cancellationToken: ct))).ToArray();
        if (rows.Any(x => x.Version != CurrentVersion || x.Name != MigrationName || x.Checksum != Checksum))
            throw new InvalidOperationException("Unknown or modified .NET migration history; upgrade this application or restore the correct migration definitions.");

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
        await ValidateShapeAsync(conn, false, ct);
        await ValidateSessionSchemaAsync(conn, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var supplierGrants = await conn.QueryAsync<SupplierGrant>(new CommandDefinition(
            "SELECT rp.role_id RoleId,rp.permission_id PermissionId,p.code Code FROM role_permissions rp JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id WHERE r.is_built_in=1 AND r.name='供应商人员'", transaction: tx, cancellationToken: ct));
        foreach (var grant in supplierGrants.Where(x => !RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员", [x.Code])))
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM role_permissions WHERE role_id=@RoleId AND permission_id=@PermissionId", grant, tx, cancellationToken: ct));
            await new AuditService([]).WriteAsync(conn, tx, null, "ROLE_ASSIGN_PERMS", "role", grant.RoleId,
                new { migration = MigrationName, removedPermission = grant.Code, reason = "supplier identity boundary" }, null, ct);
        }
        if (legacy == PreviousBaseline)
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO seaql_migrations(version,applied_at) VALUES (@version,UNIX_TIMESTAMP())", new { version = SchemaBootstrap.Version }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO yf_schema_migrations(version,name,checksum,applied_at) VALUES (@version,@name,@checksum,UTC_TIMESTAMP(6))", new { version = CurrentVersion, name = MigrationName, checksum = Checksum }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        Console.WriteLine("Database is at .NET schema version " + CurrentVersion + ". No business records were removed.");
    }

    public static async Task ValidateAsync(MySqlConnection conn, CancellationToken ct)
    {
        if (!await HasTableAsync(conn, "yf_schema_migrations", ct))
            throw new InvalidOperationException("Database has not been adopted by .NET. Back up the database and run --migrate-database explicitly before startup.");
        var rows = (await conn.QueryAsync<MigrationRow>(new CommandDefinition("SELECT version,name,checksum FROM yf_schema_migrations ORDER BY version", cancellationToken: ct))).ToArray();
        if (rows.Length != 1 || rows[0].Version != CurrentVersion || rows[0].Name != MigrationName || rows[0].Checksum != Checksum)
            throw new InvalidOperationException("Unsupported .NET schema version or modified migration history; run the matching application migration command.");
        await ValidateShapeAsync(conn, false, ct);
        await ValidateSessionSchemaAsync(conn, ct);
    }

    private static async Task ValidateSessionSchemaAsync(MySqlConnection conn, CancellationToken ct)
    {
        var columnOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='refresh_tokens' AND column_name='session_id' AND data_type='varchar' AND character_maximum_length=36 AND is_nullable='NO')", cancellationToken: ct));
        var indexColumns = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='refresh_tokens' AND index_name='idx_refresh_tokens_session_state'", cancellationToken: ct));
        if (!columnOk || indexColumns != "session_id,user_id,revoked,expires_at")
            throw new InvalidOperationException("Session-family column or index has an unsupported definition. Inspect schema before migration.");
    }

    private static async Task ValidateShapeAsync(MySqlConnection conn, bool allowMissingSessionId, CancellationToken ct)
    {
        using var resource = typeof(SchemaBootstrap).Assembly.GetManifestResourceStream("Yf.Api.Infrastructure.schema-baseline.json")!;
        using var baseline = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        var columns = (await conn.QueryAsync<ColumnRow>(new CommandDefinition("SELECT table_name TableName,column_name ColumnName FROM information_schema.columns WHERE table_schema=DATABASE()", cancellationToken: ct)))
            .Select(x => x.TableName + "." + x.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in baseline.RootElement.GetProperty("tables").EnumerateArray())
        {
            var name = table.GetProperty("name").GetString()!;
            foreach (Match match in Regex.Matches(table.GetProperty("sql").GetString()!, @"(?m)^\s*`([^`]+)`\s+"))
            {
                var column = name + "." + match.Groups[1].Value;
                if (allowMissingSessionId && column == "refresh_tokens.session_id") continue;
                if (!columns.Contains(column)) throw new InvalidOperationException("Incomplete database schema: missing " + column);
            }
        }
        var gate = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM system_configs WHERE cfg_key='security.management_lock'", cancellationToken: ct));
        if (gate != 1) throw new InvalidOperationException("Database permission gate missing.");
    }

    private static Task<bool> HasTableAsync(MySqlConnection conn, string table, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table)", new { table }, cancellationToken: ct));
    private static Task<bool> HasColumnAsync(MySqlConnection conn, string table, string column, CancellationToken ct) => conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)", new { table, column }, cancellationToken: ct));
    private sealed record MigrationRow(int Version, string Name, string Checksum);
    private sealed record ColumnRow(string TableName, string ColumnName);
    private sealed record SupplierGrant(ulong RoleId, ulong PermissionId, string Code);
}
