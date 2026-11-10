using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Yf.Api.Infrastructure;

/// <summary>Background fill of a whole row or a single cell.</summary>
public enum XlsxFill
{
    None = 0,
    Red = 2,
    Gray = 3,
    Yellow = 4,
}

/// <summary>One data row: text cells, an optional row fill, and cells that get their own fill on top of it.</summary>
public sealed record XlsxRow(IReadOnlyList<string?> Cells, XlsxFill RowFill = XlsxFill.None,
    IReadOnlySet<int>? HighlightedCells = null, XlsxFill HighlightFill = XlsxFill.Yellow);

/// <summary>
/// Writes a single-sheet .xlsx with text cells, a bold header and solid fills, using only the framework's zip
/// support. Every value is written as an inline string, so nothing is ever evaluated as a formula.
/// </summary>
public static class SimpleXlsx
{
    // Style index = fill index for data cells (cellXfs 0..4); header is cellXfs 5.
    private const int HeaderStyle = 5;

    public static byte[] Write(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<XlsxRow> rows,
        IReadOnlyList<double>? columnWidths = null)
    {
        if (string.IsNullOrWhiteSpace(sheetName) || sheetName.Length > 31) throw new ArgumentException("Invalid sheet name.", nameof(sheetName));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="5"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFC7CE"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFD9D9D9"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="6"><xf fillId="0"/><xf fillId="1"/><xf fillId="2" applyFill="1"/><xf fillId="3" applyFill="1"/><xf fillId="4" applyFill="1"/><xf fontId="1" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                """);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(headers, rows, columnWidths));
        }
        return buffer.ToArray();
    }

    private static string Sheet(IReadOnlyList<string> headers, IReadOnlyList<XlsxRow> rows, IReadOnlyList<double>? widths)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (widths is { Count: > 0 })
        {
            xml.Append("<cols>");
            for (var i = 0; i < widths.Count; i++)
                xml.Append(System.FormattableString.Invariant($"""<col min="{i + 1}" max="{i + 1}" width="{widths[i]:0.##}" customWidth="1"/>"""));
            xml.Append("</cols>");
        }
        xml.Append("<sheetData>");
        AppendRow(xml, 1, headers, _ => HeaderStyle);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            AppendRow(xml, index + 2, row.Cells, column =>
                row.HighlightedCells?.Contains(column) == true ? (int)row.HighlightFill : (int)row.RowFill);
        }
        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    private static void AppendRow(StringBuilder xml, int rowNumber, IReadOnlyList<string?> cells, Func<int, int> style)
    {
        xml.Append("<row r=\"").Append(rowNumber).Append("\">");
        for (var column = 0; column < cells.Count; column++)
        {
            var reference = ColumnName(column) + rowNumber;
            var styleIndex = style(column);
            xml.Append("<c r=\"").Append(reference).Append('"');
            if (styleIndex != 0) xml.Append(" s=\"").Append(styleIndex).Append('"');
            var value = cells[column];
            if (string.IsNullOrEmpty(value)) { xml.Append("/>"); continue; }
            xml.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Escape(value)).Append("</t></is></c>");
        }
        xml.Append("</row>");
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            // Characters XML 1.0 cannot carry are dropped instead of producing a file Excel refuses to open.
            if (!XmlConvert.IsXmlChar((char)Math.Min(rune.Value, 0xFFFF)) && rune.Value <= 0xFFFF) continue;
            switch (rune.Value)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                default: builder.Append(rune.ToString()); break;
            }
        }
        return builder.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content.Trim());
        stream.Write(bytes);
    }
}
