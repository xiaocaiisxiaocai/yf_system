using System.Text.RegularExpressions;

namespace Yf.Api.Modules.Files;

// 公司内部发给供应商（C2S）的资料要求，见 docs/上传资料要求契约-2026-09-24.md。
// 供应商发给公司（S2C）不设限制。前端镜像：web/src/utils/uploadMaterialRules.ts。
public static partial class UploadMaterialRules
{
    public const string MotionFlowNamingMessage =
        "发给供应商的 Excel 需按「CSLR-XXX XXX机 210XXX-X 动作流程.xlsx」命名（每段 X 为 1~10 位，空格个数不限，机/機、动作/動作均可）";

    public const string StepRequiredMessage =
        "发给供应商的资料至少需要一个 STEP 格式 3D 图（.step/.stp），请先上传或与本批文件一起上传 STEP 文件";

    private static readonly string[] StepExtensions = ["step", "stp"];
    private static readonly string[] SpreadsheetExtensions = ["xls", "xlsx", "xlsm", "xlsb"];

    public static IReadOnlyList<string> StepFileExtensions => StepExtensions;

    public static bool IsStepFile(string fileName) =>
        StepExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    public static bool IsSpreadsheet(string fileName) =>
        SpreadsheetExtensions.Contains(UploadService.ExtensionOf(fileName), StringComparer.Ordinal);

    public static bool IsMotionFlowWorkbookName(string fileName) => MotionFlowWorkbookPattern().IsMatch(fileName);

    // CSLR-XXX XXX机 210XXX-X 动作流程.xlsx：每段 X 为 1~10 位，各段之间的空白（含全角空格）个数不固定，
    // 机/機、动作/動作 简繁均可。编号段取完整的字母数字串，避免把 "CSLR-605 机" 拆成编号 "60" + 机台 "5"；
    // 机台名称是 1~10 个任意字符并以 机/機 结尾。
    //
    // 语义以前端 JS 正则（/…/i，无 u 标志）为准，.NET 写成显式字符类以逐字符一致：
    // - 不用 RegexOptions.IgnoreCase：.NET 的大小写等价会让开尔文符号 U+212A、长 s U+017F 匹配 k / s，JS 不会；
    //   只有 CSLR 与 xlsx 需要忽略大小写，用 [Cc] 这类 ASCII 字符类表达。
    // - \s 用 JS 的空白集合（含 U+FEFF、U+2028/2029，不含 U+0085），\S 为其补集；
    // - . 用 JS 的定义（排除 \n \r U+2028 U+2029）；
    // - 结尾用 \z（.NET 的 $ 允许末尾多一个 \n）。
    // 共享用例：web/src/utils/upload-material-rules.cases.json。
    private const string Space = @"[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
    private const string NonSpace = @"[^\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
    private const string AnyChar = @"[^\n\r\u2028\u2029]";
    private const string Dash = "[-－]";
    private const string Segment = "[0-9A-Za-z]";

    private const string MotionFlowWorkbookRegex =
        "^[Cc][Ss][Ll][Rr]" + Space + "*" + Dash + Space + "*" + Segment + "{1,10}(?!" + Segment + ")"
        + Space + "*" + NonSpace + AnyChar + "{0,9}?[机機]"
        + Space + "*210" + Segment + "{1,10}" + Space + "*" + Dash + Space + "*" + Segment + "{1,10}"
        + Space + "*[动動]作流程" + Space + @"*\.[Xx][Ll][Ss][Xx]\z";

    [GeneratedRegex(MotionFlowWorkbookRegex, RegexOptions.CultureInvariant)]
    private static partial Regex MotionFlowWorkbookPattern();
}
