namespace Yf.Api.Modules.Files;

// 公司内部发给供应商（C2S）的资料要求，见 docs/上传资料要求契约-2026-09-24.md。
// 供应商发给公司（S2C）不设限制。动作流程 Excel 不校验文件名。前端镜像：web/src/utils/uploadMaterialRules.ts。
public static class UploadMaterialRules
{
    public const string StepRequiredMessage =
        "发给供应商的资料至少需要一个 STEP 格式 3D 图（.step/.stp），请先上传或与本批文件一起上传 STEP 文件";

    private static readonly string[] StepExtensions = ["step", "stp"];
    private static readonly string[] SpreadsheetExtensions = ["xls", "xlsx", "xlsm", "xlsb"];

    public static IReadOnlyList<string> StepFileExtensions => StepExtensions;

    public static bool IsStepFile(string fileName) =>
        StepExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    public static bool IsSpreadsheet(string fileName) =>
        SpreadsheetExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);
}
