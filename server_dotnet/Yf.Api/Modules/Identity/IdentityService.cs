using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public sealed class IdentityService(
    AppDb db,
    AppOptions options,
    LoginRateLimiter loginRateLimiter,
    TokenService tokens,
    PermissionService permissions,
    AuditService audit)
{
    private const string UserSelect = """
        SELECT id Id,employee_no EmployeeNo,password_hash PasswordHash,real_name RealName,email Email,
               user_type UserType,supplier_id SupplierId,department_id DepartmentId,status Status,
               must_change_password MustChangePassword,last_login_at LastLoginAt,created_at CreatedAt
        FROM users
        """;
    private static readonly Lazy<Task<string>> DummyHash = new(() => CreateDummyHashAsync());
    internal const int MaximumFailedLogins = 10;
    internal const int LoginLockMinutes = 15;

    public async Task<(LoginResponse Response, string Refresh)> LoginAsync(LoginRequest request, string clientIp, CancellationToken ct)
    {
        var employeeNo = request.EmployeeNo?.Trim() ?? "";
        if (employeeNo.Length == 0 || string.IsNullOrEmpty(request.Password)) throw ApiException.BadRequest("工号和密码不能为空");
        if (employeeNo.EnumerateRunes().Count() > 64 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > PasswordService.MaxPasswordBytes)
            throw ApiException.BadRequest("工号或密码错误");
        if (!loginRateLimiter.AllowLogin(clientIp, employeeNo)) throw ApiException.BadRequest("请求过于频繁，请稍后再试");

        UserRow? candidate;
        await using (var lookup = await db.OpenAsync(ct))
            candidate = await lookup.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE employee_no=@employeeNo", new { employeeNo }, cancellationToken: ct));
        // Initialize the same-cost dummy on the first attempt even when the account exists, so the
        // first unknown-account request cannot be distinguished by the extra Argon2 calculation.
        var dummyHash = await DummyHash.Value;
        var matches = await PasswordService.VerifyAsync(request.Password, candidate?.PasswordHash ?? dummyHash, ct);
        if (candidate is null)
        {
            await using var auditConn = await db.OpenAsync(ct);
            await AuditBestEffortAsync(auditConn, candidate?.Id, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE id=@id FOR UPDATE", new { id = candidate.Id }, tx, cancellationToken: ct));
        if (user is null)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(conn, user?.Id, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        // The account row lock shares failure state across IPs, processes and restarts.
        // Use the database clock; attempts during a lock must not extend its expiry.
        var loginState = await conn.QuerySingleAsync<LoginState>(new CommandDefinition("""
            SELECT failed_login_attempts FailedAttempts,
                   COALESCE(locked_until>UTC_TIMESTAMP(6),0) IsLocked,
                   COALESCE(locked_until<=UTC_TIMESTAMP(6),0) LockExpired
            FROM users WHERE id=@Id
            """, new { user.Id }, tx, cancellationToken: ct));
        if (loginState.IsLocked)
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(conn, user.Id, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        // Recheck a concurrently reset password, but avoid hashing twice under a
        // database lock when the credential we just verified has not changed.
        if (user.PasswordHash != candidate.PasswordHash)
            matches = await PasswordService.VerifyAsync(request.Password, user.PasswordHash, ct);
        if (!matches)
        {
            var failures = loginState.LockExpired ? 1 : Math.Min(Math.Max(0, loginState.FailedAttempts), MaximumFailedLogins) + 1;
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE users SET failed_login_attempts=@failures,
                    locked_until=CASE WHEN @failures>=@limit THEN DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @minutes MINUTE) ELSE NULL END
                WHERE id=@Id
                """, new { failures, limit = MaximumFailedLogins, minutes = LoginLockMinutes, user.Id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            var lockedNow = failures >= MaximumFailedLogins;
            await AuditBestEffortAsync(conn, user.Id, employeeNo, lockedNow ? "LOGIN_LOCKED" : "LOGIN_FAILED",
                null, null, lockedNow ? new { failedAttempts = failures, lockMinutes = LoginLockMinutes } : null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        if (user.Status != "ACTIVE" || !await IsSupplierActiveAsync(conn, tx, user, ct))
        {
            await tx.RollbackAsync(ct);
            await AuditBestEffortAsync(conn, user.Id, employeeNo, "LOGIN_FAILED", null, null, null, clientIp, ct);
            throw ApiException.Unauthorized("工号或密码错误");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET failed_login_attempts=0,locked_until=NULL,last_login_at=UTC_TIMESTAMP(6),last_login_ip=@clientIp WHERE id=@id",
            new { clientIp, id = user.Id }, tx, cancellationToken: ct));
        var sessionId = Guid.NewGuid().ToString();
        var refresh = await IssueRefreshAsync(conn, tx, user.Id, sessionId, clientIp, ct);
        var accessToken = tokens.IssueAccess(user.Id, user.EmployeeNo, sessionId);
        var grants = await permissions.GetCodesAndMenusAsync(conn, tx, user.Id, ct);
        var response = new LoginResponse(accessToken.Token, accessToken.ExpiresAt, user.MustChangePassword,
            grants.Permissions, grants.Menus, await BriefAsync(conn, tx, user, ct));
        await tx.CommitAsync(ct);
        await AuditBestEffortAsync(conn, user.Id, user.EmployeeNo, "LOGIN", null, null, null, clientIp, ct);
        return (response, refresh);
    }

    public async Task<(TokenResponse Response, string Refresh)> RefreshAsync(string refreshToken, string clientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw ApiException.Unauthorized("缺少登录凭证");
        await using var conn = await db.OpenAsync(ct);
        var hash = TokenService.HashRefreshToken(refreshToken);
        var found = await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
            "SELECT id Id,user_id UserId,session_id SessionId,token_hash TokenHash,expires_at ExpiresAt,revoked Revoked FROM refresh_tokens WHERE token_hash=@hash",
            new { hash }, cancellationToken: ct)) ?? throw ApiException.Unauthorized("登录状态无效");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE id=@id FOR UPDATE", new { id = found.UserId }, tx, cancellationToken: ct))
                   ?? throw ApiException.Unauthorized("账号不存在");
        var row = await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
            "SELECT id Id,user_id UserId,session_id SessionId,token_hash TokenHash,expires_at ExpiresAt,revoked Revoked,COALESCE(expires_at<=UTC_TIMESTAMP(6),1) IsExpired FROM refresh_tokens WHERE id=@id FOR UPDATE",
            new { found.Id }, tx, cancellationToken: ct)) ?? throw ApiException.Unauthorized("登录状态无效");
        if (row.Revoked || row.IsExpired)
        {
            if (row.Revoked)
            {
                await RevokeSessionAsync(conn, tx, row.UserId, row.SessionId, ct);
                await tx.CommitAsync(ct);
                await AuditBestEffortAsync(conn, row.UserId, null, "LOGIN_FAILED", "refresh_token", null,
                    new { reason = "refresh token reuse detected" }, clientIp, ct);
            }
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }
        if (user.Status != "ACTIVE") throw ApiException.Unauthorized("账号已被禁用");
        if (!await IsSupplierActiveAsync(conn, tx, user, ct)) throw ApiException.Unauthorized("所属供应商已被禁用");
        var changed = await conn.ExecuteAsync(new CommandDefinition("UPDATE refresh_tokens SET revoked=1 WHERE id=@id AND revoked=0", new { row.Id }, tx, cancellationToken: ct));
        if (changed != 1) throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        var next = await IssueRefreshAsync(conn, tx, user.Id, row.SessionId, clientIp, ct);
        var accessToken = tokens.IssueAccess(user.Id, user.EmployeeNo, row.SessionId);
        await tx.CommitAsync(ct);
        return (new(accessToken.Token, accessToken.ExpiresAt), next);
    }

    public async Task LogoutAsync(string? refreshToken, string? authorization, string clientIp, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var targets = new HashSet<(ulong UserId, string SessionId)>();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var row = await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
                "SELECT user_id UserId,session_id SessionId FROM refresh_tokens WHERE token_hash=@hash AND revoked=0 AND expires_at>UTC_TIMESTAMP(6)",
                new { hash = TokenService.HashRefreshToken(refreshToken) }, cancellationToken: ct));
            if (row is not null) targets.Add((row.UserId, row.SessionId));
        }
        if (TryBearer(authorization, out var bearer))
        {
            try
            {
                var claim = tokens.ParseAccess(bearer);
                if (await HasActiveSessionAsync(conn, null, claim.UserId, claim.SessionId, ct)) targets.Add((claim.UserId, claim.SessionId));
            }
            catch { }
        }
        if (targets.Count == 0) return;
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        foreach (var uid in targets.Select(x => x.UserId).Distinct().Order())
            await conn.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition("SELECT id FROM users WHERE id=@uid FOR UPDATE", new { uid }, tx, cancellationToken: ct));
        foreach (var target in targets)
        {
            if (await RevokeSessionAsync(conn, tx, target.UserId, target.SessionId, ct) > 0)
                await audit.WriteAsync(conn, tx, target.UserId, "LOGOUT", null, null, null, clientIp, ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ChangePasswordAsync(CurrentUser current, ChangePasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.OldPassword)) throw ApiException.BadRequest("原密码错误");
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE id=@id FOR UPDATE", new { id = current.Id }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        if (user.Status != "ACTIVE") throw ApiException.Forbidden();
        if (!await PasswordService.VerifyAsync(request.OldPassword, user.PasswordHash, ct)) throw ApiException.BadRequest("原密码错误");
        if (string.Equals(request.OldPassword, request.NewPassword, StringComparison.Ordinal))
            throw ApiException.BadRequest("新密码不能与当前密码相同");
        PasswordService.Validate(request.NewPassword);
        var hash = await PasswordService.HashAsync(request.NewPassword, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET password_hash=@hash,must_change_password=0,failed_login_attempts=0,locked_until=NULL,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { hash, id = current.Id }, tx, cancellationToken: ct));
        await RevokeAllAsync(conn, tx, current.Id, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PASSWORD_CHANGE", null, null, null, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ProfileResponse> ProfileAsync(CurrentUser current, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE id=@id", new { id = current.Id }, cancellationToken: ct)) ?? throw ApiException.NotFound();
        var grants = await permissions.GetCodesAndMenusAsync(conn, null, user.Id, ct);
        return new(await BriefAsync(conn, null, user, ct), user.MustChangePassword, grants.Permissions, grants.Menus);
    }

    public async Task<ProfileResponse> UpdateProfileAsync(CurrentUser current, UpdateProfileRequest request, CancellationToken ct)
    {
        ValidateEmail(request.Email);
        var email = request.Email.Trim();
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(UserSelect + " WHERE id=@id FOR UPDATE", new { id = current.Id }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        if (user.Status != "ACTIVE") throw ApiException.Forbidden();
        if (user.Email != email)
        {
            await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET email=@email,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", new { email, id = current.Id }, tx, cancellationToken: ct));
            await audit.WriteAsync(conn, tx, current.Id, "PROFILE_UPDATE", "user", current.Id, new
            {
                changedFields = new[] { "email" },
                changes = AuditChange.OnlyChanged(new AuditChange("email", "邮箱", user.Email, email))
            }, null, ct);
        }
        var updated = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserSelect + " WHERE id=@id", new { id = current.Id }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        var grants = await permissions.GetCodesAndMenusAsync(conn, tx, updated.Id, ct);
        var response = new ProfileResponse(await BriefAsync(conn, tx, updated, ct), updated.MustChangePassword,
            grants.Permissions, grants.Menus);
        await tx.CommitAsync(ct);
        return response;
    }

    internal async Task<bool> HasActiveSessionAsync(MySqlConnection conn, MySqlTransaction? tx, ulong userId, string sessionId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM refresh_tokens WHERE user_id=@userId AND session_id=@sessionId AND revoked=0 AND expires_at>UTC_TIMESTAMP(6))",
            new { userId, sessionId }, tx, cancellationToken: ct)) == 1;

    internal static Task RevokeAllAsync(MySqlConnection conn, MySqlTransaction tx, ulong userId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("UPDATE refresh_tokens SET revoked=1 WHERE user_id=@userId", new { userId }, tx, cancellationToken: ct));

    private async Task<string> IssueRefreshAsync(MySqlConnection conn, MySqlTransaction tx, ulong userId, string sessionId, string clientIp, CancellationToken ct)
    {
        var raw = TokenService.NewRefreshToken();
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO refresh_tokens(user_id,session_id,token_hash,expires_at,revoked,ip,created_at) VALUES(@userId,@sessionId,@hash,DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @days DAY),0,@clientIp,UTC_TIMESTAMP(6))",
            new { userId, sessionId, hash = TokenService.HashRefreshToken(raw), days = options.RefreshTtlDays, clientIp }, tx, cancellationToken: ct));
        return raw;
    }

    private static Task<int> RevokeSessionAsync(MySqlConnection conn, MySqlTransaction tx, ulong userId, string sessionId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("UPDATE refresh_tokens SET revoked=1 WHERE user_id=@userId AND session_id=@sessionId AND revoked=0", new { userId, sessionId }, tx, cancellationToken: ct));

    private static async Task<bool> IsSupplierActiveAsync(MySqlConnection conn, MySqlTransaction tx, UserRow user, CancellationToken ct)
    {
        if (user.UserType == "INTERNAL") return true;
        return user.SupplierId is ulong id && await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM suppliers WHERE id=@id AND status='ACTIVE')", new { id }, tx, cancellationToken: ct)) == 1;
    }

    private async Task<UserBrief> BriefAsync(MySqlConnection conn, MySqlTransaction? tx, UserRow user, CancellationToken ct) =>
        new(user.Id, user.EmployeeNo, user.RealName, user.Email, user.UserType, user.SupplierId,
            user.Status == "ACTIVE" && user.UserType == "INTERNAL" && await AccessService.IsSystemAdminAsync(conn, tx, user.Id, ct));

    private static void ValidateEmail(string? email)
    {
        var value = email?.Trim() ?? "";
        if (value.EnumerateRunes().Count() > 128 || !System.Net.Mail.MailAddress.TryCreate(value, out _)) throw ApiException.BadRequest("邮箱格式不正确或超过 128 字符");
    }
    private static bool TryBearer(string? header, out string token) { token = ""; if (header?.StartsWith("Bearer ", StringComparison.Ordinal) != true) return false; token = header[7..]; return token.Length > 0; }
    private static async Task<string> CreateDummyHashAsync()
    {
        var salt = System.Text.Encoding.ASCII.GetBytes("yf-login-dummy-salt");
        var argon = new Konscious.Security.Cryptography.Argon2id(System.Text.Encoding.UTF8.GetBytes("dummy-login#2026")) { Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1 };
        return $"$argon2id$v=19$m=19456,t=2,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(await argon.GetBytesAsync(32)).TrimEnd('=')}";
    }

    private async Task AuditBestEffortAsync(MySqlConnection conn, ulong? userId, string? employeeNo, string action,
        string? targetType, ulong? targetId, object? detail, string? ip, CancellationToken ct)
    {
        try { await audit.WriteAsync(conn, null, userId, action, targetType, targetId, detail, ip, ct, employeeNo); } catch { }
    }

    private sealed class LoginState
    {
        public int FailedAttempts { get; init; }
        public bool IsLocked { get; init; }
        public bool LockExpired { get; init; }
    }
}
