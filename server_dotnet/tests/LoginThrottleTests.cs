using System.Text;
using System.Text.Json;
using Dapper;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class LoginThrottleTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnchangedPasswordIsRejectedWithoutChangingAccountOrSessions(bool firstLogin)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        await scope.LoginAsync("target", scope.Password, "192.0.2.240", ct);
        await scope.ExecuteAsync($"UPDATE users SET must_change_password={(firstLogin ? 1 : 0)},failed_login_attempts=3 WHERE id=1", ct);
        await using var conn = await scope.OpenAsync(ct);
        async Task<string> SnapshotAsync()
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition("""
                SELECT password_hash,must_change_password,failed_login_attempts,locked_until,updated_at,
                  (SELECT COUNT(*) FROM audit_logs) AS audit_count,
                  (SELECT COUNT(*) FROM refresh_tokens WHERE user_id=1 AND revoked=0) AS sessions
                FROM users WHERE id=1
                """, cancellationToken: ct));
            return JsonSerializer.Serialize((IDictionary<string, object>)row);
        }
        var before = await SnapshotAsync();
        var user = new CurrentUser(1, "target", "INTERNAL", null);

        var error = await Assert.ThrowsAsync<ApiException>(() => scope.Service().ChangePasswordAsync(
            user, new(scope.Password, scope.Password), ct));

        Assert.Equal(400, error.Status);
        Assert.Equal("新密码不能与当前密码相同", error.Message);
        // Keep credentials out of failure output while checking every persisted field.
        Assert.True(before == await SnapshotAsync(), "Rejected change must leave account, audit and sessions unchanged");
        Assert.Equal(1, await scope.SessionsAsync(1, ct));
        Assert.Equal(firstLogin, (await scope.LoginAsync("target", scope.Password, "192.0.2.241", ct)).MustChangePassword);

        const string replacement = "Different#2026";
        await scope.Service().ChangePasswordAsync(user, new(scope.Password, replacement), ct);
        Assert.Equal(0, await scope.SessionsAsync(1, ct));
        await scope.RejectAsync("target", scope.Password, "192.0.2.242", ct);
        Assert.False((await scope.LoginAsync("target", replacement, "192.0.2.243", ct)).MustChangePassword);
    }

    [Fact(Timeout = 120_000)]
    public async Task RepeatedOldPasswordChecksAreThrottledBeforeHashing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        var service = scope.Service(new LoginRateLimiter());
        var user = new CurrentUser(1, "target", "INTERNAL", null);
        for (var attempt = 0; attempt < LoginRateLimiter.MaximumPasswordChangeAttempts; attempt++)
        {
            var wrong = await Assert.ThrowsAsync<ApiException>(() => service.ChangePasswordAsync(
                user, new(scope.Password + "wrong", "Throttle#2026"), ct));
            Assert.Equal("原密码错误", wrong.Message);
        }

        // Even the correct old password is refused while the account's window is exhausted.
        var limited = await Assert.ThrowsAsync<ApiException>(() => service.ChangePasswordAsync(
            user, new(scope.Password, "Throttle#2026"), ct));
        Assert.Equal(429, limited.Status);
        Assert.Equal(42901, limited.Code);
        Assert.Equal(1UL, (await scope.LoginAsync("target", scope.Password, "192.0.2.230", ct)).User.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task LoginWithoutCaptchaAcceptsExistingLongPasswordWithoutForcingReset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);

        var response = await scope.LoginAsync("legacy", scope.LegacyPassword, "192.0.2.250", ct);

        Assert.Equal(3UL, response.User.Id);
        Assert.False(response.MustChangePassword);
    }

    [Fact(Timeout = 120_000)]
    public async Task WrongPasswordsDoNotPersistFailureStateAndLegacyLocksDoNotBlockCorrectLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        for (var attempt = 0; attempt < 10; attempt++)
            await scope.RejectAsync(attempt % 2 == 0 ? "target" : "TARGET", scope.Password + "wrong", "192.0.2." + (attempt + 1), ct);

        var failed = await scope.StateAsync(ct);
        Assert.Equal(0, failed.Failures);
        Assert.Null(failed.LockedUntil);
        Assert.Equal(0, await scope.SessionsAsync(1, ct));
        await using (var audit = await scope.OpenAsync(ct))
        {
            Assert.Equal(10, await audit.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN_FAILED' AND user_id=1", cancellationToken: ct)));
            Assert.Equal(0, await audit.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN_LOCKED' AND user_id=1", cancellationToken: ct)));
        }
        Assert.Equal(2UL, (await scope.LoginAsync("unaffected", scope.Password, "198.51.100.3", ct)).User.Id);

        // Existing rows from the former policy are compatibility data, not an authentication gate.
        await scope.ExecuteAsync("UPDATE users SET failed_login_attempts=2147483647,locked_until=UTC_TIMESTAMP()+INTERVAL 1 DAY WHERE id=1", ct);
        Assert.Equal(1UL, (await scope.LoginAsync("target", scope.Password, "203.0.113.2", ct)).User.Id);
        Assert.Equal(0, (await scope.StateAsync(ct)).Failures);
        Assert.Null((await scope.StateAsync(ct)).LockedUntil);
    }

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentFailuresDoNotPersistAndAuthenticatedPasswordChangeStillWins()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        await scope.LoginAsync("target", scope.Password, "192.0.2.100", ct);
        await Task.WhenAll(Enumerable.Range(1, 14).Select(attempt =>
            scope.RejectAsync("target", scope.Password + "wrong", "198.51.100." + attempt, ct)));
        Assert.Equal(0, (await scope.StateAsync(ct)).Failures);
        Assert.Null((await scope.StateAsync(ct)).LockedUntil);

        const string replacement = "NextLogin#2026";
        await scope.Service().ChangePasswordAsync(
            new CurrentUser(1, "target", "INTERNAL", null), new(scope.Password, replacement), ct);
        Assert.Equal(0, (await scope.StateAsync(ct)).Failures);
        Assert.Null((await scope.StateAsync(ct)).LockedUntil);
        Assert.Equal(0, await scope.SessionsAsync(1, ct));
        await scope.RejectAsync("target", scope.Password, "203.0.113.101", ct);
        Assert.Equal(1UL, (await scope.LoginAsync("target", replacement, "203.0.113.102", ct)).User.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task PasswordChangedAfterCandidateLookupCannotIssueAStaleSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        await using var gate = await scope.OpenAsync(ct);
        await using var transaction = await AppDb.BeginTransactionAsync(gate, ct);
        await AccessService.LockManagementAsync(gate, transaction, ct);
        var oldLogin = scope.RejectAsync("target", scope.Password, "192.0.2.200", ct);
        var released = false;
        try
        {
            // A waiting management lock proves login has already loaded and hashed
            // its candidate. Apply the concurrent credential change while this test owns
            // that same gate, preserving the production gate -> user -> token lock order.
            await scope.WaitForBlockedLoginAsync(ct);
            const string replacement = "Concurrent#2026";
            await gate.ExecuteAsync(new CommandDefinition("""
                UPDATE users SET password_hash=@hash,must_change_password=0 WHERE id=1;
                UPDATE refresh_tokens SET revoked=1 WHERE user_id=1 AND revoked=0;
                """, new { hash = await PasswordService.HashAsync(replacement, ct) }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            released = true;
            await oldLogin;
            Assert.Equal(0, await scope.SessionsAsync(1, ct));
            Assert.Equal(1UL, (await scope.LoginAsync("target", replacement, "192.0.2.201", ct)).User.Id);
        }
        finally
        {
            if (!released) await transaction.RollbackAsync(CancellationToken.None);
            if (!released)
            {
                // Preserve a failed synchronization assertion rather than replacing it
                // with the result of the login released during fixture cleanup.
                try { await oldLogin; } catch { }
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentPasswordResetMakesAnInFlightSelfChangeFailWithoutOverwritingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        await using var management = await scope.OpenAsync(ct);
        await using var managementTx = await AppDb.BeginTransactionAsync(management, ct);
        await AccessService.LockManagementAsync(management, managementTx, ct);

        var change = scope.Service().ChangePasswordAsync(
            new CurrentUser(1, "target", UserTypes.Internal, null),
            new(scope.Password, "SelfChange#2026"), ct);
        await scope.WaitForBlockedManagementAsync(ct);
        const string concurrentPassword = "AdminReset#2026";
        var concurrentHash = await PasswordService.HashAsync(concurrentPassword, ct);
        await management.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET password_hash=@hash WHERE id=1", new { hash = concurrentHash }, managementTx,
            cancellationToken: ct));
        await managementTx.CommitAsync(ct);

        var conflict = await Assert.ThrowsAsync<ApiException>(() => change);
        Assert.Equal(40901, conflict.Code);
        Assert.Equal(1UL, (await scope.LoginAsync("target", concurrentPassword, "192.0.2.219", ct)).User.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task LogoutSharesTheBusinessGateAndLaterWritesRejectTheRevokedSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        var login = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.220", ct);
        var state = await scope.RefreshStateAsync(login.Refresh, ct);
        var actor = new CurrentUser(1, "target", UserTypes.Internal, null, state.SessionId);

        await using var business = await scope.OpenAsync(ct);
        await using var businessTx = await AppDb.BeginTransactionAsync(business, ct);
        var checkedActor = await AccessService.LockActorAsync(business, businessTx, actor, ct);
        Assert.Equal(state.SessionId, checkedActor.SessionId);

        var logout = scope.Service().LogoutAsync(login.Refresh, null, "192.0.2.221", ct);
        await logout.WaitAsync(TimeSpan.FromSeconds(15), ct);

        await businessTx.CommitAsync(ct);

        await using var after = await scope.OpenAsync(ct);
        await using var afterTx = await AppDb.BeginTransactionAsync(after, ct);
        var denied = await Assert.ThrowsAsync<ApiException>(() =>
            AccessService.LockActorAsync(after, afterTx, actor, ct));
        Assert.Equal(40301, denied.Code);
        await afterTx.RollbackAsync(ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task RefreshReturnsProfileAndKeepsTheOriginalAbsoluteSessionDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        var login = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.210", ct);
        var original = await scope.RefreshStateAsync(login.Refresh, ct);

        Assert.InRange((original.SessionExpiresAt - original.SessionCreatedAt).TotalDays, 29.99, 30.01);
        Assert.True(original.ExpiresAt <= original.SessionExpiresAt);

        await scope.ExecuteAsync("""
            UPDATE refresh_tokens
            SET session_created_at=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 29 DAY),
                session_expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY),
                expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 2 DAY)
            WHERE token_hash=@hash
            """, new { hash = TokenService.HashRefreshToken(login.Refresh) }, ct);
        var shortened = await scope.RefreshStateAsync(login.Refresh, ct);

        var rotated = await scope.Service().RefreshAsync(login.Refresh, "192.0.2.211", ct);
        var current = await scope.RefreshStateAsync(rotated.Refresh, ct);

        Assert.Equal((ulong)1, rotated.Response.User.Id);
        Assert.Equal("target", rotated.Response.User.EmployeeNo);
        Assert.False(rotated.Response.MustChangePassword);
        Assert.NotNull(rotated.Response.Permissions);
        Assert.NotNull(rotated.Response.Menus);
        Assert.Equal(shortened.SessionId, current.SessionId);
        Assert.Equal(shortened.SessionCreatedAt, current.SessionCreatedAt);
        Assert.Equal(shortened.SessionExpiresAt, current.SessionExpiresAt);
        Assert.Equal(shortened.SessionExpiresAt, current.ExpiresAt);

        Assert.Equal(RefreshRevokeReasons.Rotated, await scope.RevokeReasonAsync(login.Refresh, ct));

        // A rotated token is never accepted again. Replaying it immediately revokes the
        // current generation from the same family as well, and is audited as a replay.
        var replay = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Service().RefreshAsync(login.Refresh, "192.0.2.212", ct));
        Assert.Equal(401, replay.Status);
        Assert.Equal(0, await scope.SessionsAsync(1, ct));
        Assert.Equal(RefreshRevokeReasons.Replay, await scope.RevokeReasonAsync(rotated.Refresh, ct));
        Assert.Equal(1, await scope.ReplayAuditsAsync(ct));
        await using var connection = await scope.OpenAsync(ct);
        Assert.False(await scope.Service().HasActiveSessionAsync(
            connection, null, 1, current.SessionId, ct));

        var expiredLogin = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.213", ct);
        var expiredState = await scope.RefreshStateAsync(expiredLogin.Refresh, ct);
        await scope.ExecuteAsync("""
            UPDATE refresh_tokens
            SET session_expires_at=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 SECOND),
                expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY)
            WHERE token_hash=@hash
            """, new { hash = TokenService.HashRefreshToken(expiredLogin.Refresh) }, ct);

        var absoluteExpiry = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Service().RefreshAsync(expiredLogin.Refresh, "192.0.2.214", ct));
        Assert.Equal(401, absoluteExpiry.Status);
        Assert.False(await scope.Service().HasActiveSessionAsync(
            connection, null, 1, expiredState.SessionId, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task StaleTokensRevokedForOtherReasonsAreRejectedWithoutReplayAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        var loggedOut = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.230", ct);
        await scope.Service().LogoutAsync(loggedOut.Refresh, null, "192.0.2.230", ct);
        Assert.Equal(RefreshRevokeReasons.Logout, await scope.RevokeReasonAsync(loggedOut.Refresh, ct));

        var stale = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Service().RefreshAsync(loggedOut.Refresh, "192.0.2.231", ct));
        Assert.Equal(401, stale.Status);
        Assert.Equal(RefreshRevokeReasons.Logout, await scope.RevokeReasonAsync(loggedOut.Refresh, ct));
        Assert.Equal(0, await scope.ReplayAuditsAsync(ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task LoginBeyondTheSessionCapRevokesTheOldestFamilies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        scope.Options.MaxActiveSessionsPerUser = 2;
        var first = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.240", ct);
        var second = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.241", ct);
        // Rotating the oldest family does not make it newer: the cap orders by session creation.
        var firstRotated = await scope.Service().RefreshAsync(first.Refresh, "192.0.2.240", ct);
        var third = await scope.LoginWithRefreshAsync("target", scope.Password, "192.0.2.242", ct);
        var other = await scope.LoginWithRefreshAsync("unaffected", scope.Password, "192.0.2.243", ct);

        Assert.Equal(2, await scope.SessionsAsync(1, ct));
        Assert.Equal(1, await scope.SessionsAsync(2, ct));
        Assert.Equal(RefreshRevokeReasons.SessionCap, await scope.RevokeReasonAsync(firstRotated.Refresh, ct));
        Assert.Equal(RefreshRevokeReasons.Rotated, await scope.RevokeReasonAsync(first.Refresh, ct));
        Assert.Null(await scope.RevokeReasonAsync(second.Refresh, ct));
        Assert.Null(await scope.RevokeReasonAsync(third.Refresh, ct));
        Assert.Null(await scope.RevokeReasonAsync(other.Refresh, ct));

        var evicted = await Assert.ThrowsAsync<ApiException>(() =>
            scope.Service().RefreshAsync(firstRotated.Refresh, "192.0.2.244", ct));
        Assert.Equal(401, evicted.Status);
        Assert.Equal(0, await scope.ReplayAuditsAsync(ct));
        var refreshed = await scope.Service().RefreshAsync(second.Refresh, "192.0.2.245", ct);
        Assert.False(string.IsNullOrEmpty(refreshed.Refresh));
    }

    private sealed class LoginDatabase(MySqlConnection admin, string name, AppOptions options, string password, string legacyPassword) : IAsyncDisposable
    {
        public AppOptions Options => options;

        public async Task<string?> RevokeReasonAsync(string rawToken, CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT revoke_reason FROM refresh_tokens WHERE token_hash=@hash",
                new { hash = TokenService.HashRefreshToken(rawToken) }, cancellationToken: ct));
        }

        public async Task<int> ReplayAuditsAsync(CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN_FAILED' AND target_type='refresh_token'", cancellationToken: ct));
        }

        private readonly AppDb _db = new(options);
        private readonly Lazy<IDbContextFactory<YfDbContext>> _dbContextFactory = new(() => EfTestSupport.DbContextFactory(options));
        public string Password { get; } = password;
        public string LegacyPassword { get; } = legacyPassword;

        public IdentityService Service() => Service(new LoginRateLimiter());

        public IdentityService Service(LoginRateLimiter limiter) =>
            new(_dbContextFactory.Value, options, limiter, new TokenService(options), new PermissionService(), new AuditService([]));

        public Task<MySqlConnection> OpenAsync(CancellationToken ct) => _db.OpenAsync(ct);

        public async Task WaitForBlockedLoginAsync(CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var blocked = await admin.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM information_schema.processlist p
                    WHERE p.DB=@name AND p.COMMAND='Query'
                      AND p.INFO LIKE '%security.management_lock%LOCK IN SHARE MODE%'
                    """, new { name }, cancellationToken: deadline.Token));
                if (blocked == 1) return;
                await Task.Delay(20, deadline.Token);
            }
        }

        public async Task WaitForBlockedManagementAsync(CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var blocked = await admin.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM information_schema.processlist p
                    WHERE p.DB=@name AND p.COMMAND='Query'
                      AND p.INFO LIKE '%security.management_lock%FOR UPDATE%'
                    """, new { name }, cancellationToken: deadline.Token));
                if (blocked >= 1) return;
                await Task.Delay(20, deadline.Token);
            }
        }

        public async Task<LoginResponse> LoginAsync(string employeeNo, string supplied, string ip, CancellationToken ct)
        {
            // Each request gets a fresh in-memory limiter, representing another application instance.
            return (await Service().LoginAsync(new(employeeNo, supplied), ip, ct)).Response;
        }

        public async Task<(LoginResponse Response, string Refresh)> LoginWithRefreshAsync(
            string employeeNo, string supplied, string ip, CancellationToken ct) =>
            WithoutRefreshExpiry(await Service().LoginAsync(new(employeeNo, supplied), ip, ct));

        private static (LoginResponse Response, string Refresh) WithoutRefreshExpiry(
            (LoginResponse Response, string Refresh, DateTime RefreshExpiresAt) result) =>
            (result.Response, result.Refresh);

        public async Task RejectAsync(string employeeNo, string supplied, string ip, CancellationToken ct)
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => LoginAsync(employeeNo, supplied, ip, ct));
            Assert.Equal(401, error.Status);
            Assert.Equal("工号或密码错误", error.Message);
        }

        public async Task<State> StateAsync(CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.QuerySingleAsync<State>(new CommandDefinition("""
                SELECT failed_login_attempts Failures,locked_until LockedUntil,UTC_TIMESTAMP(6) Now FROM users WHERE id=1
                """, cancellationToken: ct));
        }

        public async Task<int> SessionsAsync(ulong userId, CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM refresh_tokens WHERE user_id=@userId AND revoked=0", new { userId }, cancellationToken: ct));
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async Task ExecuteAsync(string sql, object parameters, CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }

        public async Task<RefreshState> RefreshStateAsync(string rawToken, CancellationToken ct)
        {
            await using var conn = await _db.OpenAsync(ct);
            return await conn.QuerySingleAsync<RefreshState>(new CommandDefinition("""
                SELECT session_id SessionId,session_created_at SessionCreatedAt,
                       session_expires_at SessionExpiresAt,expires_at ExpiresAt
                FROM refresh_tokens WHERE token_hash=@hash
                """, new { hash = TokenService.HashRefreshToken(rawToken) }, cancellationToken: ct));
        }

        public static async Task<LoginDatabase> CreateAsync(CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql" ||
                uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Login throttle tests require an explicitly configured local MySQL.");
            var credentials = uri.UserInfo.Split(':', 2);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = uri.Host, Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "",
                DateTimeKind = MySqlDateTimeKind.Utc
            };
            var admin = new MySqlConnection(builder.ConnectionString);
            await admin.OpenAsync(ct);
            var name = "yf_t_" + Guid.NewGuid().ToString("N");
            var created = false;
            try
            {
                await admin.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", cancellationToken: ct));
                created = true;
                builder.Database = name;
                var options = new AppOptions { ConnectionString = builder.ConnectionString, JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") };
                var password = "T#" + Guid.NewGuid().ToString("N")[..12] + "a!";
                const string legacyPassword = "HistoricalPassword#2025!";
                var scope = new LoginDatabase(admin, name, options, password, legacyPassword);
                await using var conn = await scope._db.OpenAsync(ct);
                await using (var context = EfDb.Use(conn))
                    await context.Database.MigrateAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO system_configs(cfg_key,cfg_value) VALUES('security.management_lock','');
                    INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
                    VALUES(1,'target',@hash,'Throttle target','','INTERNAL','ACTIVE',0),
                          (2,'unaffected',@hash,'Other account','','INTERNAL','ACTIVE',0),
                          (3,'legacy',@legacyHash,'Legacy account','','INTERNAL','ACTIVE',0);
                    """, new
                    {
                        hash = await PasswordService.HashAsync(password, ct),
                        legacyHash = await LegacyHashAsync(legacyPassword)
                    }, cancellationToken: ct));
                return scope;
            }
            catch
            {
                if (created) await admin.ExecuteAsync(new CommandDefinition($"DROP DATABASE `{name}`", cancellationToken: CancellationToken.None));
                await admin.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await admin.ExecuteAsync(new CommandDefinition($"DROP DATABASE `{name}`", cancellationToken: CancellationToken.None)); }
            finally { await admin.DisposeAsync(); }
        }

        private static async Task<string> LegacyHashAsync(string password)
        {
            var salt = Encoding.ASCII.GetBytes("legacy-login-salt");
            var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt, MemorySize = 4096, Iterations = 3, DegreeOfParallelism = 1
            };
            var digest = await argon.GetBytesAsync(32);
            return $"$argon2id$v=19$m=4096,t=3,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(digest).TrimEnd('=')}";
        }
    }

    private sealed class State
    {
        public int Failures { get; init; }
        public DateTime? LockedUntil { get; init; }
        public DateTime Now { get; init; }
    }

    private sealed class RefreshState
    {
        public string SessionId { get; init; } = "";
        public DateTime SessionCreatedAt { get; init; }
        public DateTime SessionExpiresAt { get; init; }
        public DateTime ExpiresAt { get; init; }
    }
}
