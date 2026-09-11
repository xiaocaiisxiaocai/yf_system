using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public static class IdentityModule
{
    private const string RefreshCookie = "refresh_token";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services) => services
        .AddSingleton<CaptchaService>()
        .AddSingleton<TokenService>()
        .AddSingleton<PermissionService>()
        .AddSingleton<IdentityService>();

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapGet("/captcha", (HttpContext ctx, CaptchaService service, AppOptions options) => service.Issue(ClientIp.Resolve(ctx, options)));
        auth.MapPost("/login", async (LoginRequest request, HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            var result = await service.LoginAsync(request, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, options);
            return Results.Json(result.Response);
        });
        auth.MapPost("/refresh", async (HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            if (!ctx.Request.Cookies.TryGetValue(RefreshCookie, out var raw)) throw ApiException.Unauthorized("缺少登录凭证");
            var result = await service.RefreshAsync(raw, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, options);
            return Results.Json(result.Response);
        });
        auth.MapPost("/logout", async (HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request.Headers.Origin.ToString(), options.WebBaseUrl)) throw ApiException.Forbidden();
            ctx.Request.Cookies.TryGetValue(RefreshCookie, out var refresh);
            await service.LogoutAsync(refresh, ctx.Request.Headers.Authorization.ToString(), ClientIp.Resolve(ctx, options), ct);
            ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions { HttpOnly = true, Path = "/api/v1/auth", Secure = options.CookieSecure, SameSite = SameSiteMode.Lax });
            return Results.Json(new { });
        });
        auth.MapGet("/profile", async (HttpContext ctx, IdentityService service, CancellationToken ct) =>
            Results.Json(await service.ProfileAsync(AccessService.GetCurrent(ctx), ct)));
        auth.MapPut("/profile", async (UpdateProfileRequest request, HttpContext ctx, IdentityService service, CancellationToken ct) =>
            Results.Json(await service.UpdateProfileAsync(AccessService.GetCurrent(ctx), request, ct)));
        auth.MapPut("/password", async (ChangePasswordRequest request, HttpContext ctx, IdentityService service, CancellationToken ct) =>
        {
            await service.ChangePasswordAsync(AccessService.GetCurrent(ctx), request, ct);
            return Results.Json(new { });
        });
        return endpoints;
    }

    private static void SetRefreshCookie(HttpContext ctx, string token, AppOptions options) => ctx.Response.Cookies.Append(RefreshCookie, token,
        new CookieOptions { HttpOnly = true, Secure = options.CookieSecure, SameSite = SameSiteMode.Lax, Path = "/api/v1/auth", MaxAge = TimeSpan.FromDays(options.RefreshTtlDays) });

    private static bool OriginAllowed(string origin, string configured)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && origin == uri.GetLeftPart(UriPartial.Authority);
    }
}
