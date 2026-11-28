using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Announcements;

/// <summary>Accepts the editor's document schema and rebuilds only the content and attributes the readers support.</summary>
internal sealed record AnnouncementDocument(string? Json, string Text, ulong[] Images)
{
    private const int MaxText = 20_000;
    private const int MaxDocumentBytes = 512 * 1024;
    private static readonly JsonSerializerOptions StorageJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static AnnouncementDocument FromDraft(AnnouncementDraftRequest draft)
    {
        if (draft.DocumentJson is null)
        {
            var plain = draft.Body?.Trim() ?? "";
            if (plain.Length > MaxText) throw ApiException.BadRequest("公告正文最多 20000 字");
            return new(null, plain, []);
        }
        if (Encoding.UTF8.GetByteCount(draft.DocumentJson) > MaxDocumentBytes)
            throw ApiException.BadRequest("公告排版内容过大");
        try
        {
            using var parsed = JsonDocument.Parse(draft.DocumentJson, new JsonDocumentOptions { MaxDepth = 64 });
            var reader = new DocumentReader();
            var document = reader.Node(parsed.RootElement, null);
            return new(document.ToJsonString(StorageJson), reader.Text.ToString().Trim(), reader.Images.Distinct().ToArray());
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static ApiException Invalid() => ApiException.BadRequest("公告正文格式无效或包含不支持的内容");

    private sealed class DocumentReader
    {
        internal readonly StringBuilder Text = new();
        internal readonly List<ulong> Images = [];
        private int nodeCount;

        internal JsonObject Node(JsonElement source, string? parent)
        {
            if (++nodeCount > 5000) throw ApiException.BadRequest("公告排版节点过多");
            var type = String(source, "type") ?? throw Invalid();
            if (!AllowedChild(parent, type)) throw Invalid();
            var output = new JsonObject { ["type"] = type };
            var attrs = Property(source, "attrs");
            if (attrs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Null)) throw Invalid();
            switch (type)
            {
                case "text":
                    var value = String(source, "text");
                    if (string.IsNullOrEmpty(value)) throw Invalid();
                    output["text"] = value;
                    Append(value);
                    if (source.TryGetProperty("marks", out var marks)) output["marks"] = Marks(marks);
                    break;
                case "image":
                    var id = Property(attrs, "assetId");
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out var imageId) || imageId == 0)
                        throw ApiException.BadRequest("图片尚未上传完成");
                    var caption = String(attrs, "caption") ?? "";
                    if (caption.Length > 1000) throw ApiException.BadRequest("图注最多 1000 字");
                    output["attrs"] = new JsonObject
                    {
                        ["assetId"] = imageId, ["caption"] = caption, ["width"] = Integer(attrs, "width", 25, 100),
                    };
                    Images.Add(imageId);
                    if (caption.Length > 0) Append(caption + "\n");
                    break;
                case "hardBreak":
                    Append("\n");
                    break;
                case "heading":
                    output["attrs"] = new JsonObject { ["level"] = Integer(attrs, "level", 2, 3) ?? 2 };
                    break;
                case "orderedList":
                    var listAttrs = new JsonObject { ["start"] = Integer(attrs, "start", 1, 1_000_000) ?? 1 };
                    if (String(attrs, "type") is { } style)
                    {
                        if (style is not ("1" or "a" or "A" or "i" or "I")) throw Invalid();
                        listAttrs["type"] = style;
                    }
                    output["attrs"] = listAttrs;
                    break;
                case "tableCell":
                case "tableHeader":
                    output["attrs"] = CellAttributes(attrs);
                    break;
            }

            if (source.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array || type is "text" or "hardBreak" or "image") throw Invalid();
                var children = new JsonArray();
                foreach (var child in content.EnumerateArray()) children.Add(Node(child, type));
                output["content"] = children;
            }
            if (type is "table" or "tableRow" or "tableCell" or "tableHeader" or "listItem" or "orderedList" or "bulletList"
                && output["content"] is not JsonArray { Count: > 0 })
                throw Invalid();
            if (type is "paragraph" or "heading") Append("\n");
            return output;
        }

        private void Append(string value)
        {
            Text.Append(value);
            if (Text.Length > MaxText + 1) throw ApiException.BadRequest("公告正文最多 20000 字");
        }

        private static bool AllowedChild(string? parent, string child) => parent switch
        {
            null => child == "doc",
            "doc" or "listItem" or "tableCell" or "tableHeader" =>
                child is "paragraph" or "heading" or "bulletList" or "orderedList" or "image" or "table",
            "paragraph" or "heading" => child is "text" or "hardBreak",
            "bulletList" or "orderedList" => child == "listItem",
            "table" => child == "tableRow",
            "tableRow" => child is "tableCell" or "tableHeader",
            _ => false,
        };

        private static JsonObject CellAttributes(JsonElement attrs)
        {
            var colspan = Integer(attrs, "colspan", 1, 100) ?? 1;
            var result = new JsonObject { ["colspan"] = colspan, ["rowspan"] = Integer(attrs, "rowspan", 1, 100) ?? 1 };
            if (String(attrs, "align") is { } align)
            {
                if (align is not ("left" or "center" or "right")) throw Invalid();
                result["align"] = align;
            }
            var widths = Property(attrs, "colwidth");
            if (widths.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (widths.ValueKind != JsonValueKind.Array || widths.GetArrayLength() != colspan) throw Invalid();
                var sanitized = new JsonArray();
                foreach (var width in widths.EnumerateArray())
                {
                    if (width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var n) || n is < 1 or > 4096) throw Invalid();
                    sanitized.Add(n);
                }
                result["colwidth"] = sanitized;
            }
            return result;
        }

        private static JsonArray Marks(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > 2) throw Invalid();
            var marks = new JsonArray();
            var seen = new HashSet<string>();
            foreach (var mark in source.EnumerateArray())
            {
                var type = String(mark, "type");
                if (type is not ("bold" or "link") || !seen.Add(type)) throw Invalid();
                var allowed = new JsonObject { ["type"] = type };
                if (type == "link")
                {
                    var href = String(Property(mark, "attrs"), "href");
                    if (href is null || href.Length > 2048 || href.Any(char.IsControl)
                        || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto"))
                        throw ApiException.BadRequest("公告链接仅支持 http、https 或 mailto 地址");
                    allowed["attrs"] = new JsonObject { ["href"] = uri.AbsoluteUri };
                }
                marks.Add(allowed);
            }
            return marks;
        }

        private static JsonElement Property(JsonElement source, string name) =>
            source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) ? value : default;

        private static string? String(JsonElement source, string name) =>
            Property(source, name) switch
            {
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                _ => throw Invalid(),
            };

        private static int? Integer(JsonElement source, string name, int min, int max)
        {
            var value = Property(source, name);
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max) return n;
            throw Invalid();
        }
    }
}
