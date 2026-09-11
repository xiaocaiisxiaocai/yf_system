using Dapper;
using Microsoft.IdentityModel.Tokens;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class IdentityMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.Ordinal)
    {
        "/api/v1/auth/login", "/api/v1/auth/logout", "/api/v1/auth/refresh", "/api/v1/auth/captcha"
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
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length == 7) throw ApiException.Unauthorized();
        AccessClaims claims;
        try { claims = tokens.ParseAccess(header[7..]); }
        catch (SecurityTokenExpiredException) { throw new ApiException(401, 40102, "登录状态已过期"); }
        catch (ApiException) { throw; }
        catch { throw ApiException.Unauthorized("登录状态无效"); }

        await using var conn = await db.OpenAsync(ct);
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
        await next(context);
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
