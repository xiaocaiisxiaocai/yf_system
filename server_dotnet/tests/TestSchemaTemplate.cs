using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>
/// Gives a disposable test database the same schema and seed that
/// <see cref="SchemaBootstrap.InitializeEmptyAsync(AppDb, string, CancellationToken)"/> produces, by copying a
/// template migrated once per test process. Replaying every EF migration for each test database rebuilt dozens of
/// tables per test and wrote terabytes a day to the local SSD. Tests that exercise initialization or migrations
/// themselves keep calling <see cref="SchemaBootstrap"/> directly.
/// </summary>
internal static partial class TestSchemaTemplate
{
    internal const string TemplatePrefix = "yf_tpl_";
    private const string TemplateCollation = "utf8mb4_unicode_ci";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> PasswordHashes = new(StringComparer.Ordinal);
    private static TemplateSnapshot? templateSnapshot;
    // Holds GET_LOCK(templateName) for the life of the process so other test runs can tell a live template from an
    // abandoned one.
    private static MySqlConnection? owner;

    internal static async Task InitializeEmptyAsync(AppDb target, string bootstrapPassword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bootstrapPassword))
            throw new InvalidOperationException("Initialization requires a nonempty bootstrap password.");
        await using var connection = await target.OpenAsync(ct);
        var collation = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = DATABASE()",
            cancellationToken: ct));
        if (collation != TemplateCollation)
        {
            await connection.DisposeAsync();
            await SchemaBootstrap.InitializeEmptyAsync(target, bootstrapPassword, ct);
            return;
        }
        if (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()",
                cancellationToken: ct)) != 0)
            throw new InvalidOperationException(
                "Initialization refused: target database is not empty. Recreate an empty development database before initialization.");

        var template = await EnsureTemplateAsync(ct);
        var passwordHash = await HashBootstrapPasswordAsync(bootstrapPassword, ct);
        var sqlMode = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT @@SESSION.sql_mode", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("SET SESSION FOREIGN_KEY_CHECKS = 0", cancellationToken: ct));
        try
        {
            await CopyTablesAsync(connection, template, ct);
            // Before the triggers exist, so the identity-revision trigger on users does not see a change that a
            // freshly seeded database never had.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE users SET password_hash = @passwordHash WHERE employee_no = 'admin'",
                new { passwordHash }, cancellationToken: ct));
            await CopyTriggersAsync(connection, template, sqlMode, ct);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SET SESSION FOREIGN_KEY_CHECKS = 1, SESSION sql_mode = @sqlMode",
                new { sqlMode }, cancellationToken: CancellationToken.None));
        }
    }

    private static async Task<string> HashBootstrapPasswordAsync(string bootstrapPassword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Validate before caching so invalid passwords never enter the cache.
        PasswordService.Validate(bootstrapPassword);
        var lazy = PasswordHashes.GetOrAdd(
            bootstrapPassword,
            static password => new Lazy<Task<string>>(
                () => PasswordService.HashAsync(password, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        try
        {
            return await task.WaitAsync(ct);
        }
        catch when (!ct.IsCancellationRequested || task.IsFaulted || task.IsCanceled)
        {
            PasswordHashes.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(bootstrapPassword, lazy));
            throw;
        }
    }

    private static async Task CopyTablesAsync(MySqlConnection connection, TemplateSnapshot template, CancellationToken ct)
    {
        foreach (var table in template.Tables)
        {
            await connection.ExecuteAsync(new CommandDefinition(table.CreateSql, cancellationToken: ct));
            if (table.InsertColumns is { } list)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO {Quote(table.Name)} ({list}) SELECT {list} FROM {Quote(template.Name)}.{Quote(table.Name)}",
                    cancellationToken: ct));
            }
        }
    }

    private static async Task CopyTriggersAsync(
        MySqlConnection connection, TemplateSnapshot template, string? initialSqlMode, CancellationToken ct)
    {
        var currentSqlMode = initialSqlMode;
        foreach (var trigger in template.Triggers)
        {
            if (!string.Equals(trigger.SqlMode, currentSqlMode, StringComparison.Ordinal))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SET SESSION sql_mode = @sqlMode", new { sqlMode = trigger.SqlMode }, cancellationToken: ct));
                currentSqlMode = trigger.SqlMode;
            }
            await connection.ExecuteAsync(new CommandDefinition(trigger.CreateSql, cancellationToken: ct));
        }
    }

    private static async Task<TemplateSnapshot> LoadTemplateSnapshotAsync(
        MySqlConnection connection, string template, CancellationToken ct)
    {
        var unsupported = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @template)
                 + (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @template)
                 + (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = @template)
            """, new { template }, cancellationToken: ct));
        if (unsupported != 0)
            throw new NotSupportedException(
                "The migrated schema now has views, routines or events; teach TestSchemaTemplate to copy them.");

        var tableNames = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @template AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
            """, new { template }, cancellationToken: ct))).ToList();
        var columns = (await connection.QueryAsync<(string Table, string Column)>(new CommandDefinition("""
            SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @template
              AND EXTRA NOT LIKE '%VIRTUAL GENERATED%' AND EXTRA NOT LIKE '%STORED GENERATED%'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """, new { template }, cancellationToken: ct)))
            .GroupBy(row => row.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => Quote(row.Column))),
                StringComparer.Ordinal);

        var tables = new List<TemplateTable>(tableNames.Count);
        foreach (var table in tableNames)
        {
            var create = await ReadColumnAsync(connection, $"SHOW CREATE TABLE {Quote(template)}.{Quote(table)}", 1, ct);
            // Query exact presence once (InnoDB TABLE_ROWS is an estimate and may report 0 on small seeded tables).
            var hasRows = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                $"SELECT 1 FROM {Quote(template)}.{Quote(table)} LIMIT 1", cancellationToken: ct)) is not null;
            tables.Add(new TemplateTable(table, create, hasRows ? columns[table] : null));
        }

        var triggerNames = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TRIGGER_NAME FROM information_schema.TRIGGERS
            WHERE TRIGGER_SCHEMA = @template
            ORDER BY EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """, new { template }, cancellationToken: ct));
        var triggers = new List<TemplateTrigger>();
        foreach (var trigger in triggerNames)
        {
            await using var command = new MySqlCommand(
                $"SHOW CREATE TRIGGER {Quote(template)}.{Quote(trigger)}", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"Trigger {trigger} vanished");
            var sqlMode = reader.GetString(reader.GetOrdinal("sql_mode"));
            var statement = reader.GetString(reader.GetOrdinal("SQL Original Statement"));
            triggers.Add(new TemplateTrigger(sqlMode, Definer().Replace(statement, "CREATE ", 1)));
        }

        return new TemplateSnapshot(template, tables, triggers);
    }

    private static async Task<TemplateSnapshot> EnsureTemplateAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref templateSnapshot) is { } ready) return ready;
        await Gate.WaitAsync(ct);
        try
        {
            if (templateSnapshot is { } existing) return existing;
            var adminOptions = LocalMySqlTestServer.AdministrationOrSkip("Database tests");
            adminOptions.Pooling = false;
            var connection = new MySqlConnection(adminOptions.ConnectionString);
            await connection.OpenAsync(ct);
            var name = TemplatePrefix + Guid.NewGuid().ToString("N")[..16];
            TemplateSnapshot snapshot;
            try
            {
                if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                        "SELECT GET_LOCK(@name, 0)", new { name }, cancellationToken: ct)) != 1)
                    throw new InvalidOperationException($"Could not lock test schema template {name}");
                await DropAbandonedTemplatesAsync(connection, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE {Quote(name)} CHARACTER SET utf8mb4 COLLATE {TemplateCollation}",
                    cancellationToken: ct));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                    {
                        Database = name,
                        Pooling = false,
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_schema_template_storage"),
                    WorkerEnabled = false,
                };
                await SchemaBootstrap.InitializeEmptyAsync(
                    new AppDb(options), "Template#" + Guid.NewGuid().ToString("N")[..8] + "!", ct);
                snapshot = await LoadTemplateSnapshotAsync(connection, name, ct);
            }
            catch
            {
                try { await connection.ExecuteAsync($"DROP DATABASE IF EXISTS {Quote(name)}"); }
                finally { await connection.DisposeAsync(); }
                throw;
            }
            owner = connection;
            Volatile.Write(ref templateSnapshot, snapshot);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DropOwnTemplate();
            return snapshot;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Drops templates whose owning test process is gone (its named lock is free).</summary>
    private static async Task DropAbandonedTemplatesAsync(MySqlConnection connection, CancellationToken ct)
    {
        var names = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME LIKE 'yf\\_tpl\\_%'",
            cancellationToken: ct));
        foreach (var name in names)
        {
            if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT IS_FREE_LOCK(@name)", new { name }, cancellationToken: ct)) == 1)
                await connection.ExecuteAsync(new CommandDefinition(
                    $"DROP DATABASE IF EXISTS {Quote(name)}", cancellationToken: ct));
        }
    }

    private static void DropOwnTemplate()
    {
        var connection = Interlocked.Exchange(ref owner, null);
        if (connection is null || Volatile.Read(ref templateSnapshot) is not { } snapshot) return;
        // A failure here leaves the template behind with a free lock; the next run drops it.
        try { connection.Execute($"DROP DATABASE IF EXISTS {Quote(snapshot.Name)}"); }
        catch (MySqlException) { }
        finally { connection.Dispose(); }
    }

    private static async Task<string> ReadColumnAsync(MySqlConnection connection, string sql, int ordinal, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"No result for {sql}");
        return reader.GetString(ordinal);
    }

    private static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    private sealed record TemplateTable(string Name, string CreateSql, string? InsertColumns);
    private sealed record TemplateTrigger(string SqlMode, string CreateSql);
    private sealed record TemplateSnapshot(
        string Name,
        IReadOnlyList<TemplateTable> Tables,
        IReadOnlyList<TemplateTrigger> Triggers);

    [GeneratedRegex(@"^\s*CREATE\s+DEFINER\s*=\s*(`[^`]*`|'[^']*'|[^@\s]+)@(`[^`]*`|'[^']*'|\S+)\s+",
        RegexOptions.IgnoreCase)]
    private static partial Regex Definer();
}
