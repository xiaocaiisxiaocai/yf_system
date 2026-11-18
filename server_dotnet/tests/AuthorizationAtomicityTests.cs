using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>
/// A save is one transaction: when any part of an authorization write is rejected, nothing from that request may
/// remain. These cover the two composite writes (role permissions + ranges, user profile + ranges).
/// </summary>
public sealed class AuthorizationAtomicityTests
{
    private const ulong RoleId = 9_101;
    private const ulong UserId = 9_201;
    private const ulong DepartmentId = 9_301;

    [Fact(Timeout = 120_000)]
    public async Task RejectedRoleRangeLeavesThePermissionSetUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO departments(id,name,parent_id,sort_no,status,kind)
                VALUES(9301,'atomic-dept',NULL,1,'ACTIVE','DIVISION');
            INSERT INTO roles(id,name,status,is_built_in) VALUES(9101,'atomic role','ACTIVE',0);
            INSERT INTO role_permissions(role_id,permission_id)
                SELECT 9101,id FROM permissions WHERE code='dashboard';
            """, null, ct);
        var dashboardId = await database.ScalarAsync<ulong>(
            "SELECT id FROM permissions WHERE code='dashboard'", ct);
        var projectListId = await database.ScalarAsync<ulong>(
            "SELECT id FROM permissions WHERE code='project:list'", ct);

        var roles = new RoleService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var error = await Assert.ThrowsAsync<ApiException>(() => roles.SetAuthorizationAsync(
            admin, RoleId,
            new RoleAuthorizationUpdate(
                PermissionIds: [dashboardId, projectListId],
                Scopes: [new DataScopeEntry("not-a-domain", "ALL")]),
            ct));
        Assert.Equal(400, error.Status);
        var codes = (await database.ScalarAsync<string>(
            "SELECT GROUP_CONCAT(p.code ORDER BY p.code) FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id WHERE rp.role_id=9101", ct));
        Assert.Equal("dashboard", codes);
    }

    [Fact(Timeout = 120_000)]
    public async Task RejectedUserRangeLeavesTheProfileUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO departments(id,name,parent_id,sort_no,status,kind)
                VALUES(9301,'atomic-dept',NULL,1,'ACTIVE','DIVISION');
            INSERT INTO roles(id,name,status,is_built_in) VALUES(9101,'atomic role','ACTIVE',0);
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password)
                VALUES(9201,'atomic-user','unused','Before','','INTERNAL',9301,'ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(9201,9101);
            """, null, ct);

        var users = new UserService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var error = await Assert.ThrowsAsync<ApiException>(() => users.UpdateAsync(
            admin, UserId,
            new UserUpdate(RealName: "After", Scopes: [new DataScopeEntry("not-a-domain", "SELF")]),
            ct));
        Assert.Equal(400, error.Status);
        Assert.Equal("Before", await database.ScalarAsync<string>(
            "SELECT real_name FROM users WHERE id=9201", ct));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM user_data_scopes WHERE user_id=9201", ct));
    }
}
