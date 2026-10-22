using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class UploadMaterialRulesTests
{
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
        Assert.True(UploadMaterialRules.IsSpreadsheet("动作流程.xlsx"));
    }

    [Theory]
    [InlineData("动作流程.xls", "application/vnd.ms-excel")]
    [InlineData("动作流程.XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("动作流程.xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12")]
    [InlineData("动作流程.xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12")]
    public void EveryAcceptedWorkbookExtensionHasAnExcelMimeType(string name, string expected) =>
        Assert.Equal(expected, FileStorage.MimeType(name));
}
