using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>
/// Audit guarantees of sign-in, failed password changes and settings changes (commit 8256b89), plus the audit
/// retention bounds as they stand after b1eaad0 (minimum back to one day).
/// </summary>
public sealed class AuditTrailRegressionTests
{
    private const ulong UserId = 9_301;
    private const string EmployeeNo = "audit_user";
    private const string Password = "AuditTrail#2026";

    [Fact(Timeout = 120_000)]
    public async Task SuccessfulLoginWritesItsAuditRowTogetherWithTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var service = await SeedIdentityAsync(database, ct);

        var login = await service.LoginAsync(new(EmployeeNo, Password), "192.0.2.77", ct);

        Assert.Equal(UserId, login.Response.User.Id);
        Assert.Equal(1, await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN' AND user_id={UserId} AND employee_no='{EmployeeNo}' AND ip='192.0.2.77'", ct));
        Assert.Equal(1, await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM refresh_tokens WHERE user_id={UserId} AND revoked=0", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task LoginIsRolledBackWhenItsAuditRowCannotBeWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var service = await SeedIdentityAsync(database, ct);
        // The audit insert of a successful sign-in fails inside the sign-in transaction.
        await database.ExecuteAsync("""
            CREATE TRIGGER trg_block_login_audit BEFORE INSERT ON audit_logs FOR EACH ROW
            BEGIN
                IF NEW.action = 'LOGIN' THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'audit unavailable for test';
                END IF;
            END
            """, null, ct);

        await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync(new(EmployeeNo, Password), "192.0.2.78", ct));

        // No session was issued and no success audit survived: the sign-in is all or nothing.
        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM refresh_tokens WHERE user_id={UserId}", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN'", ct));
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT last_login_at IS NULL FROM users WHERE id={UserId}", ct));

        await database.ExecuteAsync("DROP TRIGGER trg_block_login_audit", null, ct);
        await service.LoginAsync(new(EmployeeNo, Password), "192.0.2.79", ct);
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM refresh_tokens WHERE user_id={UserId}", ct));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='LOGIN'", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task FailedPasswordChangeIsAuditedAndLeavesThePasswordUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var service = await SeedIdentityAsync(database, ct);
        var hashBefore = await database.ScalarAsync<string>($"SELECT password_hash FROM users WHERE id={UserId}", ct);

        var error = await Assert.ThrowsAsync<ApiException>(() => service.ChangePasswordAsync(
            new CurrentUser(UserId, EmployeeNo, "INTERNAL", null), new("Wrong#OldPass1", "BrandNew#2026"), ct));

        Assert.Equal(400, error.Status);
        Assert.Equal("原密码错误", error.Message);
        Assert.Equal(1, await database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM audit_logs WHERE action='PASSWORD_CHANGE_FAILED' AND user_id={UserId} AND employee_no='{EmployeeNo}'", ct));
        Assert.Equal("OLD_PASSWORD_MISMATCH", await database.ScalarAsync<string>(
            "SELECT JSON_UNQUOTE(JSON_EXTRACT(detail,'$.reason')) FROM audit_logs WHERE action='PASSWORD_CHANGE_FAILED'", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='PASSWORD_CHANGE'", ct));
        Assert.Equal(hashBefore, await database.ScalarAsync<string>($"SELECT password_hash FROM users WHERE id={UserId}", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ConfigAndMailSettingChangesRecordTheCallerAddressWhenOneIsGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        database.Options.JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var admin = await AdminAsync(database, ct);
        await SeedAuditRetentionConfigAsync(database, ct);
        var system = new SystemService(database.Database, new AuditService([]), database.Options);
        var smtp = new SmtpSettingsService(database.Database, database.Options, new AuditService([]));

        await system.UpdateConfigsAsync(new ConfigBatch([new ConfigItem("retention.audit_days", "60")]), admin, ct, "198.51.100.23");
        await smtp.SaveAsync(new("smtp.example.invalid", 465, "sender@example.invalid", "notice@example.invalid",
            "SslOnConnect", "fixture-code"), admin, ct, "198.51.100.24");
        await system.UpdateConfigsAsync(new ConfigBatch([new ConfigItem("retention.audit_days", "61")]), admin, ct);

        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='CONFIG_UPDATE' AND ip='198.51.100.23'", ct));
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='CONFIG_UPDATE' AND ip='198.51.100.24'", ct));
        // Callers that pass no address (internal callers) still audit, with no address.
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='CONFIG_UPDATE' AND ip IS NULL", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ConfigUpdateRouteAuditsTheRemoteAddressOfTheRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var admin = await AdminAsync(database, ct);
        await SeedAuditRetentionConfigAsync(database, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.Database);
        // A binding failure then surfaces as an exception that says why, instead of a bare 400.
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<SystemService>();
        builder.Services.AddSingleton<SmtpSettingsService>();
        builder.Services.AddSingleton<SmtpTestService>();
        builder.Services.AddSingleton<MailService>();
        await using var application = builder.Build();
        application.MapSystemModule();
        var endpoint = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/admin/system/configs"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Put) == true);

        await using var requestScope = application.Services.CreateAsyncScope();
        var body = Encoding.UTF8.GetBytes("""{"items":[{"key":"retention.audit_days","value":"45"}]}""");
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Put;
        context.Request.Path = "/api/v1/admin/system/configs";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        // A bare DefaultHttpContext reports that the request cannot have a body, which the JSON binder honours.
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");
        context.Items[typeof(CurrentUser)] = admin;
        context.RequestAborted = ct;

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        var responseText = await new StreamReader(context.Response.Body).ReadToEndAsync(ct);
        Assert.True(context.Response.StatusCode == StatusCodes.Status200OK,
            $"status {context.Response.StatusCode}: {responseText}");
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='CONFIG_UPDATE' AND ip='198.51.100.77'", ct));
    }

    [Fact]
    public void AuditRetentionKeepsItsOneDayMinimum()
    {
        // b1eaad0 restored the minimum of 1 day after 8256b89 had raised it to 30; the boundary is pinned here.
        Assert.Equal("1", SystemService.NormalizeConfig("retention.audit_days", "1"));
        Assert.Equal("3650", SystemService.NormalizeConfig("retention.audit_days", "3650"));
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig("retention.audit_days", "0"));
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig("retention.audit_days", "3651"));
    }

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    // Config rows come from the bootstrap seed, not from migrations, so a migrated-only database has none.
    private static Task SeedAuditRetentionConfigAsync(MigratedTestDatabase database, CancellationToken ct) =>
        database.ExecuteAsync(
            "INSERT IGNORE INTO system_configs(cfg_key,cfg_value,description) VALUES('retention.audit_days','90','audit retention')",
            new { }, ct);

    private static async Task<IdentityService> SeedIdentityAsync(MigratedTestDatabase database, CancellationToken ct)
    {
        database.Options.JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
            VALUES(@Id,@EmployeeNo,@Hash,'Audit user','','INTERNAL','ACTIVE',0)
            """, new { Id = UserId, EmployeeNo, Hash = await PasswordService.HashAsync(Password, ct) }, ct);
        return new IdentityService(EfTestSupport.DbContextFactory(database.Options), database.Options, new LoginRateLimiter(),
            new TokenService(database.Options), new PermissionService(), new AuditService([]));
    }

    private static async Task<CurrentUser> AdminAsync(MigratedTestDatabase database, CancellationToken ct)
    {
        await database.ExecuteAsync("UPDATE users SET must_change_password=0 WHERE employee_no='admin'", null, ct);
        var id = await database.ScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='admin'", ct);
        return new CurrentUser(id, "admin", "INTERNAL", null);
    }
}
