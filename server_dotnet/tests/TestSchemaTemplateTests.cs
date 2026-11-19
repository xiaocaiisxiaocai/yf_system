using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>
/// The template copy must be indistinguishable from replaying the EF migrations, or every test that uses it would
/// run against a schema production never has.
/// </summary>
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class TestSchemaTemplateTests
{
    private const string Password = "SchemaTest#2026";

    [Fact(Timeout = 300_000)]
    public async Task CopiedDatabaseMatchesMigratedDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var migrated = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("tpl_migrated", ct);
        await migrated.InitializeWithMigrationsAsync(ct);
        await using var copied = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("tpl_copied", ct);
        await copied.InitializeAsync(ct);

        await using var expected = await migrated.Database.OpenAsync(ct);
        await using var actual = await copied.Database.OpenAsync(ct);

        var tables = await TablesAsync(expected, ct);
        Assert.NotEmpty(tables);
        Assert.Equal(tables, await TablesAsync(actual, ct));
        foreach (var table in tables)
        {
            Assert.Equal(await CreateTableAsync(expected, table, ct), await CreateTableAsync(actual, table, ct));
            Assert.Equal(await RowsAsync(expected, table, ct), await RowsAsync(actual, table, ct));
        }
        Assert.Equal(await TriggersAsync(expected, ct), await TriggersAsync(actual, ct));

        var hash = await actual.ExecuteScalarAsync<string>(
            "SELECT password_hash FROM users WHERE employee_no = 'admin'");
        Assert.True(await PasswordService.VerifyAsync(Password, hash!, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task CopyRefusesNonemptyDatabaseWithoutChangingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("tpl_refuse", ct);
        await database.ExecuteAsync("CREATE TABLE sentinel(id INT NOT NULL PRIMARY KEY)", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InitializeAsync(ct));
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(["sentinel"], await TablesAsync(conn, ct));
    }

    private static async Task<string[]> TablesAsync(MySqlConnection connection, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME
            """, cancellationToken: ct))).ToArray();

    private static async Task<string> CreateTableAsync(MySqlConnection connection, string table, CancellationToken ct)
    {
        await using var command = new MySqlCommand($"SHOW CREATE TABLE `{table}`", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        // The counter reflects rows inserted and is compared through the row contents instead.
        return Regex.Replace(reader.GetString(1), @" AUTO_INCREMENT=\d+", string.Empty);
    }

    /// <summary>
    /// Every row of the table in primary-key order. Timestamps written at seed time differ between the two runs and
    /// the admin password hash is salted, so only their presence is compared.
    /// </summary>
    private static async Task<string[]> RowsAsync(MySqlConnection connection, string table, CancellationToken ct)
    {
        var columns = (await connection.QueryAsync<(string Name, string Type)>(new CommandDefinition("""
            SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION
            """, new { table }, cancellationToken: ct))).ToArray();
        var select = string.Join(", ", columns.Select(column =>
            column.Type is "datetime" or "timestamp" || (table == "users" && column.Name == "password_hash")
                ? $"`{column.Name}` IS NULL"
                : $"`{column.Name}`"));
        var keys = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY ORDINAL_POSITION
            """, new { table }, cancellationToken: ct))).ToArray();
        var order = keys.Length > 0 ? string.Join(", ", keys.Select(key => $"`{key}`")) : "1";
        var rows = new List<string>();
        await using var command = new MySqlCommand($"SELECT {select} FROM `{table}` ORDER BY {order}", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(value => value switch
            {
                DBNull => "<null>",
                byte[] bytes => Convert.ToHexString(bytes),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            })));
        }
        return [.. rows];
    }

    private static async Task<string[]> TriggersAsync(MySqlConnection connection, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT CONCAT_WS('|', TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION,
                             ACTION_ORDER, SQL_MODE, ACTION_STATEMENT)
            FROM information_schema.TRIGGERS
            WHERE TRIGGER_SCHEMA = DATABASE()
            ORDER BY TRIGGER_NAME
            """, cancellationToken: ct))).ToArray();
}
