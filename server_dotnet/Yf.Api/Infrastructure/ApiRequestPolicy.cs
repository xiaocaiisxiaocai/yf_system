using System.Globalization;

namespace Yf.Api.Infrastructure;

internal static class ApiRequestPolicy
{
    internal static bool IsChunkUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPut(request.Method)) return false;
        var segments = request.Path.Value?.TrimEnd('/').Split('/', StringSplitOptions.None);
        if (segments is ["", var oemApi, var oemVersion, var oem, var oemResource, var oemSession, var oemAction, var oemIndex])
            return RoutePart(oemApi, "api") && RoutePart(oemVersion, "v1") && RoutePart(oem, "oem")
                && RoutePart(oemResource, "uploads") && RoutePart(oemAction, "chunks")
                && Guid.TryParseExact(oemSession, "D", out _)
                && int.TryParse(oemIndex, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        return segments is ["", var api, var version, var resource, var session, var action, var index]
            && RoutePart(api, "api") && RoutePart(version, "v1")
            && RoutePart(resource, "uploads") && RoutePart(action, "chunks")
            && Guid.TryParseExact(session, "D", out _)
            && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    internal static bool IsFileContent(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        if (IsAnnouncementAsset(request.Path)) return true;
        var segments = request.Path.Value?.TrimEnd('/').Split('/', StringSplitOptions.None);
        if (segments is ["", var api7, var version7, var segment3, var segment4, var segment5, var segment6])
        {
            if (RoutePart(api7, "api") && RoutePart(version7, "v1") && RoutePart(segment3, "oem")
                && RoutePart(segment4, "files") && (RoutePart(segment6, "content") || RoutePart(segment6, "download"))
                && ulong.TryParse(segment5, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return true;
            return RoutePart(api7, "api") && RoutePart(version7, "v1") && RoutePart(segment3, "messages")
                && RoutePart(segment5, "images")
                && ulong.TryParse(segment4, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && ulong.TryParse(segment6, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }
        return segments is ["", var api, var version, var resource, var id, var action]
            && RoutePart(api, "api") && RoutePart(version, "v1") && RoutePart(resource, "files")
            && (RoutePart(action, "content") || RoutePart(action, "media"))
            && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsAnnouncementAsset(PathString path)
    {
        if (!path.StartsWithSegments("/api/v1/announcements/manage", out var remaining)
            && !path.StartsWithSegments("/api/v1/announcements", out remaining)
            && !path.StartsWithSegments("/api/v1/oem/announcements", out remaining))
            return false;
        return remaining.Value?.TrimEnd('/').Split('/') is ["", var announcementId, var assets, var assetId]
            && RoutePart(assets, "assets")
            && ulong.TryParse(announcementId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(assetId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    // ASP.NET route literals are case-insensitive, so policy checks must match that behavior.
    private static bool RoutePart(string actual, string expected)
        => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
