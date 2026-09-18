using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Identity;

/// <summary>
/// Plugs the OEM account realm into the shared authentication pipeline. Tokens of
/// this realm are confined to <see cref="OemApi.Prefix"/> by the middleware; this
/// class validates the live session, the account and its company on every request.
/// </summary>
public sealed partial class OemIdentityExtension(IDbContextFactory<YfDbContext> dbFactory) : IRealmIdentityExtension
{
    private static readonly HashSet<string> AnonymousPosts = new(StringComparer.Ordinal)
    {
        OemApi.Prefix + "/auth/login", OemApi.Prefix + "/auth/refresh", OemApi.Prefix + "/auth/logout",
    };

    private static readonly HashSet<string> PasswordPendingPaths = new(StringComparer.Ordinal)
    {
        OemApi.Prefix + "/auth/me", OemApi.Prefix + "/auth/password", OemApi.Prefix + "/auth/logout",
    };

    public string Realm => OemRealms.Oem;

    public PathString PathPrefix => OemApi.Prefix;

    public bool IsAnonymousPath(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (HttpMethods.IsPost(request.Method) && AnonymousPosts.Contains(path)) return true;
        // File streams authenticate with a short-lived, path-scoped HttpOnly grant cookie
        // (see the delivery slice) so that browsers can download without a bearer header.
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && CookieStreamPath().IsMatch(path);
    }

    public async Task AuthenticateAsync(HttpContext context, AccessClaims claims, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = await OemClock.NowAsync(db, ct);
        if (!await OemAuthService.HasActiveSessionAsync(db, claims.UserId, claims.SessionId, now, ct))
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        var row = await db.OemAccounts.AsNoTracking()
            .Where(account => account.Id == claims.UserId)
            .Join(db.OemCompanies, account => account.OemCompanyId, company => company.Id, (account, company) => new
            {
                account.Id, account.EmployeeNo, account.RealName, account.Status, account.MustChangePassword,
                account.OemCompanyId, CompanyStatus = company.Status,
            })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.Unauthorized("账号不存在");
        if (row.Status != OemStatus.Active) throw ApiException.Unauthorized("账号已被禁用");
        if (row.CompanyStatus != OemStatus.Active) throw ApiException.Unauthorized("所属厂商已被禁用");
        if (row.MustChangePassword && !PasswordPendingPaths.Contains(context.Request.Path.Value ?? string.Empty))
            throw new ApiException(403, 40303, "请先修改初始密码");
        context.Items[typeof(OemAccountActor)] = new OemAccountActor(row.Id, row.EmployeeNo, row.RealName, row.OemCompanyId, claims.SessionId);
        context.Items[typeof(AccessClaims)] = claims;
    }

    [GeneratedRegex(@"^/api/v1/oem/files/[0-9]{1,20}/download$", RegexOptions.CultureInvariant)]
    private static partial Regex CookieStreamPath();
}
