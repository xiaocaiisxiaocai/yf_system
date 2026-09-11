using System.Text.Json;
using Dapper;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Infrastructure;

public static class SchemaBootstrap
{
    public const string Version = "m20260911_000017_auth_session_families";

    public static async Task InitializeEmptyAsync(AppDb db, CancellationToken ct = default)
    {
        var password = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD")
            ?? throw new InvalidOperationException("Initialization requires process environment YF_BOOTSTRAP_PASSWORD; passwords are never generated or printed.");
        var hash = await PasswordService.HashAsync(password, ct);
        await using var conn = await db.OpenAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()", cancellationToken: ct));
        if (count != 0) throw new InvalidOperationException("Initialization refused: target database is not empty. Existing databases must use compatible migrations.");
        var resource = typeof(SchemaBootstrap).Assembly.GetManifestResourceStream("Yf.Api.Infrastructure.schema-baseline.json")
            ?? throw new InvalidOperationException("Embedded schema baseline missing.");
        using var baseline = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        // DDL implicitly commits in MySQL. A failure leaves an incomplete new database; never drop it automatically.
        await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", cancellationToken: ct));
        try
        {
            foreach (var table in baseline.RootElement.GetProperty("tables").EnumerateArray())
                await conn.ExecuteAsync(new CommandDefinition(table.GetProperty("sql").GetString()!, cancellationToken: ct));
        }
        finally { await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=1", cancellationToken: CancellationToken.None)); }
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var table in baseline.RootElement.GetProperty("seeds").EnumerateObject())
        {
            foreach (var row in table.Value.EnumerateArray())
            {
                var fields = row.EnumerateObject().ToArray();
                var args = new DynamicParameters();
                for (var i = 0; i < fields.Length; i++)
                {
                    var value = fields[i].Value;
                    args.Add("p" + i, value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => fields[i].Name.EndsWith("_at", StringComparison.Ordinal) && DateTime.TryParse(value.GetString(), out var at) ? at : value.GetString()
                    });
                }
                var sql = $"INSERT INTO `{table.Name}` ({string.Join(',', fields.Select(x => $"`{x.Name}`"))}) VALUES ({string.Join(',', fields.Select((_, i) => "@p" + i))})";
                await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
            }
        }
        var id = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("INSERT INTO users(employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES('admin',@hash,'系统管理员','','INTERNAL','ACTIVE',1,0,UTC_TIMESTAMP(),UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();", new { hash }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO user_roles(user_id,role_id) SELECT @id,id FROM roles WHERE name='系统管理员' AND is_built_in=1", new { id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        Console.WriteLine("Empty database initialized. Administrator: admin; initial password must be changed at first login. No password printed.");
    }

    public static async Task ValidateAsync(AppDb db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='seaql_migrations'", cancellationToken: ct));
        if (exists != 1) throw new InvalidOperationException("Database schema missing. Initialize an empty database with --initialize-database, or migrate an existing database explicitly.");
        var latest = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT version FROM seaql_migrations ORDER BY version DESC LIMIT 1", cancellationToken: ct));
        if (latest != Version) throw new InvalidOperationException($"Unsupported database baseline. Required: {Version}. No automatic migration was performed.");
        var gate = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM system_configs WHERE cfg_key='security.management_lock'", cancellationToken: ct));
        if (gate != 1) throw new InvalidOperationException("Database permission gate missing.");
    }
}
