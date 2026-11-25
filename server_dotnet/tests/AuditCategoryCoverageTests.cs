using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>
/// Fast, zero-DB guard ensuring every collaboration-domain audit action emitted under Yf.Api
/// is mapped in <see cref="SystemService.Categories"/> so category filters never hide events.
/// </summary>
public sealed class AuditCategoryCoverageTests
{
    private static readonly (Regex Pattern, int ActionArgIndex, bool RequiresStringActionDeclaration)[] CallPatterns =
    [
        (new Regex(@"(?:\baudit|new\s+AuditService\([^)]*\))\s*\.\s*WriteAsync\s*\(", RegexOptions.Compiled), 3, false),
        (new Regex(@"\bnew\s+AuditWrite\s*\(", RegexOptions.Compiled), 0, false),
        (new Regex(@"\bAuditBestEffortAsync\s*\(", RegexOptions.Compiled), 3, false),
        (new Regex(@"\bWriteAuditAsync\s*\(", RegexOptions.Compiled), 2, true),
        (new Regex(@"\bWriteWindowedAuditAsync\s*\(", RegexOptions.Compiled), 3, false),
        (new Regex(@"\bWriteSessionAuditAsync\s*\(", RegexOptions.Compiled), 3, false),
        (new Regex(@"\bAuditDownloadEnd\s*\(", RegexOptions.Compiled), 1, false),
        (new Regex(@"\bAuditAction\s*\(", RegexOptions.Compiled), 1, false),
    ];

    private static readonly Regex WriteAuditWithActionDeclaration =
        new(@"\bWriteAuditAsync\s*\([^)]*\bstring\s+action\b", RegexOptions.Compiled);
    private static readonly Regex AuditLogAction =
        new(@"new\s+AuditLog\s*\{[^}]*\bAction\s*=\s*""([A-Z][A-Z0-9_]+)""", RegexOptions.Compiled);
    private static readonly Regex ActionLiteral = new(@"""([A-Z][A-Z0-9_]+)""", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    [Fact]
    public void EveryCollaborationAuditActionIsMappedInSystemCategories()
    {
        var apiRoot = ApiSourceRoot();
        Assert.True(Directory.Exists(apiRoot), $"Could not locate Yf.Api source root at {apiRoot}");

        var discovered = ExtractCollaborationAuditActions(apiRoot);
        Assert.True(discovered.Count >= 100, $"Expected at least 100 collaboration audit actions, found {discovered.Count}.");

        var categoryActions = SystemService.Categories.Values
            .SelectMany(actions => actions)
            .ToHashSet(StringComparer.Ordinal);

        var missing = discovered
            .Where(pair => !categoryActions.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} ({string.Join(", ", pair.Value)})")
            .ToArray();
        Assert.True(missing.Length == 0,
            "Collaboration audit actions missing from SystemService.Categories:\n" + string.Join('\n', missing));

        Assert.Contains("DEPT_LEADER_CHANGE", SystemService.Categories["ORG"]);
        foreach (var outsourceAction in new[]
        {
            "OUTSOURCE_ITEM_COMPLETE",
            "OUTSOURCE_ITEM_REOPEN",
            "OUTSOURCE_ITEM_SKIP_NOTICE",
            "OUTSOURCE_FIELD_UPDATE",
            "OUTSOURCE_FIELD_STATUS",
            "OUTSOURCE_FIELD_ORDER",
            "OUTSOURCE_SYNC_SCHEDULE",
            "OUTSOURCE_MES_MIRROR",
            "OUTSOURCE_MES_MIRROR_SETTINGS",
            "OUTSOURCE_MES_MIRROR_SCHEDULE",
        })
        {
            Assert.Contains(outsourceAction, SystemService.Categories["OUTSOURCE"]);
        }

        // Vendor-portal handoff actions are written via OemAuditWriter (ActorRealm="oem") and belong to OEM audit.
        Assert.DoesNotContain("OUTSOURCE_ELECTRICAL_HANDOFF", categoryActions);
        Assert.DoesNotContain("OUTSOURCE_ELECTRICAL_HANDOFF_WITHDRAW", categoryActions);
        Assert.DoesNotContain(categoryActions, action => AuditScopes.IsOemAction(action));

        foreach (var (category, actions) in SystemService.Categories)
        {
            Assert.Equal(actions.Length, actions.Distinct(StringComparer.Ordinal).Count());
            Assert.True(actions.Length > 0, $"Category {category} must not be empty.");
        }
    }

    private static SortedDictionary<string, SortedSet<string>> ExtractCollaborationAuditActions(string apiRoot)
    {
        var actions = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace('\\', '/');
            if (relative.StartsWith("Infrastructure/Migrations/", StringComparison.Ordinal)
                || relative.StartsWith("Modules/Oem/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal))
                continue;

            var raw = File.ReadAllText(file);
            if (raw.Contains("OemAuditWriter", StringComparison.Ordinal)
                && !raw.Contains("AuditService", StringComparison.Ordinal))
                continue;

            var text = StripComments(raw);
            foreach (Match match in AuditLogAction.Matches(text))
                Record(actions, match.Groups[1].Value, relative);

            var hasWriteAuditAction = WriteAuditWithActionDeclaration.IsMatch(text);
            foreach (var (pattern, argIndex, requiresDecl) in CallPatterns)
            {
                if (requiresDecl && !hasWriteAuditAction) continue;
                foreach (Match match in pattern.Matches(text))
                {
                    var args = SplitArguments(text, match.Index + match.Length - 1);
                    if (args.Count <= argIndex) continue;
                    var expression = args[argIndex];
                    if (expression.StartsWith("string ", StringComparison.Ordinal)
                        || expression.StartsWith("object ", StringComparison.Ordinal)
                        || expression.StartsWith("new ", StringComparison.Ordinal)
                        || expression.StartsWith("new{", StringComparison.Ordinal))
                        continue;

                    var literals = ActionLiteral.Matches(expression).Select(m => m.Groups[1].Value).ToArray();
                    if (literals.Length > 0)
                    {
                        foreach (var value in literals) Record(actions, value, relative);
                        continue;
                    }

                    if (!Identifier.IsMatch(expression)) continue;
                    var assignment = new Regex($@"\b{Regex.Escape(expression)}\s*=\s*([^;]+);");
                    foreach (Match assignMatch in assignment.Matches(text))
                    {
                        var rhs = assignMatch.Groups[1].Value;
                        foreach (Match literalMatch in ActionLiteral.Matches(rhs))
                        {
                            var value = literalMatch.Groups[1].Value;
                            // Skip switch-arm match patterns on the left of `=>`.
                            if (Regex.IsMatch(rhs, $@"""{Regex.Escape(value)}""\s*=>")) continue;
                            Record(actions, value, relative);
                        }
                    }
                }
            }
        }
        return actions;
    }

    private static void Record(SortedDictionary<string, SortedSet<string>> actions, string action, string file)
    {
        if (AuditScopes.IsOemAction(action)) return;
        if (!actions.TryGetValue(action, out var files))
        {
            files = new SortedSet<string>(StringComparer.Ordinal);
            actions[action] = files;
        }
        files.Add(file);
    }

    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length;)
        {
            var c = source[i];
            if (c == '"')
            {
                var start = i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { i += 2; continue; }
                    if (source[i++] == '"') break;
                }
                output.Append(source, start, i - start);
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    private static List<string> SplitArguments(string text, int openParenIndex)
    {
        var args = new List<string>();
        var depth = 0;
        var start = openParenIndex + 1;
        var inString = false;
        var inChar = false;
        for (var i = openParenIndex + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    args.Add(text[start..i].Trim());
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        return args;
    }

    private static string ApiSourceRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "Yf.Api"));
}
