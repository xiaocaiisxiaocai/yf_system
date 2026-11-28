using System.Text.Json.Nodes;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Announcements;

namespace Yf.Api.Tests;

public sealed class AnnouncementDocumentTests
{
    private static AnnouncementDocument Parse(string? json, string? legacy = null) =>
        AnnouncementDocument.FromDraft(new(null, legacy, null, null, null, null, DocumentJson: json));

    [Fact]
    public void OldTextIsLiteralAndRetainsItsLineBreaks()
    {
        var parsed = Parse(null, "  <旧标签>\n第二行  ");
        Assert.Null(parsed.Json);
        Assert.Equal("<旧标签>\n第二行", parsed.Text);
        Assert.Empty(parsed.Images);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"type":"doc","content":[{"type":"text","text":"invalid placement"}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"image","attrs":{"src":"somewhere"}}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"image","attrs":{"assetId":0}}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"image","attrs":{"assetId":1,"width":155}}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"文","marks":[{"type":"link","attrs":{"href":"javascript:invalid"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"文","marks":[{"type":"link","attrs":{"href":"//other.site/relative"}}]}]}]}""")]
    public void MalformedContentIsRejectedBeforePersistence(string json)
    {
        Assert.Equal(400, Assert.Throws<ApiException>(() => Parse(json)).Status);
    }

    [Fact]
    public void SavedDocumentRetainsBlockOrderAndOnlySupportedAttributes()
    {
        const string json = """
            {"type":"doc","content":[
              {"type":"paragraph","content":[
                {"type":"text","text":"查看","marks":[{"type":"bold"}]},
                {"type":"text","text":"规范","marks":[{"type":"link","attrs":{"href":"https://example.com/doc","target":"untrusted"}}]}]},
              {"type":"image","attrs":{"assetId":37,"caption":"示意图","width":75,"src":"ignored","style":"ignored"}},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","content":[
                {"type":"paragraph","content":[{"type":"text","text":"明细"}]}]}]}]}]}
            """;
        var parsed = Parse(json, "untrusted summary");
        Assert.Equal("查看规范\n示意图\n明细", parsed.Text);
        Assert.Equal(new ulong[] { 37 }, parsed.Images);
        var blocks = JsonNode.Parse(parsed.Json!)!["content"]!.AsArray();
        Assert.Equal("image", blocks[1]!["type"]!.GetValue<string>());
        Assert.Null(blocks[1]!["attrs"]!["src"]);
        Assert.Null(blocks[1]!["attrs"]!["style"]);
        var attrs = blocks[0]!["content"]![1]!["marks"]![0]!["attrs"]!.AsObject();
        Assert.Single(attrs);
        Assert.Equal("https://example.com/doc", attrs["href"]!.GetValue<string>());
    }
}
