using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class LoginThrottleTests
{
    [Fact(Timeout = 120_000)]
    public async Task FailuresAcrossIpsAndInstancesPersistAndExpireWithoutExtendingTheLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        for (var attempt = 0; attempt < 10; attempt++)
            await scope.RejectAsync(attempt % 2 == 0 ? "target" : "TARGET", scope.Password + "wrong", "192.0.2." + (attempt + 1), ct);

        var locked = await scope.StateAsync(ct);
        Assert.Equal(10, locked.Failures);
        Assert.NotNull(locked.LockedUntil);
        Assert.InRange((locked.LockedUntil.Value - locked.Now).TotalMinutes, 14, 15.1);
        await scope.RejectAsync("target", scope.Password, "198.51.100.1", ct);
        await scope.RejectAsync("target", scope.Password + "wrong", "198.51.100.2", ct);
        Assert.Equal(locked.LockedUntil, (await scope.StateAsync(ct)).LockedUntil);
        Assert.Equal(0, await scope.SessionsAsync(1, ct));
        await using (var audit = await scope.OpenAsync(ct))
            Assert.Equal(1, await audit.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN_LOCKED' AND user_id=1", cancellationToken: ct)));
        Assert.Equal(2UL, (await scope.LoginAsync("unaffected", scope.Password, "198.51.100.3", ct)).User.Id);

        // Move only this fixture's persisted expiry; no clock or production limit is weakened.
        await scope.ExecuteAsync("UPDATE users SET locked_until=UTC_TIMESTAMP()-INTERVAL 1 SECOND WHERE id=1", ct);
        await scope.RejectAsync("target", scope.Password + "wrong", "203.0.113.1", ct);
        var expired = await scope.StateAsync(ct);
        Assert.Equal(1, expired.Failures);
        Assert.Null(expired.LockedUntil);
        Assert.Equal(1UL, (await scope.LoginAsync("target", scope.Password, "203.0.113.2", ct)).User.Id);
        Assert.Equal(0, (await scope.StateAsync(ct)).Failures);
        Assert.Null((await scope.StateAsync(ct)).LockedUntil);
    }

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentFailuresDoNotLoseCountsAndAuthenticatedPasswordChangeClearsTheLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await LoginDatabase.CreateAsync(ct);
        await scope.LoginAsync("target", scope.Password, "192.0.2.100", ct);
        await Task.WhenAll(Enumerable.Range(1, 14).Select(attempt =>
            scope.RejectAsync("target", scope.Password + "wrong", "198.51.100." + attempt, ct)));
        Assert.Equal(10, (await scope.StateAsync(ct)).Failures);
        await scope.RejectAsync("target", scope.Password, "203.0.113.100", ct);

        var replacement = Guid.NewGuid().ToString("N") + "Ab!";
        await scope.Service(new CaptchaService([])).ChangePasswordAsync(
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
            // its candidate. Self-service password change only needs the user row.
            await scope.WaitForBlockedLoginAsync(ct);
            var replacement = Guid.NewGuid().ToString("N") + "Bb!";
            await scope.Service(new CaptchaService([])).ChangePasswordAsync(
                new CurrentUser(1, "target", "INTERNAL", null), new(scope.Password, replacement), ct);
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

    private sealed class LoginDatabase(MySqlConnection admin, string name, AppOptions options, string password) : IAsyncDisposable
    {
        private readonly AppDb _db = new(options);
        public string Password { get; } = password;

        public IdentityService Service(CaptchaService captchas) =>
            new(_db, options, captchas, new TokenService(options), new PermissionService(), new AuditService([]));

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

        public async Task<LoginResponse> LoginAsync(string employeeNo, string supplied, string ip, CancellationToken ct)
        {
            // Each request gets a fresh in-memory service, representing another application instance.
            var observer = new CaptchaObserver();
            var captchas = new CaptchaService([observer]);
            var challenge = captchas.Issue(ip);
            return (await Service(captchas).LoginAsync(new(employeeNo, supplied, challenge.CaptchaId, observer.Answer), ip, ct)).Response;
        }

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
            var name = "yf_test_login_" + Guid.NewGuid().ToString("N");
            var created = false;
            try
            {
                await admin.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", cancellationToken: ct));
                created = true;
                builder.Database = name;
                var options = new AppOptions { ConnectionString = builder.ConnectionString, JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") };
                var password = Guid.NewGuid().ToString("N") + "Aa!";
                var scope = new LoginDatabase(admin, name, options, password);
                await using var conn = await scope._db.OpenAsync(ct);
                using var resource = typeof(SchemaBootstrap).Assembly.GetManifestResourceStream("Yf.Api.Infrastructure.schema-baseline.json")!;
                using var baseline = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
                await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=0", cancellationToken: ct));
                foreach (var table in baseline.RootElement.GetProperty("tables").EnumerateArray())
                    await conn.ExecuteAsync(new CommandDefinition(table.GetProperty("sql").GetString()!, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition("SET FOREIGN_KEY_CHECKS=1", cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO system_configs(cfg_key,cfg_value) VALUES('security.management_lock','');
                    INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
                    VALUES(1,'target',@hash,'Throttle target','','INTERNAL','ACTIVE',0),
                          (2,'unaffected',@hash,'Other account','','INTERNAL','ACTIVE',0);
                    """, new { hash = await PasswordService.HashAsync(password, ct) }, cancellationToken: ct));
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
    }

    private sealed class CaptchaObserver : ICaptchaChallengeObserver
    {
        public string Answer { get; private set; } = "";
        public void OnIssued(string captchaId, string answer) => Answer = answer;
    }

    private sealed class State
    {
        public int Failures { get; init; }
        public DateTime? LockedUntil { get; init; }
        public DateTime Now { get; init; }
    }
}
