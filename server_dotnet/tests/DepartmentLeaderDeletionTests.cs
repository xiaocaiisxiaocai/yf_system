using System.Net;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

/// <summary>An account that still leads an organization node cannot be deleted until every leadership is cleared.</summary>
public sealed class DepartmentLeaderDeletionTests
{
    private const string StillLeaderMessage = "该账号仍是组织主管，请先清除或转交主管后再删除";

    [Fact(Timeout = 180_000)]
    public async Task DeletingADepartmentLeaderIsRefusedUntilEveryLeadershipIsCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("删除主管", ct);
        var leaderId = await host.CreateInternalUserAsync("leader_delete", "Leader#2026x", [], org.Section, ct);
        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = leaderId }, ct).Ok();
        await admin.PutAsync($"/api/v1/admin/departments/{org.Department}/leader", new { leaderUserId = leaderId }, ct).Ok();

        var refused = await admin.DeleteAsync($"/api/v1/admin/users/{leaderId}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Equal(StillLeaderMessage, refused.Json!["message"]!.GetValue<string>());

        // Clearing one of two leaderships is not enough.
        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = (ulong?)null }, ct).Ok();
        refused = await admin.DeleteAsync($"/api/v1/admin/users/{leaderId}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Equal(StillLeaderMessage, refused.Json!["message"]!.GetValue<string>());

        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE id=@leaderId", new { leaderId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='USER_DELETE' AND target_id=@target", new { target = leaderId.ToString() }));
        }

        await admin.PutAsync($"/api/v1/admin/departments/{org.Department}/leader", new { leaderUserId = (ulong?)null }, ct).Ok();
        await admin.DeleteAsync($"/api/v1/admin/users/{leaderId}", ct).Ok();

        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE id=@leaderId", new { leaderId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM user_roles WHERE user_id=@leaderId", new { leaderId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM departments WHERE leader_account_id=@leaderId", new { leaderId }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='USER_DELETE' AND target_id=@target", new { target = leaderId.ToString() }));
        }
    }
}
