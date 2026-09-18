using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Identity;

internal static class OemIdentityEndpoints
{
    // Distinct name and path from the internal refresh cookie: the two realms never
    // overwrite or read each other's credential in the same browser.
    internal const string RefreshCookie = "oem_refresh_token";
    private const string RefreshCookiePath = OemApi.Prefix + "/auth";

    public static void Map(RouteGroupBuilder oem)
    {
        var auth = oem.MapGroup("/auth");
        auth.MapPost("/login", async (OemLoginRequest request, HttpContext ctx, OemAuthService service, AppOptions options, CancellationToken ct) =>
        {
            if (!IdentityModule.OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            var result = await service.LoginAsync(request, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, options);
            return Results.Json(result.Response);
        });
        auth.MapPost("/refresh", async (HttpContext ctx, OemAuthService service, AppOptions options, CancellationToken ct) =>
        {
            if (!IdentityModule.OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            if (!ctx.Request.Cookies.TryGetValue(RefreshCookie, out var raw)) throw ApiException.Unauthorized("缺少登录凭证");
            var result = await service.RefreshAsync(raw, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, options);
            return Results.Json(result.Response);
        });
        auth.MapPost("/logout", async (HttpContext ctx, OemAuthService service, AppOptions options, CancellationToken ct) =>
        {
            if (!IdentityModule.OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            ctx.Request.Cookies.TryGetValue(RefreshCookie, out var refresh);
            await service.LogoutAsync(refresh, ctx.Request.Headers.Authorization.ToString(), ClientIp.Resolve(ctx, options), ct);
            ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions
            {
                HttpOnly = true, Path = RefreshCookiePath, Secure = options.CookieSecure, SameSite = SameSiteMode.Lax,
            });
            return Results.Json(new { });
        });
        auth.MapGet("/me", async (HttpContext ctx, OemAuthService service, CancellationToken ct) =>
            Results.Json(await service.MeAsync(RequireAccount(ctx), ct)));
        auth.MapPut("/password", async (OemChangePasswordRequest request, HttpContext ctx, OemAuthService service, CancellationToken ct) =>
        {
            await service.ChangePasswordAsync(RequireAccount(ctx), request, ct);
            return Results.Json(new { });
        });
    }

    private static OemAccountActor RequireAccount(HttpContext ctx) =>
        OemActorAccessor.Get(ctx) as OemAccountActor ?? throw ApiException.Forbidden("该接口仅供 OEM 厂商账号使用");

    private static void SetRefreshCookie(HttpContext ctx, string token, AppOptions options) =>
        ctx.Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true, Secure = options.CookieSecure, SameSite = SameSiteMode.Lax, Path = RefreshCookiePath,
            MaxAge = TimeSpan.FromDays(options.RefreshTtlDays),
        });
}
