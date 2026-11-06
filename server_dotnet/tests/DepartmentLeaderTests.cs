using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class DepartmentLeaderTests
{
    [Fact(Timeout = 180_000)]
    public async Task LeaderManagerCanQueryEligibleAccountsAndSetOrClearTheOrganizationLeader()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var org = await host.CreateOrgPathAsync("组织主管", ct);
        var candidateId = await host.CreateInternalUserAsync(
            "leader_candidate", "Leader#2026x", [], org.Section, ct);
        var disabledId = await host.CreateInternalUserAsync(
            "leader_disabled", "Disabled#2026x", [], org.Section, ct);
        var supplierId = await host.CreateSupplierUserAsync("leader_supplier", "Supplier#2026x", ct);
        await using (var connection = await host.OpenAsync(ct))
        {
            await connection.ExecuteAsync(
                "UPDATE users SET status='DISABLED' WHERE id=@disabledId",
                new { disabledId });
            var passwordHash = await connection.ExecuteScalarAsync<string>(
                "SELECT password_hash FROM users WHERE id=@candidateId", new { candidateId });
            var roleId = await connection.ExecuteScalarAsync<ulong>(
                "SELECT role_id FROM user_roles WHERE user_id=@candidateId", new { candidateId });
            for (var index = 0; index < 55; index++)
            {
                var userId = await connection.ExecuteScalarAsync<ulong>(
                    "INSERT INTO users(employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts) " +
                    "VALUES(@employeeNo,@passwordHash,'候选人员',@email,'INTERNAL',@departmentId,'ACTIVE',0,0); SELECT LAST_INSERT_ID();",
                    new
                    {
                        employeeNo = $"leader_pool_{index:00}",
                        passwordHash,
                        email = $"leader_pool_{index:00}@example.invalid",
                        departmentId = org.Department,
                    });
                await connection.ExecuteAsync(
                    "INSERT INTO user_roles(user_id,role_id) VALUES(@userId,@roleId)",
                    new { userId, roleId });
            }
        }

        var managerId = await host.CreateInternalUserAsync(
            "leader_manager", "Manager#2026x", ["dept:leader_manage"], null, ct);
        var manager = await host.LoginInternalAsync("leader_manager", "Manager#2026x", ct);

        var capped = (await manager.GetAsync("/api/v1/admin/department-leader-options", ct).Ok()).AsArray();
        Assert.Equal(50, capped.Count);
        Assert.DoesNotContain(capped, option => option!.Id() == disabledId);
        Assert.DoesNotContain(capped, option => option!.Id() == supplierId);
        Assert.Empty((await manager.GetAsync(
            "/api/v1/admin/department-leader-options?keyword=leader_disabled", ct).Ok()).AsArray());
        Assert.Empty((await manager.GetAsync(
            "/api/v1/admin/department-leader-options?keyword=leader_supplier", ct).Ok()).AsArray());

        var options = (await manager.GetAsync(
            "/api/v1/admin/department-leader-options?keyword=leader_candidate", ct).Ok()).AsArray();
        var option = Assert.Single(options)!;
        Assert.Equal(candidateId, option.Id());
        Assert.Equal("leader_candidate", option["employeeNo"]!.GetValue<string>());
        Assert.Equal("测试leader_candidate", option["realName"]!.GetValue<string>());
        Assert.Equal("组织主管课", option["departmentName"]!.GetValue<string>());
        Assert.Equal(
            ["departmentName", "employeeNo", "id", "realName"],
            option.AsObject().Select(property => property.Key).OrderBy(key => key).ToArray());
        Assert.Single((await manager.GetAsync(
            "/api/v1/admin/department-leader-options?keyword=测试leader_candidate", ct).Ok()).AsArray());

        await manager.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = disabledId }, ct)
            .Status(HttpStatusCode.BadRequest);
        await manager.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = supplierId }, ct)
            .Status(HttpStatusCode.BadRequest);
        // Appointing oneself would route OEM leader approvals to the appointer.
        await manager.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = managerId }, ct)
            .Status(HttpStatusCode.BadRequest);

        await manager.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = candidateId }, ct).Ok();
        var tree = (await manager.GetAsync("/api/v1/departments", ct).Ok()).AsArray();
        var section = tree.Single(node => node!.Id() == org.Division)!["children"]![0]!["children"]![0]!;
        Assert.Equal(candidateId, section["leader"]!.Id());

        await manager.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = (ulong?)null }, ct).Ok();
        tree = (await manager.GetAsync("/api/v1/departments", ct).Ok()).AsArray();
        section = tree.Single(node => node!.Id() == org.Division)!["children"]![0]!["children"]![0]!;
        Assert.Null(section["leader"]);

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync(
                "DELETE rp FROM role_permissions rp " +
                "JOIN user_roles ur ON ur.role_id=rp.role_id " +
                "JOIN permissions p ON p.id=rp.permission_id " +
                "WHERE ur.user_id=@managerId AND p.code='dept:leader_manage'",
                new { managerId });
        await manager.GetAsync("/api/v1/admin/department-leader-options", ct)
            .Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 180_000)]
    public async Task ALeaderLeadsOneOrganisationAndBelongsToItsSubtree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("唯一主管", ct);
        var otherOrg = await host.CreateOrgPathAsync("别处", ct);
        var sectionMember = await host.CreateInternalUserAsync("unique_section", "Leader#2026x", [], org.Section, ct);
        var departmentMember = await host.CreateInternalUserAsync("unique_department", "Leader#2026x", [], org.Department, ct);
        var outsider = await host.CreateInternalUserAsync("unique_outsider", "Leader#2026x", [], otherOrg.Section, ct);

        static string Message(ApiResponse result) => result.Json!["message"]!.GetValue<string>();

        // The leader must belong to the organisation or one of its descendants.
        Assert.Contains("主管必须属于", Message(await admin.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = outsider }, ct).Status(HttpStatusCode.BadRequest)));
        Assert.Contains("主管必须属于", Message(await admin.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = departmentMember }, ct).Status(HttpStatusCode.BadRequest)));
        // A section member may lead the parent department or division.
        await admin.PutAsync($"/api/v1/admin/departments/{org.Department}/leader", new { leaderUserId = sectionMember }, ct).Ok();

        // One person leads at most one organisation; re-saving the same appointment is fine.
        Assert.Contains("一人只能担任一个组织的主管", Message(await admin.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = sectionMember }, ct).Status(HttpStatusCode.BadRequest)));
        Assert.Contains("一人只能担任一个组织的主管", Message(await admin.PutAsync(
            $"/api/v1/admin/departments/{org.Division}/leader", new { leaderUserId = sectionMember }, ct).Status(HttpStatusCode.BadRequest)));
        await admin.PutAsync($"/api/v1/admin/departments/{org.Department}/leader", new { leaderUserId = sectionMember }, ct).Ok();

        // Candidate options for an organisation list only its subtree members who lead nothing else.
        var options = (await admin.GetAsync($"/api/v1/admin/department-leader-options?departmentId={org.Section}", ct).Ok()).AsArray();
        Assert.DoesNotContain(options, option => option!.Id() == sectionMember);
        Assert.DoesNotContain(options, option => option!.Id() == departmentMember);
        Assert.DoesNotContain(options, option => option!.Id() == outsider);
        options = (await admin.GetAsync($"/api/v1/admin/department-leader-options?departmentId={org.Department}", ct).Ok()).AsArray();
        Assert.Contains(options, option => option!.Id() == sectionMember);
        Assert.Contains(options, option => option!.Id() == departmentMember);
        Assert.DoesNotContain(options, option => option!.Id() == outsider);

        // Moving the leader out of the organisation, or moving their section away, is refused while they lead it.
        Assert.Contains("不能担任该组织主管", Message(await admin.PutAsync(
            $"/api/v1/admin/users/{sectionMember}", new { departmentId = otherOrg.Section }, ct).Status(HttpStatusCode.BadRequest)));
        Assert.Contains("不能担任该组织主管", Message(await admin.PutAsync(
            $"/api/v1/admin/departments/{org.Section}", new { name = "唯一主管课", parentId = otherOrg.Department, sortNo = 0 }, ct).Status(HttpStatusCode.BadRequest)));
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(org.Section, await conn.ExecuteScalarAsync<ulong>("SELECT department_id FROM users WHERE id=@sectionMember", new { sectionMember }));
            Assert.Equal(org.Department, await conn.ExecuteScalarAsync<ulong>("SELECT parent_id FROM departments WHERE id=@id", new { id = org.Section }));
        }
        // Moving inside the led subtree stays allowed.
        await admin.PutAsync($"/api/v1/admin/users/{sectionMember}", new { departmentId = org.Department }, ct).Ok();
    }

    [Fact(Timeout = 180_000)]
    public async Task DepartmentLeaderOperationsRequireTheDedicatedPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var org = await host.CreateOrgPathAsync("权限", ct);
        var candidateId = await host.CreateInternalUserAsync(
            "permission_candidate", "Candidate#2026x", [], org.Section, ct);
        await host.CreateInternalUserAsync(
            "department_editor", "Editor#2026x", ["org:dept", "dept:manage"], null, ct);
        var editor = await host.LoginInternalAsync("department_editor", "Editor#2026x", ct);

        await editor.GetAsync("/api/v1/admin/department-leader-options", ct)
            .Status(HttpStatusCode.Forbidden);
        await editor.PutAsync(
            $"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = candidateId }, ct)
            .Status(HttpStatusCode.Forbidden);
    }
}
