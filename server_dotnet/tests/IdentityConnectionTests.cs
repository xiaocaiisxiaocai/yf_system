using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class IdentityConnectionTests
{
    [Fact(Timeout = 30_000)]
    public async Task AuthenticationReleasesItsConnectionBeforeCallingDownstream()
    {
        var ct = TestContext.Current.CancellationToken;
        var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
        var uri = new Uri(raw);
        if (uri.Scheme != "mysql" || uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Authentication connection test requires local MySQL");
        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = uri.Host,
            Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
            UserID = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "",
            DateTimeKind = MySqlDateTimeKind.Utc
        };
        await using var admin = new MySqlConnection(builder.ConnectionString);
        await admin.OpenAsync(ct);
        var schema = "yf_test_dotnet_auth_" + Guid.NewGuid().ToString("N");
        await admin.ExecuteAsync(new CommandDefinition($"CREATE DATABASE `{schema}`", cancellationToken: ct));
        try
        {
            builder.Database = schema;
            builder.MaximumPoolSize = 1;
            var options = new AppOptions
            {
                ConnectionString = builder.ConnectionString,
                JwtSecret = "isolated-authentication-connection-test-secret"
            };
            var db = new AppDb(options);
            await using (var seed = await db.OpenAsync(ct))
            {
                await seed.ExecuteAsync(new CommandDefinition("""
                    CREATE TABLE users(
                        id BIGINT UNSIGNED PRIMARY KEY, employee_no VARCHAR(32) NOT NULL,
                        user_type VARCHAR(16) NOT NULL, supplier_id BIGINT UNSIGNED NULL,
                        status VARCHAR(16) NOT NULL, must_change_password BOOLEAN NOT NULL
                    );
                    CREATE TABLE refresh_tokens(
                        user_id BIGINT UNSIGNED NOT NULL, session_id VARCHAR(36) NOT NULL,
                        revoked BOOLEAN NOT NULL, expires_at DATETIME NOT NULL
                    );
                    INSERT INTO users VALUES(1,'pool_test','INTERNAL',NULL,'ACTIVE',0);
                    INSERT INTO refresh_tokens VALUES(1,'pool-session',0,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR));
                    """, cancellationToken: ct));
            }
            var tokens = new TokenService(options);
            var identity = new IdentityService(EfTestSupport.DbContextFactory(options), options, new LoginRateLimiter(), tokens,
                new PermissionService(), new AuditService([]));
            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/projects";
            context.Request.Headers.Authorization = "Bearer " + tokens.IssueAccess(1, "pool_test", "pool-session").Token;
            context.RequestAborted = ct;
            var downstreamCompleted = false;
            var middleware = new IdentityMiddleware(async current =>
            {
                Assert.Equal(1UL, AccessService.GetCurrent(current).Id);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                // With a pool size of one, holding the authentication connection
                // across next() deterministically prevents this business read.
                await using var business = await db.OpenAsync(deadline.Token);
                Assert.Equal(1, await business.ExecuteScalarAsync<int>(
                    new CommandDefinition("SELECT 1", cancellationToken: deadline.Token)));
                downstreamCompleted = true;
            });
            await middleware.InvokeAsync(context, db, tokens, identity);
            Assert.True(downstreamCompleted);
        }
        finally
        {
            await admin.ExecuteAsync(new CommandDefinition($"DROP DATABASE `{schema}`",
                cancellationToken: CancellationToken.None, commandTimeout: 10));
        }
    }
}
