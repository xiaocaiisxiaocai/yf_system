using System.Net;
using System.Net.Http.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Identity;

namespace Yf.Api.Tests.Oem;

public sealed class OemIdentityHardeningTests
{
    private const string RefreshCookie = "oem_refresh_token";

    [Fact(Timeout = 180_000)]
    public async Task WrongPasswordsDoNotPersistAndLegacyLockDoesNotBlockOemLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        await host.CreateVendorAsync(admin, "不锁定厂商", "no_lock_vendor", ct);
        await using var connection = await host.OpenAsync(ct);
        var accountId = await connection.ExecuteScalarAsync<ulong>(
            "SELECT id FROM oem_accounts WHERE employee_no='no_lock_vendor'");
        await connection.ExecuteAsync("""
            UPDATE oem_accounts
            SET failed_login_attempts=2147483647,locked_until=UTC_TIMESTAMP(3)+INTERVAL 1 DAY
            WHERE id=@accountId
            """, new { accountId });

        var service = host.Service<OemAuthService>();
        var legacy = await service.LoginAsync(new("no_lock_vendor", "Vendor#2026"), "192.0.2.1", ct);
        Assert.Equal(accountId, legacy.Response.Account.Id);
        Assert.Equal((0, (DateTime?)null), await AccountLockStateAsync());

        for (var attempt = 2; attempt <= 11; attempt++)
        {
            var rejected = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync(
                new("no_lock_vendor", "Vendor#2026-wrong"), "192.0.2." + attempt, ct));
            Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)rejected.Status);
            Assert.Equal("账号或密码错误", rejected.Message);
        }

        Assert.Equal((0, (DateTime?)null), await AccountLockStateAsync());
        Assert.Equal(10, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM audit_logs
            WHERE action='OEM_LOGIN_FAILED' AND actor_realm='oem' AND actor_account_id=@accountId
            """, new { accountId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM audit_logs
            WHERE action='OEM_LOGIN_LOCKED' AND actor_realm='oem' AND actor_account_id=@accountId
            """, new { accountId }));
        Assert.Equal(accountId, (await service.LoginAsync(
            new("no_lock_vendor", "Vendor#2026"), "198.51.100.1", ct)).Response.Account.Id);

        async Task<(int Failures, DateTime? LockedUntil)> AccountLockStateAsync()
        {
            var row = await connection.QuerySingleAsync<OemLockState>(
                "SELECT failed_login_attempts Failures,locked_until LockedUntil FROM oem_accounts WHERE id=@accountId",
                new { accountId });
            return (row.Failures, row.LockedUntil);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task OemAuthRejectsCrossSiteFetchWithoutOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var client = host.Anonymous();
        client.Http.DefaultRequestHeaders.Remove("Origin");
        client.Http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.Http.PostAsJsonAsync("/api/v1/oem/auth/login",
            new { employeeNo = "unknown", password = "Unknown#2026" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Timeout = 240_000)]
    public async Task OnlyReuseOfARotatedRefreshTokenIsAuditedAsReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendor = await host.CreateVendorAsync(admin, "刷新重放厂商", "replay_vendor", ct);
        await using var connection = await host.OpenAsync(ct);
        var accountId = await connection.ExecuteScalarAsync<ulong>(
            "SELECT id FROM oem_accounts WHERE employee_no='replay_vendor'");
        Task<int> ReplayAuditsAsync() => connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM audit_logs
            WHERE action='OEM_LOGIN_FAILED' AND actor_realm='oem' AND actor_account_id=@accountId
              AND JSON_UNQUOTE(JSON_EXTRACT(detail,'$.reason'))='refresh token reuse detected'
            """, new { accountId });
        Task<string?> ReasonAsync(string token) => connection.ExecuteScalarAsync<string?>(
            "SELECT revoke_reason FROM oem_refresh_tokens WHERE token_hash=@hash", new { hash = Hash(token) });

        // Logout: the stale cookie of a logged-out tab is rejected but is not a replay.
        var loggedOut = RefreshOf(vendor);
        await vendor.PostAsync("/api/v1/oem/auth/logout", null, ct).Ok();
        Assert.Equal("LOGOUT", await ReasonAsync(loggedOut));
        await WithRefresh(host, loggedOut).PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        Assert.Equal(0, await ReplayAuditsAsync());

        // Legacy revoked rows without a reason are treated as non-replay.
        var legacyClient = await host.LoginOemAsync("replay_vendor", "Vendor#2026", ct);
        var legacy = RefreshOf(legacyClient);
        await connection.ExecuteAsync("UPDATE oem_refresh_tokens SET revoked=1, revoke_reason=NULL WHERE token_hash=@hash",
            new { hash = Hash(legacy) });
        await WithRefresh(host, legacy).PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        Assert.Equal(0, await ReplayAuditsAsync());

        // Rotation: reusing the superseded token is a replay that kills the family once.
        var rotatingClient = await host.LoginOemAsync("replay_vendor", "Vendor#2026", ct);
        var superseded = RefreshOf(rotatingClient);
        await rotatingClient.PostAsync("/api/v1/oem/auth/refresh", null, ct).Ok();
        Assert.Equal("ROTATED", await ReasonAsync(superseded));
        var successor = RefreshOf(rotatingClient);
        await WithRefresh(host, superseded).PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        Assert.Equal(1, await ReplayAuditsAsync());
        Assert.Equal("REPLAY", await ReasonAsync(successor));
        await rotatingClient.PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        Assert.Equal(1, await ReplayAuditsAsync());

        // Vendor disable revokes every session of the vendor in one statement, marked as administrative.
        var companyId = await host.CompanyIdOfAsync("replay_vendor", ct);
        var disabledClient = await host.LoginOemAsync("replay_vendor", "Vendor#2026", ct);
        var disabled = RefreshOf(disabledClient);
        await admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "DISABLED" }, ct).Ok();
        Assert.Equal("ADMIN", await ReasonAsync(disabled));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_refresh_tokens WHERE account_id=@accountId AND revoked=0", new { accountId }));
        await WithRefresh(host, disabled).PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        Assert.Equal(1, await ReplayAuditsAsync());
    }

    [Fact(Timeout = 180_000)]
    public async Task CompanyKeywordSearchTreatsLikeWildcardsLiterally()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        foreach (var name in new[] { "折扣50%厂商", "折扣500厂商", "甲_乙厂商", "甲x乙厂商", @"反\斜厂商" })
            await admin.PostAsync("/api/v1/oem/companies", new { name }, ct).Ok();

        async Task<string[]> SearchAsync(string keyword)
        {
            var page = await admin.GetAsync("/api/v1/oem/companies?keyword=" + Uri.EscapeDataString(keyword), ct).Ok();
            return page["list"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
        }

        Assert.Equal(["折扣50%厂商"], await SearchAsync("50%"));
        Assert.Equal(["甲_乙厂商"], await SearchAsync("甲_乙"));
        Assert.Equal([@"反\斜厂商"], await SearchAsync(@"反\斜"));
        Assert.Equal(["折扣50%厂商"], await SearchAsync("%"));
        Assert.Equal(["甲_乙厂商"], await SearchAsync("_"));
    }

    [Fact(Timeout = 180_000)]
    public async Task DepartmentBoundToAnApprovalTemplateCannotBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var (_, _, section) = await host.CreateOrgPathAsync("模板绑定", ct);
        await using var connection = await host.OpenAsync(ct);
        var templateId = await connection.ExecuteScalarAsync<ulong>("SELECT id FROM oem_flow_templates ORDER BY id LIMIT 1");
        await connection.ExecuteAsync("INSERT INTO oem_flow_template_scopes(template_id,department_id) VALUES(@templateId,@section)",
            new { templateId, section });

        var rejected = await admin.DeleteAsync($"/api/v1/admin/departments/{section}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Contains("审批模板", rejected.Json!["message"]!.GetValue<string>());
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM departments WHERE id=@section", new { section }));

        await connection.ExecuteAsync("DELETE FROM oem_flow_template_scopes WHERE department_id=@section", new { section });
        await admin.DeleteAsync($"/api/v1/admin/departments/{section}", ct).Ok();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM departments WHERE id=@section", new { section }));

        // The leader lookup is a read and no longer needs the exclusive management gate.
        await admin.GetAsync("/api/v1/admin/department-leader-options?keyword=admin", ct).Ok();
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalUserWithOnlyAnOemUploadSessionCannotBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var userId = await host.CreateInternalUserAsync("oem_upload_history", "UploadUser#2026", ["dashboard"], null, ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "上传历史厂商" }, ct).Ok()).Id();
        await using var connection = await host.OpenAsync(ct);
        var retentionId = await connection.ExecuteScalarAsync<ulong>("SELECT id FROM oem_retention_templates ORDER BY id LIMIT 1");
        var transferId = await connection.ExecuteScalarAsync<ulong>("""
            INSERT INTO oem_transfers(
                direction,oem_company_id,title,lifecycle_status,retention_template_id,created_at,updated_at,concurrency_version)
            VALUES('INTERNAL_TO_OEM',@companyId,'上传历史','DRAFT',@retentionId,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),0);
            SELECT LAST_INSERT_ID();
            """, new { companyId, retentionId });
        await connection.ExecuteAsync("""
            INSERT INTO oem_upload_sessions(
                id,transfer_id,uploader_realm,uploader_id,file_name,file_size,chunk_size,total_chunks,temp_dir,
                reserved_bytes,status,expires_at,created_at,updated_at)
            VALUES('internal-upload-history',@transferId,'internal',@userId,'upload.pdf',1,1,1,'history/temp',1,'UPLOADING',
                UTC_TIMESTAMP(3) + INTERVAL 1 DAY,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, new { transferId, userId });

        await admin.DeleteAsync($"/api/v1/admin/users/{userId}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE id=@userId", new { userId }));
    }

    private static string RefreshOf(ApiClient client) =>
        client.Cookies.GetCookies(new Uri(client.Http.BaseAddress!, "/api/v1/oem/auth/refresh"))[RefreshCookie]?.Value
        ?? throw new InvalidOperationException("No OEM refresh cookie");

    private static ApiClient WithRefresh(OemTestHost host, string token)
    {
        var client = host.Anonymous();
        client.Cookies.Add(new Cookie(RefreshCookie, token, "/api/v1/oem/auth", client.Http.BaseAddress!.Host));
        return client;
    }

    private static string Hash(string token) => Yf.Api.Modules.Identity.TokenService.HashRefreshToken(token);

    private sealed class OemLockState
    {
        public int Failures { get; init; }
        public DateTime? LockedUntil { get; init; }
    }
}
