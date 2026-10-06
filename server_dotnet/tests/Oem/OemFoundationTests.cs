using System.Net;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests.Oem;

/// <summary>Realm tokens, business-line isolation, OEM login lifecycle and OEM directory management.</summary>
public sealed class OemFoundationTests
{
    [Fact]
    public void InternalTokensStayUnchangedAndOtherRealmsAreStamped()
    {
        var tokens = new TokenService(new AppOptions { JwtSecret = "unit-test-secret-0123456789-abcdefghijklmnop" });
        var internalToken = tokens.IssueAccess(7, "emp7", "s-1");
        var payload = System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(internalToken.Token.Split('.')[1]));
        Assert.DoesNotContain("rlm", payload);
        Assert.Equal(IdentityRealms.Internal, tokens.ParseAccess(internalToken.Token).Realm);

        var oemToken = tokens.IssueAccess(7, "vendor7", "s-2", "oem");
        var claims = tokens.ParseAccess(oemToken.Token);
        Assert.Equal("oem", claims.Realm);
        Assert.Equal(7UL, claims.UserId);
    }

    [Fact]
    public void RealmScopedLoginBucketsDoNotShareAccountCounters()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < 10; i++) Assert.True(limiter.AllowLogin("10.0.0.1", "same_name"));
        Assert.False(limiter.AllowLogin("10.0.0.1", "same_name"));
        // The OEM realm has its own account bucket even for an identical login name.
        Assert.True(limiter.AllowLogin("oem", "10.0.0.1", "same_name"));
    }

    [Fact(Timeout = 180_000)]
    public async Task OemAccountLifecycleAndBusinessLineIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);

        // Admin creates a vendor and an account.
        var company = await admin.PostAsync("/api/v1/oem/companies", new { name = "精密代工一厂", contactName = "王工", contactEmail = "wang@vendor.invalid" }, ct).Ok();
        var companyId = company.Id();
        await admin.PostAsync("/api/v1/oem/companies", new { name = "精密代工一厂" }, ct).Status(HttpStatusCode.Conflict);
        var account = await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts",
            new { employeeNo = "vendor_a", realName = "张三", email = "zhang@vendor.invalid", password = "Vendor#2026" }, ct).Ok();
        Assert.True(account["mustChangePassword"]!.GetValue<bool>());

        // First login must change the password before anything but auth/me.
        var vendor = await host.LoginOemAsync("vendor_a", "Vendor#2026", ct);
        var me = await vendor.GetAsync("/api/v1/oem/auth/me", ct).Ok();
        Assert.Equal("精密代工一厂", me["account"]!["companyName"]!.GetValue<string>());
        await vendor.GetAsync("/api/v1/oem/company-options", ct).Status(HttpStatusCode.Forbidden, 40303);
        await vendor.PutAsync("/api/v1/oem/auth/password", new { oldPassword = "Vendor#2026", newPassword = "Vendor#2027x" }, ct).Ok();
        // Password change revoked every session.
        await vendor.GetAsync("/api/v1/oem/auth/me", ct).Status(HttpStatusCode.Unauthorized);
        vendor = await host.LoginOemAsync("vendor_a", "Vendor#2027x", ct);

        // An OEM token never reaches the collaboration line (or any non-OEM API).
        await vendor.GetAsync("/api/v1/project-groups", ct).Status(HttpStatusCode.Forbidden, 40304);
        await vendor.GetAsync("/api/v1/auth/profile", ct).Status(HttpStatusCode.Forbidden, 40304);
        await vendor.GetAsync("/api/v1/departments", ct).Status(HttpStatusCode.Forbidden, 40304);
        // ...and holds no internal OEM management permission.
        await vendor.GetAsync("/api/v1/oem/companies", ct).Status(HttpStatusCode.Forbidden);

        // A supplier account cannot see the OEM line at all.
        await host.CreateSupplierUserAsync("supplier_x", "Supplier#2026", ct);
        var supplier = await host.LoginInternalAsync("supplier_x", "Supplier#2026", ct);
        await supplier.GetAsync("/api/v1/oem/company-options", ct).Status(HttpStatusCode.Forbidden, 40304);
        await supplier.GetAsync("/api/v1/oem/companies", ct).Status(HttpStatusCode.Forbidden, 40304);

        // Same login name in two realms: independent accounts.
        await host.CreateInternalUserAsync("vendor_a", "Internal#2026", [], null, ct);
        await host.LoginInternalAsync("vendor_a", "Internal#2026", ct);
        await host.LoginOemAsync("vendor_a", "Vendor#2027x", ct);

        // Refresh rotation and replay revocation.
        var refreshed = await vendor.PostAsync("/api/v1/oem/auth/refresh", null, ct).Ok();
        Assert.False(string.IsNullOrEmpty(refreshed["accessToken"]!.GetValue<string>()));

        // Disabling the vendor revokes its sessions immediately.
        await admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "DISABLED" }, ct).Ok();
        await vendor.GetAsync("/api/v1/oem/auth/me", ct).Status(HttpStatusCode.Unauthorized);
        await host.Anonymous().PostAsync("/api/v1/oem/auth/login", new { employeeNo = "vendor_a", password = "Vendor#2027x" }, ct)
            .Status(HttpStatusCode.Unauthorized);
        // An account cannot be re-enabled under a disabled vendor.
        await admin.PutAsync($"/api/v1/oem/accounts/{account.Id()}/status", new { status = "DISABLED" }, ct).Ok();
        await admin.PutAsync($"/api/v1/oem/accounts/{account.Id()}/status", new { status = "ACTIVE" }, ct).Status(HttpStatusCode.BadRequest);

        // OEM audit rows are recorded with realm identity and are invisible to the collaboration audit view.
        await using var conn = await host.OpenAsync(ct);
        Assert.True(await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='OEM_LOGIN' AND actor_realm='oem' AND user_id IS NULL AND actor_account_id=@id",
            new { id = account.Id() }) >= 2);
        var collaborationLogs = await admin.GetAsync("/api/v1/admin/audit-logs?pageSize=100", ct).Ok();
        foreach (var row in collaborationLogs["list"]!.AsArray())
            Assert.DoesNotContain("OEM_", row!["action"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task OemManagementRequiresExplicitPermissionsAndConfigsStayOutOfTheCollaborationSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);

        // An internal user without oem:* permissions sees nothing of the OEM line.
        await host.CreateInternalUserAsync("plain_staff", "Staff#2026x", ["dashboard", "project:list"], null, ct);
        var staff = await host.LoginInternalAsync("plain_staff", "Staff#2026x", ct);
        await staff.GetAsync("/api/v1/oem/companies", ct).Status(HttpStatusCode.Forbidden);
        await staff.GetAsync("/api/v1/oem/company-options", ct).Status(HttpStatusCode.Forbidden);

        // A dedicated account manager cannot manage companies, and vice versa.
        await host.CreateInternalUserAsync("acct_mgr", "Manager#2026x", ["oem:account_manage"], null, ct);
        var manager = await host.LoginInternalAsync("acct_mgr", "Manager#2026x", ct);
        await manager.PostAsync("/api/v1/oem/companies", new { name = "不应创建" }, ct).Status(HttpStatusCode.Forbidden);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "厂商B" }, ct).Ok()).Id();
        await manager.PostAsync($"/api/v1/oem/companies/{companyId}/accounts",
            new { employeeNo = "vendor_b", realName = "李四", email = "li@vendor.invalid", password = "Vendor#2026" }, ct).Ok();

        // OEM system parameters are neither listed nor editable through the collaboration settings.
        var configs = await admin.GetAsync("/api/v1/admin/system/configs", ct).Ok();
        Assert.DoesNotContain(configs.AsArray(), item => item!["key"]!.GetValue<string>().StartsWith("oem.", StringComparison.Ordinal));
        await admin.PutAsync("/api/v1/admin/system/configs", new { items = new[] { new { key = "oem.upload.max_file_size", value = "1" } } }, ct)
            .Status(HttpStatusCode.BadRequest);
    }

    [Fact(Timeout = 180_000)]
    public async Task DepartmentLeaderMustBeAnActiveInternalAccountAndIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("质量", ct);
        var leaderId = await host.CreateInternalUserAsync("sec_leader", "Leader#2026x", [], org.Section, ct);
        var supplierId = await host.CreateSupplierUserAsync("sup_leader", "Supplier#2026", ct);

        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = supplierId }, ct).Status(HttpStatusCode.BadRequest);
        var result = await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = leaderId }, ct).Ok();
        Assert.Equal(leaderId, result["leader"]!.Id());

        var tree = await admin.GetAsync("/api/v1/departments", ct).Ok();
        var section = tree.AsArray().Single(d => d!.Id() == org.Division)!["children"]![0]!["children"]![0]!;
        Assert.Equal("sec_leader", section["leader"]!["employeeNo"]!.GetValue<string>());

        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = (ulong?)null }, ct).Ok();
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='DEPT_LEADER_CHANGE'"));
        Assert.Null(await conn.ExecuteScalarAsync<ulong?>("SELECT leader_account_id FROM departments WHERE id=@id", new { id = org.Section }));

        // Without dept:leader_manage the endpoint is forbidden.
        await host.CreateInternalUserAsync("dept_mgr", "DeptMgr#2026x", ["org:dept", "dept:manage"], null, ct);
        var deptManager = await host.LoginInternalAsync("dept_mgr", "DeptMgr#2026x", ct);
        await deptManager.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = leaderId }, ct).Status(HttpStatusCode.Forbidden);
    }
}
