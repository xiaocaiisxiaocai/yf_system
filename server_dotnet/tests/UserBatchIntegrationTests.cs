using System.Net;
using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class UserBatchIntegrationTests
{
    private const string Path = "/api/v1/admin/users/batch";
    private const string Password = "Batch#2026!Lamp";

    [Fact(Timeout = 180_000)]
    public async Task CombinedChangesCommitAndAuditOncePerAccountWithIndependentFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var from = await host.CreateOrgPathAsync("原组织", ct);
        var to = await host.CreateOrgPathAsync("目标组织", ct);
        var oldRoleId = await CreateRoleAsync(admin, "原角色", ct);
        var roleId = await CreateRoleAsync(admin, "目标角色", ct);
        var first = await CreateUserAsync(admin, "batch_first", from.Section, oldRoleId, ct);
        var leader = await CreateUserAsync(admin, "batch_leader", from.Section, oldRoleId, ct);
        var second = await CreateUserAsync(admin, "batch_second", from.Section, oldRoleId, ct);
        var supplier = await host.CreateSupplierUserAsync("batch_supplier", Password, ct);
        await using var conn = await host.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE departments SET leader_account_id=@leader WHERE id=@department",
            new { leader, department = from.Department }, cancellationToken: ct));

        var result = await UpdateAsync(admin,
            new([first, supplier, leader, 1, second, first], roleId, to.Section, "INTERNAL_ONLY", "DISABLED"), ct);

        Assert.Equal([first, second], result.Succeeded);
        Assert.Collection(result.Failed,
            failure => Assert.Equal(new UserBatchFailure(supplier, "batch_supplier", "仅支持内部账号"), failure),
            failure =>
            {
                Assert.Equal(leader, failure.Id);
                Assert.Equal("batch_leader", failure.EmployeeNo);
                Assert.Contains("请先更换该组织主管", failure.Message, StringComparison.Ordinal);
            },
            failure => Assert.Equal(new UserBatchFailure(1, "admin", "不能停用自己的账号"), failure));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM users u JOIN user_roles r ON r.user_id=u.id
            WHERE u.id IN (@first,@second) AND u.status='DISABLED' AND u.network_scope='INTERNAL_ONLY'
              AND u.department_id=@department AND r.role_id=@roleId
            """, new { first, second, department = to.Section, roleId }, cancellationToken: ct)));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM user_roles WHERE user_id IN (@first,@second)",
            new { first, second }, cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM users u JOIN user_roles r ON r.user_id=u.id
            WHERE u.id=@leader AND u.status='ACTIVE' AND u.network_scope='ANY'
              AND u.department_id=@department AND r.role_id=@oldRoleId
            """, new { leader, department = from.Section, oldRoleId }, cancellationToken: ct)));

        var audits = (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT detail FROM audit_logs WHERE target_type='user' AND target_id IN (1,@first,@second,@leader,@supplier)
              AND action IN ('USER_UPDATE','USER_STATUS') ORDER BY target_id
            """, new { first, second, leader, supplier }, cancellationToken: ct))).ToArray();
        Assert.Equal(2, audits.Length);
        foreach (var (audit, employeeNo) in audits.Zip(new[] { "batch_first", "batch_second" }))
        {
            using var detail = JsonDocument.Parse(audit);
            Assert.Equal(employeeNo, detail.RootElement.GetProperty("employeeNo").GetString());
            var fields = detail.RootElement.GetProperty("changedFields").EnumerateArray().Select(field => field.GetString());
            Assert.Equal(["networkScope", "departmentId", "roleId", "status"], fields);
        }

        var alreadyDisabled = await UpdateAsync(admin, new([first], Status: "DISABLED"), ct);
        Assert.Empty(alreadyDisabled.Succeeded);
        Assert.Equal(new UserBatchFailure(first, "batch_first", "账号已停用"), Assert.Single(alreadyDisabled.Failed));
        var enabled = await UpdateAsync(admin, new([first], Status: "ACTIVE"), ct);
        Assert.Equal(first, Assert.Single(enabled.Succeeded));
        Assert.Empty(enabled.Failed);
        Assert.Equal("ACTIVE", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM users WHERE id=@first", new { first }, cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM audit_logs WHERE target_type='user' AND target_id=@first AND action='USER_STATUS'
            """, new { first }, cancellationToken: ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task DelegationCeilingAndLastAdministratorProtectionKeepOtherItemsIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("授权测试", ct);
        await host.CreateInternalUserAsync("batch_manager", Password, ["user:manage"], org.Section, ct);
        var target = await host.CreateInternalUserAsync("batch_target", Password, [], org.Section, ct);
        var privileged = await host.CreateInternalUserAsync("batch_privileged", Password, ["role:manage"], org.Section, ct);
        var manager = await host.LoginInternalAsync("batch_manager", Password, ct);
        await using var conn = await host.OpenAsync(ct);
        var targetRole = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT role_id FROM user_roles WHERE user_id=@target", new { target }, cancellationToken: ct));
        var privilegedRole = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT role_id FROM user_roles WHERE user_id=@privileged", new { privileged }, cancellationToken: ct));

        var deniedRole = await UpdateAsync(manager, new([target], privilegedRole, NetworkScope: "INTERNAL_ONLY"), ct);
        Assert.Empty(deniedRole.Succeeded);
        Assert.Equal(new UserBatchFailure(target, "batch_target", "无操作权限"), Assert.Single(deniedRole.Failed));
        Assert.Equal(targetRole, await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT role_id FROM user_roles WHERE user_id=@target", new { target }, cancellationToken: ct)));
        Assert.Equal("ANY", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT network_scope FROM users WHERE id=@target", new { target }, cancellationToken: ct)));

        // A non-admin reaches the target grant check before the last-admin check; a sole admin also cannot disable itself.
        var result = await UpdateAsync(manager, new([1, target], Status: "DISABLED"), ct);
        Assert.Equal(target, Assert.Single(result.Succeeded));
        Assert.Equal(new UserBatchFailure(1, "admin", "无操作权限"), Assert.Single(result.Failed));
        Assert.Equal("ACTIVE", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM users WHERE id=1", cancellationToken: ct)));
        Assert.Equal("DISABLED", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM users WHERE id=@target", new { target }, cancellationToken: ct)));

        // The grant ceiling also checks the effective data range derived from the incoming role.
        var wideRole = await CreateRoleAsync(admin, "全部数据范围", ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO role_data_scopes(role_id,`domain`,`scope`) VALUES(@wideRole,'project','ALL')",
            new { wideRole }, cancellationToken: ct));
        var deniedRange = await UpdateAsync(manager, new([target], wideRole), ct);
        Assert.Empty(deniedRange.Succeeded);
        Assert.Contains("范围", Assert.Single(deniedRange.Failed).Message, StringComparison.Ordinal);
        Assert.Equal(targetRole, await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT role_id FROM user_roles WHERE user_id=@target", new { target }, cancellationToken: ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task RequestValidationAndPermissionAreCheckedBeforeWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("批量上限测试", ct);
        var viewer = await host.CreateInternalUserAsync("batch_viewer", Password, ["user:view"], org.Section, ct);
        var client = await host.LoginInternalAsync("batch_viewer", Password, ct);

        await admin.PutAsync(Path, new UserBatchUpdate([], NetworkScope: "ANY"), ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(Path, new UserBatchUpdate([viewer]), ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(Path, new UserBatchUpdate([viewer], NetworkScope: "EXTERNAL_ONLY"), ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(Path, new UserBatchUpdate([viewer], Status: "PENDING"), ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(Path, new UserBatchUpdate(Enumerable.Repeat(viewer, 101).ToArray(), NetworkScope: "INTERNAL_ONLY"), ct)
            .Status(HttpStatusCode.BadRequest);
        await client.PutAsync(Path, new UserBatchUpdate([viewer], NetworkScope: "INTERNAL_ONLY", Status: "DISABLED"), ct)
            .Status(HttpStatusCode.Forbidden);
        await client.PutAsync(Path, new UserBatchUpdate([viewer], Status: "DISABLED"), ct).Status(HttpStatusCode.Forbidden);
        await client.PutAsync(Path, new UserBatchUpdate([viewer], NetworkScope: "INTERNAL_ONLY"), ct).Status(HttpStatusCode.Forbidden);

        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE id=@viewer AND status='ACTIVE' AND network_scope='ANY'",
            new { viewer }, cancellationToken: ct)));
        var accepted = await UpdateAsync(admin, new(Enumerable.Repeat(viewer, 100).ToArray(), NetworkScope: "INTERNAL_ONLY"), ct);
        Assert.Equal(viewer, Assert.Single(accepted.Succeeded));
        Assert.Empty(accepted.Failed);
    }

    private static async Task<UserBatchResponse> UpdateAsync(ApiClient client, UserBatchUpdate request, CancellationToken ct) =>
        (await client.PutAsync(Path, request, ct).Ok()).Deserialize<UserBatchResponse>(JsonDefaults.Web)!;

    private static async Task<ulong> CreateRoleAsync(ApiClient admin, string name, CancellationToken ct) =>
        (await admin.PostAsync("/api/v1/admin/roles", new { name }, ct).Ok()).Id();

    private static async Task<ulong> CreateUserAsync(ApiClient admin, string employeeNo, ulong departmentId, ulong roleId, CancellationToken ct) =>
        (await admin.PostAsync("/api/v1/admin/users", new
        {
            employeeNo, password = Password, realName = employeeNo, email = "", departmentId, roleId,
        }, ct).Ok()).Id();
}
