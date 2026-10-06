using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Identity;

public sealed class IdentityMiddleware(RequestDelegate next, IEnumerable<IRealmIdentityExtension> realms)
{
    private readonly IRealmIdentityExtension[] realmExtensions = ValidateRealmExtensions(realms);

    private static readonly HashSet<string> PublicPaths = new(StringComparer.Ordinal)
    {
        "/api/v1/auth/login", "/api/v1/auth/logout", "/api/v1/auth/refresh"
    };

    public async Task InvokeAsync(HttpContext context, AppDb db, TokenService tokens, IdentityProjectionCache identityCache)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/api/v1") || PublicPaths.Contains(path)
            || IsMediaRequest(context.Request) || IsNativeDownloadRequest(context.Request)
            || realmExtensions.Any(realm => IsAnonymousPath(realm, context.Request)))
        {
            await next(context);
            return;
        }
        if (!TryGetAccessToken(context.Request, out var token)) throw ApiException.Unauthorized();
        AccessClaims claims;
        try { claims = tokens.ParseAccess(token); }
        catch (SecurityTokenExpiredException) { throw new ApiException(401, 40102, "登录状态已过期"); }
        catch (ApiException) { throw; }
        catch { throw ApiException.Unauthorized("登录状态无效"); }

        if (claims.Realm != IdentityRealms.Internal)
        {
            var realm = realmExtensions.SingleOrDefault(extension => extension.Realm == claims.Realm)
                ?? throw ApiException.Unauthorized("登录状态无效");
            if (!context.Request.Path.StartsWithSegments(realm.PathPrefix))
                throw new ApiException(403, 40304, "该账号无权访问此系统");
            await realm.AuthenticateAsync(context, claims, ct);
            await next(context);
            return;
        }

        // Authentication owns only its lookup connection, not the downstream request. Cache hits
        // still probe the database revision and clock, so committed revocation and expiry are never
        // hidden by an application TTL.
        var row = await identityCache.ResolveAsync(db, claims, ct);
        if (row is null || !row.SessionActive) throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        if (row.Status != AccountStatuses.Active) throw ApiException.Unauthorized("账号已被禁用");
        if (row.UserType == UserTypes.Supplier && !row.SupplierActive) throw ApiException.Unauthorized("所属供应商已被禁用");
        if (row.MustChangePassword && path is not ("/api/v1/auth/profile" or "/api/v1/auth/password" or "/api/v1/auth/logout"))
            throw new ApiException(403, 40303, "请先修改初始密码");
        context.Items[typeof(CurrentUser)] = new CurrentUser(row.Id, row.EmployeeNo, row.UserType, row.SupplierId, claims.SessionId);
        context.Items[typeof(AccessClaims)] = claims;
        await next(context);
    }

    internal static bool IsAnonymousPath(IRealmIdentityExtension realm, HttpRequest request) =>
        request.Path.StartsWithSegments(realm.PathPrefix) && realm.IsAnonymousPath(request);

    private static IRealmIdentityExtension[] ValidateRealmExtensions(IEnumerable<IRealmIdentityExtension> realms)
    {
        var extensions = realms.ToArray();
        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension.Realm) || extension.Realm == IdentityRealms.Internal)
                throw new InvalidOperationException("Realm extensions must use a non-internal realm name.");
            if (!extension.PathPrefix.HasValue || !extension.PathPrefix.Value!.StartsWith("/api/v1/", StringComparison.Ordinal))
                throw new InvalidOperationException("Realm extensions must use an API path prefix below /api/v1.");
        }
        if (extensions.GroupBy(extension => extension.Realm, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Identity realm names must be unique.");
        return extensions;
    }

    internal static bool TryGetAccessToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        var header = request.Headers.Authorization.ToString();
        if (header.Length > 0)
        {
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length == 7)
                return false;
            token = header[7..];
            return true;
        }
        if (!string.Equals(request.Path.Value, ProjectRealtimeHub.Path, StringComparison.Ordinal)
            || !request.Query.TryGetValue("access_token", out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
            return false;
        token = values[0]!;
        return true;
    }

    internal static bool IsMediaRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;
        const string prefix = "/api/v1/files/";
        const string suffix = "/media";
        var path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var id = path[prefix.Length..^suffix.Length];
        return id.Length > 0 && long.TryParse(id, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0;
    }

    internal static bool IsNativeDownloadRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;
        const string prefix = "/api/v1/files/";
        var path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var segments = path[prefix.Length..].Split('/', StringSplitOptions.None);
        if (segments.Length == 2 && segments[0] == "batch-download")
            return DownloadGrantService.IsValidHandle(segments[1]);
        if (segments.Length != 3 || segments[1] != "native-download"
            || !DownloadGrantService.IsValidHandle(segments[2])) return false;
        return long.TryParse(segments[0], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
    }
}
