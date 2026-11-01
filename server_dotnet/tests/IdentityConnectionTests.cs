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
        var builder = LocalMySqlTestServer.AdministrationOrSkip("Authentication connection test");
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
                await seed.ExecuteAsync(new CommandDefinition(HandwrittenTestSchemas.AuthenticationConnection + Environment.NewLine + """
                    INSERT INTO system_configs VALUES('security.identity_revision','0');
                    INSERT INTO users VALUES(1,'pool_test','INTERNAL',NULL,'ACTIVE',0);
                    INSERT INTO refresh_tokens
                        (user_id,session_id,revoked,expires_at,session_created_at,session_expires_at)
                    VALUES(1,'pool-session',0,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR),
                           UTC_TIMESTAMP(),DATE_ADD(UTC_TIMESTAMP(),INTERVAL 30 DAY));
                    """, cancellationToken: ct));
            }
            var tokens = new TokenService(options);
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
            }, []);
            await middleware.InvokeAsync(context, db, tokens, new IdentityProjectionCache());
            Assert.True(downstreamCompleted);
        }
        finally
        {
            await admin.ExecuteAsync(new CommandDefinition($"DROP DATABASE `{schema}`",
                cancellationToken: CancellationToken.None, commandTimeout: 10));
        }
    }
}
