namespace Yf.Api.Modules.Files;

// 公司内部发给供应商（C2S）的资料要求，见 docs/上传资料要求契约-2026-09-24.md。
// 一批资料必须同时有动作流程 Excel 与 3D 资料（STEP 或压缩文件，STEP 很多时会打包压缩）。
// 供应商发给公司（S2C）不设限制。动作流程 Excel 不校验文件名。前端镜像：web/src/utils/uploadMaterialRules.ts。
public static class UploadMaterialRules
{
    public const string MaterialRequiredMessage =
        "发给供应商的资料需要同时包含动作流程 Excel 和 STEP 格式 3D 图（.step/.stp）或压缩文件（.zip/.rar/.7z），请与本批文件一起上传";

    private static readonly string[] StepExtensions = ["step", "stp"];
    private static readonly string[] ArchiveExtensions = ["zip", "rar", "7z"];
    private static readonly string[] SpreadsheetExtensions = ["xls", "xlsx", "xlsm", "xlsb"];

    /// <summary>3D 资料扩展名：STEP 或压缩文件。</summary>
    public static IReadOnlyList<string> ModelFileExtensions { get; } = [.. StepExtensions, .. ArchiveExtensions];

    public static IReadOnlyList<string> SpreadsheetFileExtensions => SpreadsheetExtensions;

    public static bool IsStepFile(string fileName) =>
        StepExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    public static bool IsArchive(string fileName) =>
        ArchiveExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    public static bool IsModelMaterial(string fileName) => IsStepFile(fileName) || IsArchive(fileName);

    public static bool IsSpreadsheet(string fileName) =>
        SpreadsheetExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    /// <summary>Excel、STEP、压缩文件本身就是必需资料，服务端不要求它们先行；其它文件才需要两类资料都已具备。</summary>
    public static bool IsRequiredMaterial(string fileName) => IsModelMaterial(fileName) || IsSpreadsheet(fileName);
}
