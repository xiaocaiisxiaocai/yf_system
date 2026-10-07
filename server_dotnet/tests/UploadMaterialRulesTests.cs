using System.Runtime.CompilerServices;
using System.Text.Json;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class UploadMaterialRulesTests
{
    // Shared with web/src/test/uploadMaterialRules.test.ts, the same way password-policy.json is shared:
    // the frontend (JS regex semantics) and the backend must classify every name identically.
    public static TheoryData<string> AcceptedNames() => new(Names("accept"));
    public static TheoryData<string> RejectedNames() => new(Names("reject"));

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void AcceptsMotionFlowWorkbookNameVariants(string name) =>
        Assert.True(UploadMaterialRules.IsMotionFlowWorkbookName(name), Escape(name));

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void RejectsWorkbookNamesOutsideTheConvention(string name) =>
        Assert.False(UploadMaterialRules.IsMotionFlowWorkbookName(name), Escape(name));

    [Fact]
    public void SharedCasesCoverUnicodeFoldingAndLineTerminatorEdges()
    {
        var rejected = Names("reject");
        Assert.Contains(rejected, name => name.Contains('\u212A', StringComparison.Ordinal));
        Assert.Contains(rejected, name => name.Contains('\u017F', StringComparison.Ordinal));
        Assert.Contains(rejected, name => name.EndsWith('\n'));
        var accepted = Names("accept");
        Assert.Contains(accepted, name => name.Contains('\uFEFF', StringComparison.Ordinal));
        Assert.Contains(accepted, name => name.Contains('\u2028', StringComparison.Ordinal));
    }

    private static string[] Names(string kind)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesPath()));
        var names = document.RootElement.GetProperty(kind).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.NotEmpty(names);
        return names;
    }

    private static string CasesPath([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..",
            "web", "src", "utils", "upload-material-rules.cases.json"));

    private static string Escape(string name) =>
        string.Concat(name.Select(character => character is < ' ' or > '~' && !char.IsLetter(character)
            ? $"\\u{(int)character:X4}" : character.ToString()));

    [Fact]
    public void ClassifiesStepAndSpreadsheetFilesByExtension()
    {
        Assert.True(UploadMaterialRules.IsStepFile("CSLR-605 六軸雙工位放板機2105931-1.STEP"));
        Assert.True(UploadMaterialRules.IsStepFile("assembly.stp"));
        Assert.False(UploadMaterialRules.IsStepFile("assembly.step.zip"));
        Assert.False(UploadMaterialRules.IsStepFile("step"));
        Assert.True(UploadMaterialRules.IsSpreadsheet("a.XLSX"));
        Assert.True(UploadMaterialRules.IsSpreadsheet("a.xls"));
        Assert.False(UploadMaterialRules.IsSpreadsheet("a.pdf"));
        Assert.True(UploadMaterialRules.IsSpreadsheet("a.XLSM"));
        Assert.True(UploadMaterialRules.IsSpreadsheet("a.xlsb"));
    }

    [Theory]
    [InlineData("动作流程.xls", "application/vnd.ms-excel")]
    [InlineData("动作流程.XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("动作流程.xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12")]
    [InlineData("动作流程.xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12")]
    public void EveryAcceptedWorkbookExtensionHasAnExcelMimeType(string name, string expected) =>
        Assert.Equal(expected, FileStorage.MimeType(name));
}
