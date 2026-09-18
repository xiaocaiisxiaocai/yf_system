using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Identity;

public sealed record OemLoginRequest(string EmployeeNo, [property: JsonRequired] string Password);

public sealed record OemChangePasswordRequest(
    [property: JsonRequired] string OldPassword,
    [property: JsonRequired] string NewPassword);

public sealed record OemAccountBrief(ulong Id, string EmployeeNo, string RealName, string Email, ulong CompanyId, string CompanyName);

public sealed record OemLoginResponse(string AccessToken, long ExpiresAt, bool MustChangePassword, OemAccountBrief Account);

public sealed record OemTokenResponse(string AccessToken, long ExpiresAt);

public sealed record OemMeResponse(OemAccountBrief Account, bool MustChangePassword);

/// <summary>
/// Login, refresh rotation, logout and password change for OEM accounts. The flow
/// mirrors the internal realm (same Argon2 policy, same lockout thresholds, same
/// rotation/replay semantics) but operates only on the OEM tables, so an OEM
/// session can never resolve to an internal or supplier user.
/// </summary>
public sealed class OemAuthService(
    IDbContextFactory<YfDbContext> dbFactory,
    AppOptions options,
    LoginRateLimiter rateLimiter,
    TokenService tokens,
    AuditService audit)
{
    private const string InvalidCredentials = "账号或密码错误";

    public async Task<(OemLoginResponse Response, string Refresh)> LoginAsync(OemLoginRequest request, string clientIp, CancellationToken ct)
    {
        var employeeNo = request.EmployeeNo?.Trim() ?? string.Empty;
        if (employeeNo.Length == 0 || string.IsNullOrEmpty(request.Password)) throw ApiException.BadRequest("账号和密码不能为空");
        if (employeeNo.EnumerateRunes().Count() > 64 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > PasswordService.MaxPasswordBytes)
            throw ApiException.BadRequest(InvalidCredentials);
        if (!rateLimiter.AllowLogin(OemRealms.Oem, clientIp, employeeNo)) throw ApiException.BadRequest("请求过于频繁，请稍后再试");

        OemAccount? candidate;
        await using (var lookup = await dbFactory.CreateDbContextAsync(ct))
            candidate = await lookup.OemAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.EmployeeNo == employeeNo, ct);
        var matches = await PasswordService.VerifyAsync(request.Password,
            candidate?.PasswordHash ?? await PasswordService.TimingDummyHashAsync(), ct);
        if (candidate is null)
        {
            await using var auditContext = await dbFactory.CreateDbContextAsync(ct);
            await auditContext.Database.OpenConnectionAsync(ct);
            await AuditBestEffortAsync(auditContext, null, employeeNo, "OEM_LOGIN_FAILED", clientIp, null, ct);
            throw ApiException.Unauthorized(InvalidCredentials);
        }

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var account = await LockAccountAsync(context, candidate.Id, ct);
        var now = await OemClock.NowAsync(context, ct);
        if (account is null)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(context, null, employeeNo, "OEM_LOGIN_FAILED", clientIp, null, ct);
            throw ApiException.Unauthorized(InvalidCredentials);
        }
        if (account.LockedUntil is DateTime lockedUntil && lockedUntil > now)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(context, account, employeeNo, "OEM_LOGIN_FAILED", clientIp, null, ct);
            throw ApiException.Unauthorized(InvalidCredentials);
        }
        if (account.PasswordHash != candidate.PasswordHash)
            matches = await PasswordService.VerifyAsync(request.Password, account.PasswordHash, ct);
        if (!matches)
        {
            var lockExpired = account.LockedUntil is DateTime previous && previous <= now;
            var failures = lockExpired ? 1 : Math.Min(Math.Max(0, account.FailedLoginAttempts), IdentityService.MaximumFailedLogins) + 1;
            account.FailedLoginAttempts = failures;
            account.LockedUntil = failures >= IdentityService.MaximumFailedLogins ? now.AddMinutes(IdentityService.LoginLockMinutes) : null;
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var lockedNow = failures >= IdentityService.MaximumFailedLogins;
            await AuditBestEffortAsync(context, account, employeeNo, lockedNow ? "OEM_LOGIN_LOCKED" : "OEM_LOGIN_FAILED", clientIp,
                lockedNow ? new { failedAttempts = failures, lockMinutes = IdentityService.LoginLockMinutes } : null, ct);
            throw ApiException.Unauthorized(InvalidCredentials);
        }
        var company = await context.OemCompanies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == account.OemCompanyId, ct);
        if (account.Status != OemStatus.Active || company?.Status != OemStatus.Active)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(context, account, employeeNo, "OEM_LOGIN_FAILED", clientIp, null, ct);
            throw ApiException.Unauthorized(InvalidCredentials);
        }

        account.FailedLoginAttempts = 0;
        account.LockedUntil = null;
        account.LastLoginAt = now;
        account.LastLoginIp = clientIp;
        await context.SaveChangesAsync(ct);
        var sessionId = Guid.NewGuid().ToString();
        var refresh = await IssueRefreshAsync(context, account.Id, sessionId, clientIp, now, ct);
        var access = tokens.IssueAccess(account.Id, account.EmployeeNo, sessionId, OemRealms.Oem);
        await tx.CommitAsync(ct);
        await AuditBestEffortAsync(context, account, account.EmployeeNo, "OEM_LOGIN", clientIp, null, ct);
        return (new OemLoginResponse(access.Token, access.ExpiresAt, account.MustChangePassword, Brief(account, company)), refresh);
    }

    public async Task<(OemTokenResponse Response, string Refresh)> RefreshAsync(string refreshToken, string clientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw ApiException.Unauthorized("缺少登录凭证");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var hash = TokenService.HashRefreshToken(refreshToken);
        var found = await context.OemRefreshTokens.AsNoTracking().SingleOrDefaultAsync(token => token.TokenHash == hash, ct)
                    ?? throw ApiException.Unauthorized("登录状态无效");
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var account = await LockAccountAsync(context, found.AccountId, ct) ?? throw ApiException.Unauthorized("账号不存在");
        var row = await context.OemRefreshTokens.FromSqlInterpolated($"SELECT * FROM oem_refresh_tokens WHERE id = {found.Id} FOR UPDATE")
                      .SingleOrDefaultAsync(ct) ?? throw ApiException.Unauthorized("登录状态无效");
        var now = await OemClock.NowAsync(context, ct);
        if (row.Revoked || row.ExpiresAt <= now)
        {
            if (row.Revoked)
            {
                // Reuse of a rotated token: revoke the whole login family.
                await RevokeSessionAsync(context, row.AccountId, row.SessionId, ct);
                await tx.CommitAsync(ct);
                await AuditBestEffortAsync(context, account, account.EmployeeNo, "OEM_LOGIN_FAILED", clientIp,
                    new { reason = "refresh token reuse detected" }, ct);
            }
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }
        if (account.Status != OemStatus.Active) throw ApiException.Unauthorized("账号已被禁用");
        if (!await context.OemCompanies.AnyAsync(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active, ct))
            throw ApiException.Unauthorized("所属厂商已被禁用");
        row.Revoked = true;
        await context.SaveChangesAsync(ct);
        var next = await IssueRefreshAsync(context, account.Id, row.SessionId, clientIp, now, ct);
        var access = tokens.IssueAccess(account.Id, account.EmployeeNo, row.SessionId, OemRealms.Oem);
        await tx.CommitAsync(ct);
        return (new OemTokenResponse(access.Token, access.ExpiresAt), next);
    }

    public async Task LogoutAsync(string? refreshToken, string? authorization, string clientIp, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var now = await OemClock.NowAsync(context, ct);
        var targets = new HashSet<(ulong AccountId, string SessionId)>();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = TokenService.HashRefreshToken(refreshToken);
            var row = await context.OemRefreshTokens.AsNoTracking()
                .Where(token => token.TokenHash == hash && !token.Revoked && token.ExpiresAt > now)
                .Select(token => new { token.AccountId, token.SessionId }).SingleOrDefaultAsync(ct);
            if (row is not null) targets.Add((row.AccountId, row.SessionId));
        }
        if (authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true && authorization.Length > 7)
        {
            try
            {
                var claims = tokens.ParseAccess(authorization[7..]);
                if (claims.Realm == OemRealms.Oem && await HasActiveSessionAsync(context, claims.UserId, claims.SessionId, now, ct))
                    targets.Add((claims.UserId, claims.SessionId));
            }
            catch
            {
                // An invalid bearer never prevents clearing the refresh cookie.
            }
        }
        if (targets.Count == 0) return;
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        foreach (var target in targets.OrderBy(item => item.AccountId))
        {
            var account = await LockAccountAsync(context, target.AccountId, ct);
            if (account is null) continue;
            if (await RevokeSessionAsync(context, target.AccountId, target.SessionId, ct) > 0)
                await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), null, "OEM_LOGOUT", null, null,
                    null, clientIp, ct, null, RealmActor(account));
        }
        await tx.CommitAsync(ct);
    }

    public async Task ChangePasswordAsync(OemAccountActor actor, OemChangePasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.OldPassword)) throw ApiException.BadRequest("原密码错误");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var account = await LockAccountAsync(context, actor.AccountId, ct) ?? throw ApiException.NotFound();
        if (account.Status != OemStatus.Active) throw ApiException.Forbidden();
        if (!await PasswordService.VerifyAsync(request.OldPassword, account.PasswordHash, ct)) throw ApiException.BadRequest("原密码错误");
        if (string.Equals(request.OldPassword, request.NewPassword, StringComparison.Ordinal))
            throw ApiException.BadRequest("新密码不能与当前密码相同");
        PasswordService.Validate(request.NewPassword);
        account.PasswordHash = await PasswordService.HashAsync(request.NewPassword, ct);
        account.MustChangePassword = false;
        account.FailedLoginAttempts = 0;
        account.LockedUntil = null;
        account.UpdatedAt = await OemClock.NowAsync(context, ct);
        await context.SaveChangesAsync(ct);
        await RevokeAllAsync(context, account.Id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), null, "OEM_PASSWORD_CHANGE", null, null,
            null, null, ct, null, RealmActor(account));
        await tx.CommitAsync(ct);
    }

    public async Task<OemMeResponse> MeAsync(OemAccountActor actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var account = await context.OemAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actor.AccountId, ct)
                      ?? throw ApiException.NotFound();
        var company = await context.OemCompanies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == account.OemCompanyId, ct);
        return new OemMeResponse(Brief(account, company), account.MustChangePassword);
    }

    internal static async Task<bool> HasActiveSessionAsync(YfDbContext context, ulong accountId, string sessionId, DateTime now, CancellationToken ct) =>
        await context.OemRefreshTokens.AnyAsync(token => token.AccountId == accountId && token.SessionId == sessionId
            && !token.Revoked && token.ExpiresAt > now, ct);

    /// <summary>Revokes every refresh session of an account (disable, password reset/change).</summary>
    internal static Task<int> RevokeAllAsync(YfDbContext context, ulong accountId, CancellationToken ct) =>
        context.OemRefreshTokens.Where(token => token.AccountId == accountId && !token.Revoked)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.Revoked, true), ct);

    private static Task<int> RevokeSessionAsync(YfDbContext context, ulong accountId, string sessionId, CancellationToken ct) =>
        context.OemRefreshTokens.Where(token => token.AccountId == accountId && token.SessionId == sessionId && !token.Revoked)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.Revoked, true), ct);

    private static Task<OemAccount?> LockAccountAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        context.OemAccounts.FromSqlInterpolated($"SELECT * FROM oem_accounts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    private async Task<string> IssueRefreshAsync(YfDbContext context, ulong accountId, string sessionId, string clientIp, DateTime now, CancellationToken ct)
    {
        var raw = TokenService.NewRefreshToken();
        context.OemRefreshTokens.Add(new OemRefreshToken
        {
            AccountId = accountId,
            SessionId = sessionId,
            TokenHash = TokenService.HashRefreshToken(raw),
            ExpiresAt = now.AddDays(options.RefreshTtlDays),
            Revoked = false,
            Ip = clientIp,
            CreatedAt = now,
        });
        await context.SaveChangesAsync(ct);
        return raw;
    }

    private static OemAccountBrief Brief(OemAccount account, OemCompany? company) =>
        new(account.Id, account.EmployeeNo, account.RealName, account.Email, account.OemCompanyId, company?.Name ?? string.Empty);

    private static AuditRealmActor RealmActor(OemAccount account) =>
        new(OemRealms.Oem, account.Id, account.EmployeeNo, account.RealName);

    /// <summary>
    /// Writes a login audit row outside any transaction on the caller's already-open
    /// connection, so authentication never holds one pooled connection while waiting
    /// for another.
    /// </summary>
    private async Task AuditBestEffortAsync(YfDbContext context, OemAccount? account, string employeeNo, string action, string ip, object? detail, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(context.Database.Connection(), null, null, action, null, null, detail, ip, ct,
                account is null ? employeeNo : null, account is null ? null : RealmActor(account));
        }
        catch
        {
            // Authentication outcome never depends on audit availability.
        }
    }
}
