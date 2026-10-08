using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Identity;

public sealed class IdentityService(
    IDbContextFactory<YfDbContext> dbFactory,
    AppOptions options,
    LoginRateLimiter loginRateLimiter,
    TokenService tokens,
    PermissionService permissions,
    AuditService audit,
    ILogger<IdentityService>? logger = null)
{
    public async Task<(LoginResponse Response, string Refresh, DateTime RefreshExpiresAt)> LoginAsync(LoginRequest request, string clientIp, CancellationToken ct)
    {
        var employeeNo = request.EmployeeNo?.Trim() ?? "";
        if (employeeNo.Length == 0 || string.IsNullOrEmpty(request.Password)) throw ApiException.BadRequest("工号和密码不能为空");
        if (employeeNo.EnumerateRunes().Count() > 64 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > PasswordService.MaxPasswordBytes)
            throw ApiException.BadRequest("工号或密码错误");
        if (!loginRateLimiter.AllowLogin(clientIp, employeeNo)) throw ApiException.TooManyRequests("请求过于频繁，请稍后再试");

        User? candidate;
        await using (var lookup = await dbFactory.CreateDbContextAsync(ct))
            candidate = await lookup.Users.AsNoTracking().SingleOrDefaultAsync(u => u.EmployeeNo == employeeNo, ct);
        if (!loginRateLimiter.AllowAccountLogin(IdentityRealms.Internal, candidate?.Id, employeeNo, clientIp))
            throw ApiException.TooManyRequests("请求过于频繁，请稍后再试");
        // Initialize the same-cost dummy on the first attempt even when the account exists, so the
        // first unknown-account request cannot be distinguished by the extra Argon2 calculation.
        var dummyHash = await PasswordService.TimingDummyHashAsync();
        var matches = await PasswordService.VerifyAsync(request.Password, candidate?.PasswordHash ?? dummyHash, ct);
        if (candidate is null)
        {
            await using var auditCtx = await dbFactory.CreateDbContextAsync(ct);
            // No EF query has touched auditCtx yet, so open the connection explicitly
            // before passing it to the shared non-owning audit context.
            await auditCtx.Database.OpenConnectionAsync(ct);
            await AuditBestEffortAsync(auditCtx.Database.Connection(), null, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var user = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {candidate.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is null)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(context.Database.Connection(), null, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        // The account row lock keeps the credential recheck, status check and session issue
        // atomic with concurrent password resets and account-management changes. Failed
        // passwords are throttled in memory by source/IP; they do not persist victim-controlled
        // failure counts or account locks.
        var dbNow = await DbClock.UtcNowAsync(context, ct);
        // Recheck a concurrently reset password, but avoid hashing twice under a
        // database lock when the credential we just verified has not changed.
        if (user.PasswordHash != candidate.PasswordHash)
            matches = await PasswordService.VerifyAsync(request.Password, user.PasswordHash, ct);
        if (!matches)
        {
            // Normalize legacy lockout state without accumulating unauthenticated input.
            // The columns remain for schema compatibility and administrative reset flows.
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await AuditBestEffortAsync(context.Database.Connection(), user.Id, employeeNo, "LOGIN_FAILED",
                null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        if (user.Status != AccountStatuses.Active || !await IsSupplierActiveAsync(context, user, ct))
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(context.Database.Connection(), user.Id, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = dbNow;
        user.LastLoginIp = clientIp;
        await context.SaveChangesAsync(ct);
        var sessionId = Guid.NewGuid().ToString();
        var sessionExpiresAt = dbNow.AddDays(options.AbsoluteSessionLifetimeDays);
        var refresh = await IssueRefreshAsync(context, user.Id, sessionId, clientIp, dbNow,
            dbNow, sessionExpiresAt, ct);
        var evictedSessions = await EnforceSessionCapAsync(context, user.Id, dbNow, ct);
        var accessToken = tokens.IssueAccess(user.Id, user.EmployeeNo, sessionId);
        var grants = await permissions.GetCodesAndMenusAsync(context.Database.Connection(), context.Database.Transaction(), user.Id, ct);
        var response = new LoginResponse(accessToken.Token, accessToken.ExpiresAt, user.MustChangePassword,
            grants.Permissions, grants.Menus, await BriefAsync(context, user, ct));
        await tx.CommitAsync(ct);
        loginRateLimiter.RecordSuccessfulLogin(IdentityRealms.Internal, user.Id, clientIp);
        await AuditBestEffortAsync(context.Database.Connection(), user.Id, user.EmployeeNo, "LOGIN", null, null,
            evictedSessions > 0 ? new { evictedSessions, reason = RefreshRevokeReasons.SessionCap } : null, clientIp, ct);
        return (response, refresh.Token, refresh.ExpiresAt);
    }

    public async Task<(LoginResponse Response, string Refresh, DateTime RefreshExpiresAt)> RefreshAsync(string refreshToken, string clientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw ApiException.Unauthorized("缺少登录凭证");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var hash = TokenService.HashRefreshToken(refreshToken);
        var found = await context.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct)
                    ?? throw ApiException.Unauthorized("登录状态无效");
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var user = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {found.UserId} FOR UPDATE").SingleOrDefaultAsync(ct)
                   ?? throw ApiException.Unauthorized("账号不存在");
        var row = await context.RefreshTokens.FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE id = {found.Id} FOR UPDATE").SingleOrDefaultAsync(ct)
                  ?? throw ApiException.Unauthorized("登录状态无效");
        var dbNow = await DbClock.UtcNowAsync(context, ct);
        var isExpired = row.ExpiresAt <= dbNow || row.SessionExpiresAt <= dbNow;
        if (row.Revoked || isExpired)
        {
            if (row.Revoked)
            {
                // Presenting any revoked token kills what is left of its family, but only reuse of a
                // rotated token is a replay signal; a token revoked by logout, password change, an
                // administrator or the session cap is simply stale (e.g. an old tab).
                var replay = row.RevokeReason == RefreshRevokeReasons.Rotated;
                await RevokeSessionAsync(context, row.UserId, row.SessionId,
                    replay ? RefreshRevokeReasons.Replay : row.RevokeReason ?? RefreshRevokeReasons.Replay, ct);
                await tx.CommitAsync(ct);
                if (replay)
                    await AuditBestEffortAsync(context.Database.Connection(), row.UserId, null, "LOGIN_FAILED", "refresh_token", null,
                        new { reason = "refresh token reuse detected" }, clientIp, ct);
            }
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }
        if (user.Status != AccountStatuses.Active) throw ApiException.Unauthorized("账号已被禁用");
        if (!await IsSupplierActiveAsync(context, user, ct)) throw ApiException.Unauthorized("所属供应商已被禁用");
        // Already exclusively locked by the FOR UPDATE read above, so nothing else
        // could have raced this revoke between that read and this write.
        row.Revoked = true;
        row.RevokeReason = RefreshRevokeReasons.Rotated;
        await context.SaveChangesAsync(ct);
        var next = await IssueRefreshAsync(context, user.Id, row.SessionId, clientIp, dbNow,
            row.SessionCreatedAt, row.SessionExpiresAt, ct);
        var accessToken = tokens.IssueAccess(user.Id, user.EmployeeNo, row.SessionId);
        var grants = await permissions.GetCodesAndMenusAsync(context.Database.Connection(), context.Database.Transaction(), user.Id, ct);
        var response = new LoginResponse(accessToken.Token, accessToken.ExpiresAt, user.MustChangePassword,
            grants.Permissions, grants.Menus, await BriefAsync(context, user, ct));
        await tx.CommitAsync(ct);
        return (response, next.Token, next.ExpiresAt);
    }

    public async Task LogoutAsync(string? refreshToken, string? authorization, string clientIp, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var dbNow = await DbClock.UtcNowAsync(context, ct);
        var targets = new HashSet<(ulong UserId, string SessionId)>();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = TokenService.HashRefreshToken(refreshToken);
            var row = await context.RefreshTokens.AsNoTracking()
                .Where(t => t.TokenHash == hash && !t.Revoked && t.ExpiresAt > dbNow)
                .Select(t => new { t.UserId, t.SessionId }).SingleOrDefaultAsync(ct);
            if (row is not null) targets.Add((row.UserId, row.SessionId));
        }
        if (TryBearer(authorization, out var bearer))
        {
            try
            {
                var claim = tokens.ParseAccess(bearer);
                // Ids of another realm (e.g. an OEM account) live in a different table and must
                // never be mistaken for an internal user's session.
                if (claim.Realm == IdentityRealms.Internal
                    && await HasActiveSessionAsync(context.Database.Connection(), null, claim.UserId, claim.SessionId, ct))
                    targets.Add((claim.UserId, claim.SessionId));
            }
            catch (Exception ex) when (ex is ApiException or SecurityTokenException or ArgumentException or FormatException)
            {
                logger?.LogDebug(ex, "Ignoring an invalid access token supplied during logout");
            }
        }
        if (targets.Count == 0) return;
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        foreach (var uid in targets.Select(x => x.UserId).Distinct().Order())
            await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {uid} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
        foreach (var target in targets)
        {
            if (await RevokeSessionAsync(context, target.UserId, target.SessionId, RefreshRevokeReasons.Logout, ct) > 0)
                await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), target.UserId, "LOGOUT", null, null, null, clientIp, ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ChangePasswordAsync(CurrentUser current, ChangePasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.OldPassword)) throw ApiException.BadRequest("原密码错误");
        if (!loginRateLimiter.AllowPasswordChange(current.Id))
            throw ApiException.PasswordRateLimited("密码校验尝试过于频繁，请稍后再试");
        if (string.Equals(request.OldPassword, request.NewPassword, StringComparison.Ordinal))
            throw ApiException.BadRequest("新密码不能与当前密码相同");
        PasswordService.Validate(request.NewPassword);

        User snapshot;
        await using (var lookup = await dbFactory.CreateDbContextAsync(ct))
            snapshot = await lookup.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == current.Id, ct) ?? throw ApiException.NotFound();
        if (!await PasswordService.VerifyForPasswordChangeAsync(request.OldPassword, snapshot.PasswordHash, ct))
            throw ApiException.BadRequest("原密码错误");
        var replacementHash = await PasswordService.HashAsync(request.NewPassword, ct);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockManagementAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var user = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {current.Id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (user.Status != AccountStatuses.Active || !await IsSupplierActiveAsync(context, user, ct)
            || current.SessionId is string sessionId && !await HasActiveSessionAsync(
                context.Database.Connection(), context.Database.RequireTransaction(), current.Id, sessionId, ct))
            throw ApiException.Forbidden();
        if (!string.Equals(user.PasswordHash, snapshot.PasswordHash, StringComparison.Ordinal))
            throw ApiException.Conflict("密码已被其他操作修改，请重新登录");
        user.PasswordHash = replacementHash;
        user.MustChangePassword = false;
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await context.SaveChangesAsync(ct);
        await RevokeAllAsync(context.Database.Connection(), context.Database.RequireTransaction(), current.Id,
            RefreshRevokeReasons.PasswordChanged, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), current.Id, "PASSWORD_CHANGE", null, null, null, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ProfileResponse> ProfileAsync(CurrentUser current, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == current.Id, ct) ?? throw ApiException.NotFound();
        var grants = await permissions.GetCodesAndMenusAsync(context.Database.Connection(), null, user.Id, ct);
        return new(await BriefAsync(context, user, ct), user.MustChangePassword, grants.Permissions, grants.Menus);
    }

    public async Task<ProfileResponse> UpdateProfileAsync(CurrentUser current, UpdateProfileRequest request, CancellationToken ct)
    {
        ValidateEmail(request.Email);
        var email = request.Email.Trim();
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var user = await context.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {current.Id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (user.Status != AccountStatuses.Active || !await IsSupplierActiveAsync(context, user, ct)
            || current.SessionId is string sessionId && !await HasActiveSessionAsync(
                context.Database.Connection(), context.Database.RequireTransaction(), current.Id, sessionId, ct))
            throw ApiException.Forbidden();
        if (user.Email != email)
        {
            var oldEmail = user.Email;
            user.Email = email;
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.Transaction(), current.Id, "PROFILE_UPDATE", "user", current.Id, new
            {
                changedFields = new[] { "email" },
                changes = AuditChange.OnlyChanged(new AuditChange("email", "邮箱", oldEmail, email))
            }, null, ct);
        }
        var grants = await permissions.GetCodesAndMenusAsync(context.Database.Connection(), context.Database.Transaction(), user.Id, ct);
        var response = new ProfileResponse(await BriefAsync(context, user, ct), user.MustChangePassword,
            grants.Permissions, grants.Menus);
        await tx.CommitAsync(ct);
        return response;
    }

    // Public contract shared with Admin, IdentityMiddleware and Files. The caller
    // continues to own the connection and optional transaction.
    internal async Task<bool> HasActiveSessionAsync(MySqlConnection conn, MySqlTransaction? tx, ulong userId, string sessionId, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        var dbNow = await DbClock.UtcNowAsync(context, ct);
        return await context.RefreshTokens.AnyAsync(token => token.UserId == userId && token.SessionId == sessionId
            && !token.Revoked && token.ExpiresAt > dbNow && token.SessionExpiresAt > dbNow, ct);
    }

    internal static async Task RevokeAllAsync(MySqlConnection conn, MySqlTransaction tx, ulong userId, string reason, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        // Already revoked rows keep their original reason so a later replay is still classified correctly.
        await context.RefreshTokens.Where(token => token.UserId == userId && !token.Revoked)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.Revoked, true)
                .SetProperty(token => token.RevokeReason, reason), ct);
    }

    /// <summary>
    /// Revokes the oldest still-active session families of <paramref name="userId"/> beyond
    /// App:MaxActiveSessionsPerUser. Callers hold the user row lock, so concurrent logins serialize here.
    /// </summary>
    private async Task<int> EnforceSessionCapAsync(YfDbContext context, ulong userId, DateTime dbNow, CancellationToken ct)
    {
        var active = await context.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && !t.Revoked && t.ExpiresAt > dbNow && t.SessionExpiresAt > dbNow)
            .Select(t => t.SessionId).Distinct().ToListAsync(ct);
        if (active.Count <= options.MaxActiveSessionsPerUser) return 0;
        // Order families by when the session began; session_created_at has second precision,
        // so break ties with the family's first token id (rotation does not make a family newer).
        var families = await context.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && active.Contains(t.SessionId))
            .GroupBy(t => t.SessionId)
            .Select(g => new { SessionId = g.Key, CreatedAt = g.Min(t => t.SessionCreatedAt), FirstId = g.Min(t => t.Id) })
            .ToListAsync(ct);
        var evicted = families.OrderBy(x => x.CreatedAt).ThenBy(x => x.FirstId)
            .Take(families.Count - options.MaxActiveSessionsPerUser).Select(x => x.SessionId).ToArray();
        await context.RefreshTokens.Where(t => t.UserId == userId && !t.Revoked && evicted.Contains(t.SessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true)
                .SetProperty(t => t.RevokeReason, RefreshRevokeReasons.SessionCap), ct);
        return evicted.Length;
    }

    private async Task<IssuedRefresh> IssueRefreshAsync(YfDbContext context, ulong userId, string sessionId, string clientIp,
        DateTime dbNow, DateTime sessionCreatedAt, DateTime sessionExpiresAt, CancellationToken ct)
    {
        var raw = TokenService.NewRefreshToken();
        var expiresAt = Min(dbNow.AddDays(options.RefreshTtlDays), sessionExpiresAt);
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            SessionId = sessionId,
            TokenHash = TokenService.HashRefreshToken(raw),
            SessionCreatedAt = sessionCreatedAt,
            SessionExpiresAt = sessionExpiresAt,
            ExpiresAt = expiresAt,
            Revoked = false,
            Ip = clientIp,
            CreatedAt = dbNow
        });
        await context.SaveChangesAsync(ct);
        return new(raw, expiresAt);
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;

    private static async Task<int> RevokeSessionAsync(YfDbContext context, ulong userId, string sessionId, string reason, CancellationToken ct) =>
        await context.RefreshTokens.Where(t => t.UserId == userId && t.SessionId == sessionId && !t.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Revoked, true).SetProperty(t => t.RevokeReason, reason), ct);

    private static async Task<bool> IsSupplierActiveAsync(YfDbContext context, User user, CancellationToken ct)
    {
        if (user.UserType == UserTypes.Internal) return true;
        return user.SupplierId is ulong id && await context.Suppliers.AnyAsync(s => s.Id == id && s.Status == AccountStatuses.Active, ct);
    }

    private async Task<UserBrief> BriefAsync(YfDbContext context, User user, CancellationToken ct) =>
        new(user.Id, user.EmployeeNo, user.RealName, user.Email, user.UserType, user.SupplierId,
            user.Status == AccountStatuses.Active && user.UserType == UserTypes.Internal
                && await AccessService.IsSystemAdminAsync(context.Database.Connection(), context.Database.Transaction(), user.Id, ct));

    private static void ValidateEmail(string? email)
    {
        var value = email?.Trim() ?? "";
        if (value.EnumerateRunes().Count() > 128 || !System.Net.Mail.MailAddress.TryCreate(value, out _)) throw ApiException.BadRequest("邮箱格式不正确或超过 128 字符");
    }
    private static bool TryBearer(string? header, out string token) { token = ""; if (header?.StartsWith("Bearer ", StringComparison.Ordinal) != true) return false; token = header[7..]; return token.Length > 0; }

    private async Task AuditBestEffortAsync(MySqlConnection conn, ulong? userId, string? employeeNo, string action,
        string? targetType, ulong? targetId, object? detail, string? ip, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(conn, null, userId, action, targetType, targetId, detail, ip, ct, employeeNo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Best-effort identity audit failed for {Action}", action);
        }
    }

    private sealed record IssuedRefresh(string Token, DateTime ExpiresAt);
}
