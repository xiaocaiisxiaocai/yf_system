using System.Globalization;

namespace Yf.Api.Infrastructure;

internal static class ApiRequestPolicy
{
    internal static bool IsChunkUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPut(request.Method)) return false;
        var segments = request.Path.Value?.TrimEnd('/').Split('/', StringSplitOptions.None);
        return segments is ["", var api, var version, var resource, var session, var action, var index]
            && RoutePart(api, "api") && RoutePart(version, "v1")
            && RoutePart(resource, "uploads") && RoutePart(action, "chunks")
            && Guid.TryParseExact(session, "D", out _)
            && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    internal static bool IsFileContent(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        var segments = request.Path.Value?.TrimEnd('/').Split('/', StringSplitOptions.None);
        return segments is ["", var api, var version, var resource, var id, var action]
            && RoutePart(api, "api") && RoutePart(version, "v1") && RoutePart(resource, "files")
            && (RoutePart(action, "content") || RoutePart(action, "media"))
            && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    // ASP.NET route literals are case-insensitive, so policy checks must match that behavior.
    private static bool RoutePart(string actual, string expected)
        => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
