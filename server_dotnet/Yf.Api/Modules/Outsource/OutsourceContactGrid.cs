using System.Text.RegularExpressions;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Outsource;

internal static partial class OutsourceContactGrid
{
    internal sealed record ParsedPersonToken(string? PrefixLabel, string Name, string Phone);

    internal static void EnsureRowAntiLeak(
        string visibility,
        IReadOnlyCollection<ulong> vendorIds,
        IEnumerable<OutsourceContactPerson> assignedPeople,
        IReadOnlyDictionary<ulong, string> vendorNames)
    {
        if (visibility == ContactRowVisibilities.InternalOnly) return;

        foreach (var person in assignedPeople)
        {
            if (person.ScopeType != ContactPersonScopes.Vendor || person.VendorId is not { } ownVendorId) continue;
            var ownName = vendorNames.GetValueOrDefault(ownVendorId) ?? $"厂商#{ownVendorId}";
            if (visibility != ContactRowVisibilities.SpecificVendors)
            {
                throw ApiException.BadRequest(
                    $"防泄密拦截：该行包含【{ownName}】的联系人「{person.Name}」，不能设为“全部外包可见”，请改为“仅指定外包：{ownName}”或“仅内部可见”");
            }
            var otherVendors = vendorIds.Where(id => id != ownVendorId).Distinct().ToArray();
            if (otherVendors.Length > 0)
            {
                var otherText = string.Join("、", otherVendors.Select(id => vendorNames.GetValueOrDefault(id) ?? $"厂商#{id}"));
                throw ApiException.BadRequest(
                    $"防泄密拦截：该行包含【{ownName}】的联系人「{person.Name}」，不能同时开放给【{otherText}】");
            }
        }
    }

    internal static byte[] BuildInternalWorkbook(
        IReadOnlyList<OutsourceContactColumnResponse> columns,
        IReadOnlyList<OutsourceContactRowResponse> rows,
        IReadOnlyDictionary<ulong, string> vendorNames)
    {
        var visualCols = ExpandVisualColumns(columns, includeVisibilityColumn: true);
        return BuildWorkbookCore("对接联络表", visualCols, rows.Select(row => new ExportRowAdapter(
            row.Visibility,
            row.VendorIds,
            row.Values,
            row.People.ToDictionary(
                p => p.Key,
                p => p.Value.Select(v => (v.PrefixLabel, v.Name, v.Phone)).ToArray(),
                StringComparer.Ordinal)
        )).ToArray(), vendorNames);
    }

    internal static byte[] BuildVendorWorkbook(
        IReadOnlyList<OemContactColumnResponse> columns,
        IReadOnlyList<OemContactRowResponse> rows)
    {
        var adaptedCols = columns.Select(c => new OutsourceContactColumnResponse(
            c.Id, c.ColKey, c.Title, c.DataType, c.PersonDisplay, c.PhoneTitle,
            c.AutoMerge, true, c.IsGroupColumn, c.SortNo, c.WidthHint, AccountStatuses.Active)).ToArray();
        var visualCols = ExpandVisualColumns(adaptedCols, includeVisibilityColumn: false);
        return BuildWorkbookCore("对接联络表", visualCols, rows.Select(row => new ExportRowAdapter(
            ContactRowVisibilities.AllVendors,
            [],
            row.Values,
            row.People.ToDictionary(
                p => p.Key,
                p => p.Value.Select(v => (v.PrefixLabel, v.Name, v.Phone)).ToArray(),
                StringComparer.Ordinal)
        )).ToArray(), new Dictionary<ulong, string>());
    }

    internal static byte[] BuildTemplateWorkbook(IReadOnlyList<OutsourceContactColumnResponse> columns)
    {
        var visualCols = ExpandVisualColumns(columns, includeVisibilityColumn: true);
        var headers = visualCols.Select(v => v.Header).ToArray();
        var widths = visualCols.Select(v => v.Width).ToArray();
        var sampleCells = visualCols.Select(v => v.Kind switch
        {
            VisualKind.Visibility => "全部外包（可填：全部外包 / 仅内部 / 厂商A、厂商B）",
            VisualKind.Text => v.Col!.IsGroupColumn ? "示例分组（导入时自动忽略本范例行）" : "示例内容",
            VisualKind.Multiline => "1.示例要求一\n2.示例要求二",
            VisualKind.PersonCombined => "示例前缀：示例人员 13800000001",
            VisualKind.PersonName => "示例人员",
            VisualKind.PersonPhone => "13800000001",
            _ => "",
        }).ToArray();
        var centered = visualCols.Select((v, i) => (v, i)).Where(x => x.v.Centered).Select(x => x.i).ToHashSet();
        return SimpleXlsx.Write("对接联络表模板", headers, [new XlsxRow(sampleCells)], widths, null, gridBorders: true, centeredColumns: centered);
    }

    private enum VisualKind { Text, Multiline, PersonCombined, PersonName, PersonPhone, Visibility }

    private sealed record VisualColumn(
        OutsourceContactColumnResponse? Col,
        VisualKind Kind,
        string Header,
        bool AutoMerge,
        bool IsGroupColumn,
        double Width,
        bool Centered);

    private sealed record ExportRowAdapter(
        string Visibility,
        ulong[] VendorIds,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, (string? PrefixLabel, string Name, string Phone)[]> People);

    private static VisualColumn[] ExpandVisualColumns(IReadOnlyList<OutsourceContactColumnResponse> columns, bool includeVisibilityColumn)
    {
        var list = new List<VisualColumn>();
        foreach (var col in columns.OrderBy(c => c.SortNo).ThenBy(c => c.Id))
        {
            if (col.DataType == ContactColumnTypes.Person && col.PersonDisplay == ContactPersonDisplays.Split)
            {
                var phoneTitle = string.IsNullOrWhiteSpace(col.PhoneTitle) ? $"{col.Title}电话" : col.PhoneTitle.Trim();
                list.Add(new VisualColumn(col, VisualKind.PersonName, col.Title, col.AutoMerge, col.IsGroupColumn, Math.Max(14, col.WidthHint), true));
                list.Add(new VisualColumn(col, VisualKind.PersonPhone, phoneTitle, col.AutoMerge, col.IsGroupColumn, 18, true));
            }
            else if (col.DataType == ContactColumnTypes.Person)
            {
                list.Add(new VisualColumn(col, VisualKind.PersonCombined, col.Title, col.AutoMerge, col.IsGroupColumn, Math.Max(18, col.WidthHint), false));
            }
            else if (col.DataType == ContactColumnTypes.Multiline)
            {
                list.Add(new VisualColumn(col, VisualKind.Multiline, col.Title, col.AutoMerge, col.IsGroupColumn, Math.Max(26, col.WidthHint), false));
            }
            else
            {
                list.Add(new VisualColumn(col, VisualKind.Text, col.Title, col.AutoMerge, col.IsGroupColumn, Math.Max(14, col.WidthHint), true));
            }
        }
        if (includeVisibilityColumn)
        {
            list.Add(new VisualColumn(null, VisualKind.Visibility, "可见范围", false, false, 24, false));
        }
        return [.. list];
    }

    private static byte[] BuildWorkbookCore(
        string sheetName,
        VisualColumn[] visualCols,
        ExportRowAdapter[] rows,
        IReadOnlyDictionary<ulong, string> vendorNames)
    {
        var groupCol = visualCols.FirstOrDefault(v => v.IsGroupColumn)?.Col;
        var rowCount = rows.Length;
        var colCount = visualCols.Length;
        var cellValues = new string?[rowCount, colCount];
        var mergeKeys = new string?[rowCount, colCount];
        var groupKeys = new string[rowCount];

        for (var r = 0; r < rowCount; r++)
        {
            var row = rows[r];
            groupKeys[r] = groupCol is null ? "" : (row.Values.GetValueOrDefault(groupCol.ColKey) ?? "").Trim();
            for (var c = 0; c < colCount; c++)
            {
                var vc = visualCols[c];
                if (vc.Kind == VisualKind.Visibility)
                {
                    cellValues[r, c] = row.Visibility switch
                    {
                        ContactRowVisibilities.AllVendors => "全部外包",
                        ContactRowVisibilities.SpecificVendors => string.Join("、", row.VendorIds.Select(id => vendorNames.GetValueOrDefault(id) ?? $"厂商#{id}")),
                        _ => "仅内部",
                    };
                    continue;
                }
                var col = vc.Col!;
                if (vc.Kind is VisualKind.Text or VisualKind.Multiline)
                {
                    var text = row.Values.GetValueOrDefault(col.ColKey) ?? "";
                    cellValues[r, c] = text;
                    if (vc.AutoMerge && !string.IsNullOrWhiteSpace(text))
                        mergeKeys[r, c] = "T:" + text.Trim();
                }
                else
                {
                    var people = row.People.GetValueOrDefault(col.ColKey) ?? [];
                    if (people.Length == 0)
                    {
                        cellValues[r, c] = "";
                        continue;
                    }
                    cellValues[r, c] = vc.Kind switch
                    {
                        VisualKind.PersonCombined => string.Join("\n", people.Select(p =>
                            string.IsNullOrWhiteSpace(p.PrefixLabel) ? $"{p.Name} {p.Phone}" : $"{p.PrefixLabel}：{p.Name} {p.Phone}")),
                        VisualKind.PersonName => string.Join(" / ", people.Select(p =>
                            string.IsNullOrWhiteSpace(p.PrefixLabel) ? p.Name : $"{p.PrefixLabel}：{p.Name}")),
                        VisualKind.PersonPhone => people.Length == 1
                            ? people[0].Phone
                            : string.Join("\n", people.Select(p => $"{p.Name}：{p.Phone}")),
                        _ => "",
                    };
                    if (vc.AutoMerge)
                    {
                        mergeKeys[r, c] = "P:" + string.Join("|", people.Select(p => $"{p.Name}:{p.Phone}:{(p.PrefixLabel ?? "").Trim()}"));
                    }
                }
            }
        }

        var merges = new List<XlsxMerge>();
        for (var c = 0; c < colCount; c++)
        {
            var vc = visualCols[c];
            if (!vc.AutoMerge) continue;
            var r = 0;
            while (r < rowCount)
            {
                var key = mergeKeys[r, c];
                if (key is null) { r++; continue; }
                var span = 1;
                while (r + span < rowCount
                    && mergeKeys[r + span, c] == key
                    && (vc.IsGroupColumn || groupKeys[r + span] == groupKeys[r]))
                {
                    cellValues[r + span, c] = null;
                    span++;
                }
                if (span > 1)
                {
                    // Excel worksheet row 1 is header, so data row r starts at r + 2.
                    merges.Add(new XlsxMerge(r + 2, c, r + span + 1, c));
                }
                r += span;
            }
        }

        var xlsxRows = new List<XlsxRow>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var line = new string?[colCount];
            for (var c = 0; c < colCount; c++) line[c] = cellValues[r, c];
            xlsxRows.Add(new XlsxRow(line));
        }

        var headers = visualCols.Select(v => v.Header).ToArray();
        var widths = visualCols.Select(v => v.Width).ToArray();
        var centered = visualCols.Select((v, i) => (v, i)).Where(x => x.v.Centered).Select(x => x.i).ToHashSet();
        return SimpleXlsx.Write(sheetName, headers, xlsxRows, widths, merges, gridBorders: true, centeredColumns: centered);
    }

    internal static (string? Error, IReadOnlyList<ParsedPersonToken> Tokens) ParsePersonCell(
        string personDisplay,
        string? rawText,
        string? rawPhonePairText)
    {
        if (personDisplay == ContactPersonDisplays.Split)
            return ParseSplitPersonCell(rawText, rawPhonePairText);
        return ParseCombinedPersonCell(rawText);
    }

    private static (string? Error, IReadOnlyList<ParsedPersonToken> Tokens) ParseCombinedPersonCell(string? rawText)
    {
        var lines = (rawText ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line is not ("-" or "—"))
            .ToArray();
        if (lines.Length == 0) return (null, []);

        var tokens = new List<ParsedPersonToken>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (LooksLikePhoneOnly(line))
            {
                return ($"电话「{line}」缺少对应的姓名", []);
            }

            var (prefix, body) = SplitPrefix(line);
            var phoneMatch = MatchTrailingPhone(body);
            if (phoneMatch.Success)
            {
                var name = body[..phoneMatch.Index].Trim(' ', '\t', '：', ':', '/', '|', '(', ')', '（', '）');
                var phone = NormalizePhone(phoneMatch.Value);
                if (string.IsNullOrWhiteSpace(name))
                    return ($"「{line}」缺少人员姓名", []);
                tokens.Add(new ParsedPersonToken(prefix, name, phone));
                continue;
            }

            // Support two-line single-person format: "机构：陈某\n13800000001"
            if (i + 1 < lines.Length && LooksLikePhoneOnly(lines[i + 1]))
            {
                var name = body.Trim(' ', '\t', '：', ':');
                if (string.IsNullOrWhiteSpace(name))
                    return ($"「{line}」缺少人员姓名", []);
                tokens.Add(new ParsedPersonToken(prefix, name, NormalizePhone(lines[i + 1])));
                i++;
                continue;
            }

            return ($"「{line}」缺少联系电话（请按“姓名 电话”或“前缀：姓名 电话”填写）", []);
        }

        return (null, tokens);
    }

    private static (string? Error, IReadOnlyList<ParsedPersonToken> Tokens) ParseSplitPersonCell(
        string? rawNames,
        string? rawPhones)
    {
        var nameEntries = (rawNames ?? "")
            .Split(['\r', '\n', '/', '、', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x is not ("-" or "—"))
            .ToArray();
        var phoneLines = (rawPhones ?? "")
            .Split(['\r', '\n', '/', '、', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x is not ("-" or "—"))
            .ToArray();

        if (nameEntries.Length == 0 && phoneLines.Length == 0) return (null, []);
        if (nameEntries.Length == 0) return ("电话列有内容，但姓名列为空", []);

        var namedPhones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionalPhones = new List<string>();
        foreach (var entry in phoneLines)
        {
            var colon = entry.IndexOfAny(['：', ':']);
            if (colon > 0)
            {
                var who = entry[..colon].Trim();
                var num = NormalizePhone(entry[(colon + 1)..]);
                if (!LooksLikePhoneOnly(num)) return ($"电话格式不正确：「{entry}」", []);
                namedPhones[who] = num;
            }
            else
            {
                var num = NormalizePhone(entry);
                if (!LooksLikePhoneOnly(num)) return ($"电话格式不正确：「{entry}」", []);
                positionalPhones.Add(num);
            }
        }

        var tokens = new List<ParsedPersonToken>(nameEntries.Length);
        var posIdx = 0;
        foreach (var raw in nameEntries)
        {
            var (prefix, namePart) = SplitPrefix(raw);
            var inlinePhone = MatchTrailingPhone(namePart);
            if (inlinePhone.Success)
            {
                var cleanName = namePart[..inlinePhone.Index].Trim();
                tokens.Add(new ParsedPersonToken(prefix, cleanName, NormalizePhone(inlinePhone.Value)));
                continue;
            }

            var name = namePart.Trim();
            if (string.IsNullOrWhiteSpace(name)) return ("人员姓名不能为空", []);
            string phone;
            if (namedPhones.TryGetValue(name, out var matched))
                phone = matched;
            else if (posIdx < positionalPhones.Count)
                phone = positionalPhones[posIdx++];
            else
                return ($"「{name}」未匹配到对应的联系电话", []);

            tokens.Add(new ParsedPersonToken(prefix, name, phone));
        }

        return (null, tokens);
    }

    private static (string? Prefix, string Body) SplitPrefix(string text)
    {
        var idx = text.IndexOfAny(['：', ':']);
        if (idx <= 0) return (null, text.Trim());
        var left = text[..idx].Trim();
        var right = text[(idx + 1)..].Trim();
        if (left.Length > 32 || right.Length == 0) return (null, text.Trim());
        // If right side is just a phone number, left side is the person's name, not a role prefix.
        if (LooksLikePhoneOnly(right)) return (null, text.Trim());
        return (left, right);
    }

    private static bool LooksLikePhoneOnly(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 5 or > 32) return false;
        var digits = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiDigit(ch)) digits++;
            else if (ch is not ('-' or ' ' or '+')) return false;
        }
        return digits >= 5;
    }

    internal static string NormalizePhone(string value) => value.Trim();

    private static Match MatchTrailingPhone(string text)
    {
        var delimited = DelimitedTrailingPhoneRegex().Match(text);
        if (delimited.Success && LooksLikePhoneOnly(delimited.Value)) return delimited;
        var compact = CompactTrailingPhoneRegex().Match(text);
        if (compact.Success && LooksLikePhoneOnly(compact.Value)) return compact;
        return Match.Empty;
    }

    [GeneratedRegex(@"(?<=[\s:：/|（(])(?:\+\d[\d\-\s]{3,26}\d|\d{2,}[\d\-\s]{2,25}\d|\d[\d\-]{3,26}\d)$")]
    private static partial Regex DelimitedTrailingPhoneRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\d\-]{3,26}\d)$")]
    private static partial Regex CompactTrailingPhoneRegex();
}
