using System.Text.RegularExpressions;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceFilterTerms
{
    private static readonly Regex Separators = new(@"[\s,，;；、]+");

    // Pasted lines can contain spaces or commas within a term.
    internal static IEnumerable<string> Split(string text) => text.Contains('\n')
        ? text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
        : Separators.Split(text).Where(term => term.Length > 0);
}
