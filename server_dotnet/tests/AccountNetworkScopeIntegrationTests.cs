using System.Net;
using Dapper;
using Yf.Api.Modules.Projects;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class AccountNetworkScopeIntegrationTests
{
    private const string Password = "Network#2026!";
    private const string InsideIp = "10.4.5.6";
    private const string OutsideIp = "203.0.113.7";
    private const string InsideOnly = "INTERNAL_ONLY";
    private const string Any = "ANY";
    private const string DeniedMessage = "该账号仅限内网访问";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string ProfilePath = "/api/v1/auth/profile";
    private const string ConfigPath = "/api/v1/admin/system/configs";

    [Fact(Timeout = 180_000)]
    public async Task ScopeAppliesToLoginCachedIdentityHubAndRefreshWithoutRevokingSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct, args => args.Add("--App:TrustLoopbackProxy=true"));
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("网络范围测试", ct);
        var roleId = (await admin.PostAsync("/api/v1/admin/roles", new { name = "网络范围测试角色" }, ct).Ok()).Id();

        var restrictedAccount = await admin.PostAsync("/api/v1/admin/users", new
        {
            employeeNo = "local_scope", password = Password, realName = "内网账号", email = "", departmentId = org.Section,
            roleId, networkScope = InsideOnly,
        }, ct).Ok();
        var targetId = restrictedAccount.Id();
        var targetPath = $"/api/v1/admin/users/{targetId}";
        Assert.Equal(InsideOnly, restrictedAccount["networkScope"]!.GetValue<string>());

        var anyAccount = await admin.PostAsync("/api/v1/admin/users", new
        {
            employeeNo = "any_scope", password = Password, realName = "全网络账号", email = "", departmentId = org.Section, roleId,
        }, ct).Ok();
        Assert.Equal(Any, anyAccount["networkScope"]!.GetValue<string>());

        var supplierId = await host.CreateSupplierUserAsync("supplier_scope", Password, ct);
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE users SET must_change_password=0 WHERE id IN (@targetId,@anyId);
                UPDATE users SET network_scope='INTERNAL_ONLY' WHERE id=@supplierId;
                """, new { targetId, anyId = anyAccount.Id(), supplierId }, cancellationToken: ct));
        }

        var outside = From(host, OutsideIp);
        var wrongPassword = await outside.PostAsync("/api/v1/auth/login", new { employeeNo = "local_scope", password = "Wrong#2026!" }, ct)
            .Status(HttpStatusCode.Unauthorized);
        Assert.Equal("账号或密码错误", wrongPassword.Json!["message"]!.GetValue<string>());
        await AssertNetworkDeniedAsync(outside.PostAsync("/api/v1/auth/login", new { employeeNo = "local_scope", password = Password }, ct));
        Assert.Null(RefreshCookie(outside));
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM refresh_tokens WHERE user_id=@targetId", new { targetId }, cancellationToken: ct)));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM audit_logs WHERE user_id=@targetId AND action='LOGIN_FAILED'
                  AND ip=@ip AND JSON_UNQUOTE(JSON_EXTRACT(detail,'$.reason'))='NETWORK_SCOPE'
                """, new { targetId, ip = OutsideIp }, cancellationToken: ct)));
        }

        var inside = From(host, InsideIp);
        var token = await LoginAsync(inside, "local_scope", ct);
        await inside.GetAsync(ProfilePath, ct).Ok();
        var originalRefresh = RefreshCookie(inside);
        Assert.NotNull(originalRefresh);

        var unrestricted = From(host, OutsideIp);
        await LoginAsync(unrestricted, "any_scope", ct);
        await unrestricted.GetAsync(ProfilePath, ct).Ok();
        await unrestricted.PutAsync(targetPath, new { networkScope = Any }, ct).Status(HttpStatusCode.Forbidden);

        // Warm the same external request's identity cache before the administrator tightens the account.
        await admin.PutAsync(targetPath, new { networkScope = Any }, ct).Ok();
        outside.Bearer(token);
        await outside.GetAsync(ProfilePath, ct).Ok();
        var updated = await admin.PutAsync(targetPath, new { networkScope = InsideOnly }, ct).Ok();
        Assert.Equal(InsideOnly, updated["networkScope"]!.GetValue<string>());
        await AssertNetworkDeniedAsync(outside.GetAsync(ProfilePath, ct));

        var hub = From(host, OutsideIp);
        await AssertNetworkDeniedAsync(hub.GetAsync($"{ProjectRealtimeHub.Path}?access_token={Uri.EscapeDataString(token)}", ct));

        inside.Http.DefaultRequestHeaders.Remove("X-Forwarded-For");
        inside.Http.DefaultRequestHeaders.Add("X-Forwarded-For", OutsideIp);
        await AssertNetworkDeniedAsync(inside.PostAsync(RefreshPath, null, ct));
        Assert.Equal(originalRefresh, RefreshCookie(inside));
        inside.Http.DefaultRequestHeaders.Remove("X-Forwarded-For");
        inside.Http.DefaultRequestHeaders.Add("X-Forwarded-For", InsideIp);
        var refreshed = await inside.PostAsync(RefreshPath, null, ct).Ok();
        inside.Bearer(refreshed["accessToken"]!.GetValue<string>());
        await inside.GetAsync(ProfilePath, ct).Ok();

        var supplier = From(host, OutsideIp);
        await LoginAsync(supplier, "supplier_scope", ct);
        await supplier.GetAsync(ProfilePath, ct).Ok();
        await supplier.PostAsync(RefreshPath, null, ct).Ok();

        await VerifyConfiguredHostAsync(admin, inside, ct);
        await host.CreateVendorAsync(admin, "网络测试 OEM 厂商", "oem_scope", ct);
        var oem = From(host, OutsideIp);
        var oemLogin = await oem.PostAsync("/api/v1/oem/auth/login", new { employeeNo = "oem_scope", password = "Vendor#2026" }, ct).Ok();
        oem.Bearer(oemLogin["accessToken"]!.GetValue<string>());
        await oem.GetAsync("/api/v1/oem/auth/me", ct).Ok();
        await oem.PostAsync("/api/v1/oem/auth/refresh", null, ct).Ok();
    }

    private static async Task VerifyConfiguredHostAsync(ApiClient admin, ApiClient inside, CancellationToken ct)
    {
        foreach (var (key, value) in new[]
        {
            ("security.internal_networks", "10.0.0.0/33"),
            ("security.external_hosts", "https://outside.example.invalid:8090"),
        })
        {
            await admin.PutAsync(ConfigPath, new { items = new[] { new { key, value } } }, ct).Status(HttpStatusCode.BadRequest);
        }
        await admin.PutAsync(ConfigPath, new { items = new[]
        {
            new { key = "security.internal_networks", value = "10.0.0.0/8\n::1" },
            new { key = "security.external_hosts", value = "outside.example.invalid:8090" },
        } }, ct).Ok();

        inside.Http.DefaultRequestHeaders.Host = "OUTSIDE.EXAMPLE.INVALID:8091";
        await inside.GetAsync(ProfilePath, ct).Ok();
        inside.Http.DefaultRequestHeaders.Host = "OUTSIDE.EXAMPLE.INVALID:8090";
        await AssertNetworkDeniedAsync(inside.GetAsync(ProfilePath, ct));
    }

    private static ApiClient From(OemTestHost host, string ip)
    {
        var client = host.Anonymous();
        client.Http.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }

    private static async Task<string> LoginAsync(ApiClient client, string employeeNo, CancellationToken ct)
    {
        var response = await client.PostAsync("/api/v1/auth/login", new { employeeNo, password = Password }, ct).Ok();
        var token = response["accessToken"]!.GetValue<string>();
        client.Bearer(token);
        return token;
    }

    private static string? RefreshCookie(ApiClient client) =>
        client.Cookies.GetCookies(new Uri(client.Http.BaseAddress!, RefreshPath))["refresh_token"]?.Value;

    private static async Task AssertNetworkDeniedAsync(Task<ApiResponse> request)
    {
        var response = await request.Status(HttpStatusCode.Forbidden, 40305);
        var body = response.Json!;
        Assert.Equal(DeniedMessage, body["message"]!.GetValue<string>());
        Assert.Null(body["accessToken"]);
    }
}
