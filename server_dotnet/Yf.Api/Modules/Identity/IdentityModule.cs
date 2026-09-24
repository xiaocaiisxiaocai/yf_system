using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public static class IdentityModule
{
    private const string RefreshCookie = "refresh_token";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services) => services
        .AddSingleton<LoginRateLimiter>()
        .AddSingleton<IdentityProjectionCache>()
        .AddSingleton<TokenService>()
        .AddSingleton<PermissionService>()
        .AddSingleton<IdentityService>()
        .AddHostedService<SessionCleanupService>();

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapPost("/login", async (LoginRequest request, HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request, options.WebBaseUrl)) throw ApiException.Forbidden();
            var result = await service.LoginAsync(request, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, result.RefreshExpiresAt, options);
            return result.Response;
        });
        auth.MapPost("/refresh", async (HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request, options.WebBaseUrl)) throw ApiException.Forbidden();
            if (!ctx.Request.Cookies.TryGetValue(RefreshCookie, out var raw)) throw ApiException.Unauthorized("缺少登录凭证");
            var result = await service.RefreshAsync(raw, ClientIp.Resolve(ctx, options), ct);
            SetRefreshCookie(ctx, result.Refresh, result.RefreshExpiresAt, options);
            return result.Response;
        });
        auth.MapPost("/logout", async (HttpContext ctx, IdentityService service, AppOptions options, CancellationToken ct) =>
        {
            if (!OriginAllowed(ctx.Request, options.WebBaseUrl)) throw ApiException.Forbidden();
            ctx.Request.Cookies.TryGetValue(RefreshCookie, out var refresh);
            await service.LogoutAsync(refresh, ctx.Request.Headers.Authorization.ToString(), ClientIp.Resolve(ctx, options), ct);
            ctx.Response.Cookies.Delete(RefreshCookie, new CookieOptions { HttpOnly = true, Path = "/api/v1/auth", Secure = options.CookieSecure, SameSite = SameSiteMode.Lax });
            return EmptyResponse.Instance;
        });
        auth.MapGet("/profile", async (HttpContext ctx, IdentityService service, CancellationToken ct) =>
            await service.ProfileAsync(AccessService.GetCurrent(ctx), ct));
        auth.MapPut("/profile", async (UpdateProfileRequest request, HttpContext ctx, IdentityService service, CancellationToken ct) =>
            await service.UpdateProfileAsync(AccessService.GetCurrent(ctx), request, ct));
        auth.MapPut("/password", async (ChangePasswordRequest request, HttpContext ctx, IdentityService service, CancellationToken ct) =>
        {
            await service.ChangePasswordAsync(AccessService.GetCurrent(ctx), request, ct);
            return EmptyResponse.Instance;
        });
        return endpoints;
    }

    private static void SetRefreshCookie(HttpContext ctx, string token, DateTime refreshExpiresAt, AppOptions options)
    {
        var expires = new DateTimeOffset(DateTime.SpecifyKind(refreshExpiresAt, DateTimeKind.Utc));
        var maxAge = RefreshCookieMaxAge(expires, DateTimeOffset.UtcNow, options.RefreshTtlDays);
        ctx.Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth",
            MaxAge = maxAge,
            Expires = expires
        });
    }

    internal static TimeSpan RefreshCookieMaxAge(DateTimeOffset expiresAt, DateTimeOffset now, int refreshTtlDays)
    {
        var remaining = expiresAt - now;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining <= TimeSpan.FromDays(refreshTtlDays) ? remaining : TimeSpan.FromDays(refreshTtlDays);
    }

    internal static bool OriginAllowed(string origin, string configured)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && origin == uri.GetLeftPart(UriPartial.Authority);
    }

    internal static bool OriginAllowed(HttpRequest request, string configured)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)) return OriginAllowed(origin, configured);
        return !string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);
    }
}
