using OpenCCNET;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Simplified/traditional Chinese for comparing names that IPMS and the account directory spell differently
/// (IPMS 古才鸿, account 古才鴻). Conversion is OpenCC (OpenCCDotNet); its dictionaries are embedded in the package.
/// </summary>
internal static class ChineseVariants
{
    private static readonly Lazy<bool> Ready = new(() => { ZhConverter.Initialize(); return true; });

    /// <summary>The text in simplified Chinese; text that has no traditional characters comes back unchanged.</summary>
    internal static string Simplify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        _ = Ready.Value;
        return ZhConverter.HantToHans(text);
    }
}
