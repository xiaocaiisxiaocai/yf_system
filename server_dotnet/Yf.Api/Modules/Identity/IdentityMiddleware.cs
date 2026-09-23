using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Identity;

public sealed class IdentityMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.Ordinal)
    {
        "/api/v1/auth/login", "/api/v1/auth/logout", "/api/v1/auth/refresh"
    };

    public async Task InvokeAsync(HttpContext context, AppDb db, TokenService tokens)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/api/v1") || PublicPaths.Contains(path) || IsMediaRequest(context.Request))
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

        // Authentication owns only its lookups, not the downstream request. In
        // particular, uploads/downloads must not pin this connection while they
        // stream or wait for a second connection from the same pool.
        await using (var conn = await db.OpenAsync(ct))
        {
            // One round trip checks the session, the account and its supplier. DateTime.UtcNow is
            // translated to the database clock (UTC_TIMESTAMP), matching the session checks elsewhere.
            await using var ef = EfDb.Use(conn);
            var sessionId = claims.SessionId;
            var row = await ef.Users.Where(user => user.Id == claims.UserId).Select(user => new
            {
                user.Id, user.EmployeeNo, user.UserType, user.SupplierId, user.Status, user.MustChangePassword,
                SessionActive = ef.RefreshTokens.Any(token => token.UserId == user.Id && token.SessionId == sessionId
                    && !token.Revoked && token.ExpiresAt > DateTime.UtcNow),
                SupplierActive = user.SupplierId != null
                    && ef.Suppliers.Any(supplier => supplier.Id == user.SupplierId && supplier.Status == "ACTIVE"),
            }).SingleOrDefaultAsync(ct);
            if (row is null || !row.SessionActive) throw ApiException.Unauthorized("登录状态已失效，请重新登录");
            if (row.Status != "ACTIVE") throw ApiException.Unauthorized("账号已被禁用");
            if (row.UserType == "SUPPLIER" && !row.SupplierActive) throw ApiException.Unauthorized("所属供应商已被禁用");
            if (row.MustChangePassword && path is not ("/api/v1/auth/profile" or "/api/v1/auth/password" or "/api/v1/auth/logout"))
                throw new ApiException(403, 40303, "请先修改初始密码");
            context.Items[typeof(CurrentUser)] = new CurrentUser(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
            context.Items[typeof(AccessClaims)] = claims;
        }
        await next(context);
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
}
