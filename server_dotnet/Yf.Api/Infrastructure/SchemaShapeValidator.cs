using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

internal enum SchemaShapeValidationMode
{
    Strict,
    LegacyV16BeforeSessionMigration
}

/// <summary>Read-only comparison of the embedded authoritative DDL with the current MySQL schema.</summary>
internal static class SchemaShapeValidator
{
    private const string BaselineResource = "Yf.Api.Infrastructure.schema-baseline.json";
    private static readonly Regex IntegerDisplayWidth = new(@"\b(tinyint|smallint|mediumint|int|integer|bigint)\(\d+\)", RegexOptions.Compiled);
    private static readonly Regex QuotedNumericDefault = new(@"\bdefault '(-?\d+(?:\.\d+)?)'", RegexOptions.Compiled);
    private static readonly Regex EngineOption = new(@"\bengine\s*=\s*([a-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CharsetOption = new(@"\b(?:default\s+)?charset\s*=\s*([a-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CollationOption = new(@"\bcollate\s*=\s*([a-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColumnCollation = new(@"\s+collate\s+([a-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColumnCharset = new(@"\s+character\s+set\s+([a-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForeignKey = new(
        @"^constraint\s+`(?<name>[^`]+)`\s+foreign key\s*\((?<local>[^)]*)\)\s+references\s+`(?<table>[^`]+)`\s*\((?<target>[^)]*)\)(?<rules>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteRule = new(@"\bon delete\s+(restrict|cascade|set null|no action)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UpdateRule = new(@"\bon update\s+(restrict|cascade|set null|no action)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static async Task ValidateBaselineAsync(
        MySqlConnection conn,
        SchemaShapeValidationMode mode,
        CancellationToken ct)
    {
        foreach (var expected in LoadBaseline())
            await ValidateTableAsync(conn, expected, mode, ct);
    }

    internal static Task ValidateTableAsync(
        MySqlConnection conn,
        string tableName,
        string expectedSql,
        CancellationToken ct) =>
        ValidateTableAsync(conn, ParseTable(tableName, expectedSql), SchemaShapeValidationMode.Strict, ct);

    private static IReadOnlyList<TableShape> LoadBaseline()
    {
        using var resource = typeof(SchemaBootstrap).Assembly.GetManifestResourceStream(BaselineResource)
            ?? throw new InvalidOperationException("Embedded schema baseline missing.");
        using var baseline = JsonDocument.Parse(resource);
        return baseline.RootElement.GetProperty("tables").EnumerateArray()
            .Select(table => ParseTable(
                table.GetProperty("name").GetString()!,
                table.GetProperty("sql").GetString()!))
            .ToArray();
    }

    private static async Task ValidateTableAsync(
        MySqlConnection conn,
        TableShape expected,
        SchemaShapeValidationMode mode,
        CancellationToken ct)
    {
        if (!await HasTableAsync(conn, expected.Name, ct))
            throw Mismatch(expected.Name, "required table is missing");

        var actual = ParseTable(expected.Name, await ReadCreateTableAsync(conn, expected.Name, ct));
        if (!string.Equals(actual.Engine, expected.Engine, StringComparison.OrdinalIgnoreCase))
            throw Mismatch(expected.Name, $"engine is {actual.Engine}, expected {expected.Engine}");
        if (expected.Charset is not null && !string.Equals(actual.Charset, expected.Charset, StringComparison.OrdinalIgnoreCase))
            throw Mismatch(expected.Name, $"character set is {actual.Charset}, expected {expected.Charset}");
        // A baseline that omits COLLATE deliberately inherits the server/database default.
        if (expected.Collation is not null && !string.Equals(actual.Collation, expected.Collation, StringComparison.OrdinalIgnoreCase))
            throw Mismatch(expected.Name, $"collation is {actual.Collation}, expected {expected.Collation}");

        foreach (var expectedColumn in expected.Columns.Values)
        {
            var isLegacySession = mode == SchemaShapeValidationMode.LegacyV16BeforeSessionMigration
                && expected.Name.Equals("refresh_tokens", StringComparison.OrdinalIgnoreCase)
                && expectedColumn.Name.Equals("session_id", StringComparison.OrdinalIgnoreCase);
            if (!actual.Columns.TryGetValue(expectedColumn.Name, out var actualColumn))
            {
                if (isLegacySession) continue;
                throw Mismatch(expected.Name, $"required column {expectedColumn.Name} is missing");
            }

            if (!ColumnsMatch(actualColumn, expectedColumn, isLegacySession))
                throw Mismatch(expected.Name, $"column {expectedColumn.Name} has unsupported definition '{actualColumn.Definition}'");
        }

        foreach (var actualColumn in actual.Columns.Values.Where(column => !expected.Columns.ContainsKey(column.Name)))
        {
            if (!IsHarmlessExtraColumn(actualColumn.Definition))
                throw Mismatch(expected.Name, $"extra column {actualColumn.Name} can block existing inserts");
        }

        if (expected.PrimaryKey is null || actual.PrimaryKey is null
            || !expected.PrimaryKey.SemanticallyEquals(actual.PrimaryKey))
            throw Mismatch(expected.Name, "primary key columns or index method differ from the baseline");

        var unmatched = actual.Indexes.ToList();
        foreach (var expectedIndex in expected.Indexes)
        {
            var legacySessionIndex = mode == SchemaShapeValidationMode.LegacyV16BeforeSessionMigration
                && expected.Name.Equals("refresh_tokens", StringComparison.OrdinalIgnoreCase)
                && expectedIndex.Name.Equals("idx_refresh_tokens_session_state", StringComparison.OrdinalIgnoreCase);
            if (legacySessionIndex)
            {
                var sameName = unmatched.FirstOrDefault(index => index.Name.Equals(expectedIndex.Name, StringComparison.OrdinalIgnoreCase));
                if (sameName is not null && !sameName.SemanticallyEquals(expectedIndex))
                    throw Mismatch(expected.Name, $"index {expectedIndex.Name} already exists with unsupported columns");
            }

            var match = unmatched.FirstOrDefault(index => index.SemanticallyEquals(expectedIndex));
            if (match is not null)
            {
                unmatched.Remove(match);
                continue;
            }
            if (legacySessionIndex) continue;
            throw Mismatch(expected.Name, $"required {(expectedIndex.Unique ? "unique " : string.Empty)}index {expectedIndex.Name} is missing or reordered");
        }

        var unexpectedUnique = unmatched.FirstOrDefault(index => index.Unique);
        if (unexpectedUnique is not null)
            throw Mismatch(expected.Name, $"unexpected unique index {unexpectedUnique.Name} changes write semantics");

        var expectedForeignKeys = expected.ForeignKeys.Select(key => key.SemanticKey).ToHashSet(StringComparer.Ordinal);
        var actualForeignKeys = actual.ForeignKeys.Select(key => key.SemanticKey).ToHashSet(StringComparer.Ordinal);
        if (!expectedForeignKeys.SetEquals(actualForeignKeys))
            throw Mismatch(expected.Name, "foreign key columns, target, or update/delete rules differ from the baseline");

        if (actual.OtherConstraints.Count != expected.OtherConstraints.Count
            || !actual.OtherConstraints.Order().SequenceEqual(expected.OtherConstraints.Order(), StringComparer.Ordinal))
            throw Mismatch(expected.Name, "check or other table constraints differ from the baseline");
    }

    private static bool ColumnsMatch(ColumnShape actual, ColumnShape expected, bool allowLegacyNullableSession)
    {
        if (expected.Charset is not null && !string.Equals(actual.Charset, expected.Charset, StringComparison.OrdinalIgnoreCase))
            return false;
        if (expected.Collation is not null && !string.Equals(actual.Collation, expected.Collation, StringComparison.OrdinalIgnoreCase))
            return false;
        if (actual.Definition == expected.Definition) return true;
        if (!allowLegacyNullableSession) return false;
        var nullableExpected = expected.Definition.EndsWith(" not null", StringComparison.Ordinal)
            ? expected.Definition[..^" not null".Length] + " default null"
            : expected.Definition;
        return actual.Definition == nullableExpected;
    }

    private static bool IsHarmlessExtraColumn(string definition) =>
        !definition.Contains(" not null", StringComparison.Ordinal)
        || definition.Contains(" default ", StringComparison.Ordinal)
        || definition.Contains(" generated always as", StringComparison.Ordinal)
        || definition.Contains(" auto_increment", StringComparison.Ordinal);

    private static async Task<string> ReadCreateTableAsync(MySqlConnection conn, string tableName, CancellationToken ct)
    {
        if (tableName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
            throw new InvalidOperationException("Unsafe table name in schema definition.");
        await using var command = new MySqlCommand($"SHOW CREATE TABLE `{tableName}`", conn);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Mismatch(tableName, "SHOW CREATE TABLE returned no row");
        return reader.GetString(1);
    }

    private static TableShape ParseTable(string name, string sql)
    {
        var columns = new Dictionary<string, ColumnShape>(StringComparer.OrdinalIgnoreCase);
        var indexes = new List<IndexShape>();
        var foreignKeys = new List<ForeignKeyShape>();
        var otherConstraints = new List<string>();
        IndexShape? primaryKey = null;
        string? engine = null;
        string? charset = null;
        string? collation = null;

        foreach (var rawLine in sql.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd(',');
            if (line.Length == 0 || line.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith('`'))
            {
                var end = line.IndexOf('`', 1);
                if (end <= 1) throw new InvalidOperationException($"Cannot parse column in schema definition for {name}.");
                var columnName = line[1..end];
                columns.Add(columnName, new(columnName, CanonicalFragment(line[(end + 1)..])));
                continue;
            }
            if (line.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            {
                primaryKey = ParseIndex("PRIMARY", unique: true, line);
                continue;
            }
            if (line.StartsWith("UNIQUE KEY", StringComparison.OrdinalIgnoreCase))
            {
                indexes.Add(ParseNamedIndex(unique: true, line));
                continue;
            }
            if (line.StartsWith("KEY", StringComparison.OrdinalIgnoreCase))
            {
                indexes.Add(ParseNamedIndex(unique: false, line));
                continue;
            }
            if (line.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                var foreign = ParseForeignKey(line);
                if (foreign is not null) foreignKeys.Add(foreign);
                else otherConstraints.Add(CanonicalFragment(line));
                continue;
            }
            if (line.StartsWith(')'))
            {
                engine = EngineOption.Match(line).Groups[1].Value.NullIfEmpty();
                charset = CharsetOption.Match(line).Groups[1].Value.NullIfEmpty();
                collation = CollationOption.Match(line).Groups[1].Value.NullIfEmpty();
                continue;
            }
            otherConstraints.Add(CanonicalFragment(line));
        }

        if (engine is null) throw new InvalidOperationException($"Cannot parse table engine for {name}.");
        var effectiveColumns = columns.ToDictionary(
            pair => pair.Key,
            pair => NormalizeColumn(pair.Value, charset, collation),
            StringComparer.OrdinalIgnoreCase);
        return new(name, engine, charset, collation, effectiveColumns, primaryKey, indexes, foreignKeys, otherConstraints);
    }

    private static ColumnShape NormalizeColumn(ColumnShape column, string? tableCharset, string? tableCollation)
    {
        var charsetMatch = ColumnCharset.Match(column.Definition);
        var collationMatch = ColumnCollation.Match(column.Definition);
        var effectiveCollation = collationMatch.Success
            ? collationMatch.Groups[1].Value.ToLowerInvariant()
            : tableCollation;
        var effectiveCharset = charsetMatch.Success
            ? charsetMatch.Groups[1].Value.ToLowerInvariant()
            : tableCharset ?? CharsetFromCollation(effectiveCollation);
        var definition = ColumnCharset.Replace(column.Definition, string.Empty);
        definition = ColumnCollation.Replace(definition, string.Empty);
        return column with
        {
            Definition = CanonicalFragment(definition),
            Charset = effectiveCharset,
            Collation = effectiveCollation
        };
    }

    private static string? CharsetFromCollation(string? collation)
    {
        if (collation is null) return null;
        var separator = collation.IndexOf('_');
        return separator > 0 ? collation[..separator] : null;
    }

    private static IndexShape ParseNamedIndex(bool unique, string line)
    {
        var first = line.IndexOf('`');
        var second = first < 0 ? -1 : line.IndexOf('`', first + 1);
        if (first < 0 || second <= first) throw new InvalidOperationException("Cannot parse named index in schema definition.");
        return ParseIndex(line[(first + 1)..second], unique, line[(second + 1)..]);
    }

    private static IndexShape ParseIndex(string name, bool unique, string line)
    {
        var open = line.IndexOf('(');
        var close = FindMatchingParenthesis(line, open);
        if (open < 0 || close < 0) throw new InvalidOperationException($"Cannot parse index {name} in schema definition.");
        var suffix = CanonicalFragment(line[(close + 1)..]);
        var methodMatch = Regex.Match(suffix, @"\busing\s+([a-z0-9_]+)\b", RegexOptions.IgnoreCase);
        var method = methodMatch.Success ? methodMatch.Groups[1].Value.ToLowerInvariant() : "btree";
        var visible = !suffix.Contains("invisible", StringComparison.Ordinal);
        return new(name, unique, CanonicalFragment(line[(open + 1)..close]), method, visible);
    }

    private static ForeignKeyShape? ParseForeignKey(string line)
    {
        var canonical = CanonicalFragment(line);
        var match = ForeignKey.Match(canonical);
        if (!match.Success) return null;
        return new(
            match.Groups["name"].Value,
            CanonicalFragment(match.Groups["local"].Value),
            match.Groups["table"].Value.ToLowerInvariant(),
            CanonicalFragment(match.Groups["target"].Value),
            ParseRule(DeleteRule, match.Groups["rules"].Value),
            ParseRule(UpdateRule, match.Groups["rules"].Value));
    }

    private static string ParseRule(Regex regex, string rules)
    {
        var match = regex.Match(rules);
        if (!match.Success) return "restrict";
        var rule = match.Groups[1].Value.ToLowerInvariant();
        return rule == "no action" ? "restrict" : rule;
    }

    private static int FindMatchingParenthesis(string value, int open)
    {
        if (open < 0) return -1;
        var depth = 0;
        var quoted = false;
        for (var i = open; i < value.Length; i++)
        {
            if (value[i] == '\'' && (i == 0 || value[i - 1] != '\\')) quoted = !quoted;
            if (quoted) continue;
            if (value[i] == '(') depth++;
            else if (value[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static string CanonicalFragment(string value)
    {
        var builder = new StringBuilder(value.Length);
        var quoted = false;
        var identifier = false;
        var pendingSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (quoted)
            {
                builder.Append(ch);
                if (ch == '\'' && (i == 0 || value[i - 1] != '\\')) quoted = false;
                continue;
            }
            if (identifier)
            {
                builder.Append(char.ToLowerInvariant(ch));
                if (ch == '`') identifier = false;
                continue;
            }
            if (ch == '\'')
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                quoted = true;
                builder.Append(ch);
                continue;
            }
            if (ch == '`')
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                identifier = true;
                builder.Append(ch);
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (ch is '(' or ')' or ',')
            {
                while (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
                pendingSpace = false;
                builder.Append(ch);
                continue;
            }
            if (pendingSpace && builder.Length > 0 && builder[^1] is not '(' and not ',') builder.Append(' ');
            pendingSpace = false;
            builder.Append(char.ToLowerInvariant(ch));
        }

        var canonical = NormalizeOutsideStringLiterals(builder.ToString().Trim());
        canonical = NormalizeQuotedNumericDefaults(canonical);
        return NormalizeGeneratedExpression(canonical);
    }

    private static string NormalizeOutsideStringLiterals(string value)
    {
        var result = new StringBuilder(value.Length);
        var segmentStart = 0;
        var quoted = false;
        for (var i = 0; i <= value.Length; i++)
        {
            var atEnd = i == value.Length;
            if (!atEnd && value[i] != '\'') continue;
            if (!atEnd && i > 0 && value[i - 1] == '\\') continue;
            if (!quoted)
            {
                result.Append(NormalizeSqlTokens(value[segmentStart..i]));
                if (!atEnd)
                {
                    quoted = true;
                    segmentStart = i;
                }
            }
            else
            {
                if (!atEnd && i + 1 < value.Length && value[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                result.Append(value[segmentStart..(atEnd ? i : i + 1)]);
                quoted = false;
                segmentStart = i + 1;
            }
        }
        return result.ToString();
    }

    private static string NormalizeSqlTokens(string value)
    {
        value = IntegerDisplayWidth.Replace(value, "$1");
        return value.Replace("current_timestamp()", "current_timestamp", StringComparison.Ordinal);
    }

    private static string NormalizeQuotedNumericDefaults(string value)
    {
        return QuotedNumericDefault.Replace(value, match =>
        {
            var prefix = value[..match.Index];
            var quoteCount = prefix.Count(ch => ch == '\'');
            return quoteCount % 2 == 0 ? "default " + match.Groups[1].Value : match.Value;
        });
    }

    private static string NormalizeGeneratedExpression(string definition)
    {
        const string marker = "generated always as(";
        var markerAt = definition.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0) return definition;
        var open = markerAt + marker.Length - 1;
        var close = FindMatchingParenthesis(definition, open);
        if (close < 0) return definition;
        var expression = definition[(open + 1)..close];
        while (expression.StartsWith('(') && FindMatchingParenthesis(expression, 0) == expression.Length - 1)
            expression = expression[1..^1];
        return definition[..(open + 1)] + expression + definition[close..];
    }

    private static Task<bool> HasTableAsync(MySqlConnection conn, string table, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table AND table_type='BASE TABLE')",
            new { table }, cancellationToken: ct));

    private static InvalidOperationException Mismatch(string table, string detail) =>
        new($"Unsupported database schema for {table}: {detail}. No automatic repair was attempted.");

    private sealed record TableShape(
        string Name,
        string Engine,
        string? Charset,
        string? Collation,
        IReadOnlyDictionary<string, ColumnShape> Columns,
        IndexShape? PrimaryKey,
        IReadOnlyList<IndexShape> Indexes,
        IReadOnlyList<ForeignKeyShape> ForeignKeys,
        IReadOnlyList<string> OtherConstraints);

    private sealed record ColumnShape(
        string Name,
        string Definition,
        string? Charset = null,
        string? Collation = null);

    private sealed record IndexShape(string Name, bool Unique, string Columns, string Method, bool Visible)
    {
        internal bool SemanticallyEquals(IndexShape other) =>
            Unique == other.Unique && Columns == other.Columns && Method == other.Method && Visible == other.Visible;
    }

    private sealed record ForeignKeyShape(
        string Name,
        string LocalColumns,
        string TargetTable,
        string TargetColumns,
        string DeleteRule,
        string UpdateRule)
    {
        internal string SemanticKey => string.Join('|', LocalColumns, TargetTable, TargetColumns, DeleteRule, UpdateRule);
    }

    private static string? NullIfEmpty(this string value) => value.Length == 0 ? null : value.ToLowerInvariant();
}
