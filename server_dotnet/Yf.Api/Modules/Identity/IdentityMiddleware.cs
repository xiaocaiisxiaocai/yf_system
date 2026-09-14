using Dapper;
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

    public async Task InvokeAsync(HttpContext context, AppDb db, TokenService tokens, IdentityService identity)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/api/v1") || PublicPaths.Contains(path))
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
            if (!await identity.HasActiveSessionAsync(conn, null, claims.UserId, claims.SessionId, ct)) throw ApiException.Unauthorized("登录状态已失效，请重新登录");
            var row = await conn.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
                "SELECT id Id,employee_no EmployeeNo,user_type UserType,supplier_id SupplierId,status Status,must_change_password MustChangePassword FROM users WHERE id=@id",
                new { id = claims.UserId }, cancellationToken: ct)) ?? throw ApiException.Unauthorized("账号不存在");
            if (row.Status != "ACTIVE") throw ApiException.Unauthorized("账号已被禁用");
            if (row.UserType == "SUPPLIER")
            {
                if (row.SupplierId is not ulong sid || await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM suppliers WHERE id=@sid AND status='ACTIVE')", new { sid }, cancellationToken: ct)) != 1)
                    throw ApiException.Unauthorized("所属供应商已被禁用");
            }
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

    private sealed class AuthUser
    {
        public ulong Id { get; init; }
        public string EmployeeNo { get; init; } = "";
        public string UserType { get; init; } = "";
        public ulong? SupplierId { get; init; }
        public string Status { get; init; } = "";
        public bool MustChangePassword { get; init; }
    }
}
