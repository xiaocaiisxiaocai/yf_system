using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>
/// A user update can change the range, the role and the organisation in one request. The delegation ceiling has to
/// hold for the range the account ends up with, not for each part judged against the state before the update.
/// </summary>
public sealed class UserScopeCeilingTests
{
    // 9301 is the manager's division, 9302 another one. The manager role (9101) holds every permission but only an
    // ORG project range; 9102 has no default (SELF), 9103 defaults to ALL, 9104 defaults to ORG.
    private const string Fixture = """
        UPDATE users SET must_change_password=0 WHERE id=1;
        INSERT INTO departments(id,name,parent_id,sort_no,status,kind) VALUES
            (9301,'mgr-div',NULL,1,'ACTIVE','DIVISION'),(9302,'other-div',NULL,2,'ACTIVE','DIVISION');
        INSERT INTO roles(id,name,status,is_built_in) VALUES
            (9101,'mgr','ACTIVE',0),(9102,'plain','ACTIVE',0),(9103,'wide','ACTIVE',0),(9104,'org','ACTIVE',0);
        INSERT INTO role_permissions(role_id,permission_id) SELECT 9101,id FROM permissions;
        INSERT INTO role_permissions(role_id,permission_id)
            SELECT r.id,p.id FROM roles r JOIN permissions p ON p.code IN ('dashboard','project:list')
            WHERE r.id IN (9102,9103,9104);
        INSERT INTO role_data_scopes(role_id,`domain`,`scope`) VALUES
            (9101,'project','ORG'),(9103,'project','ALL'),(9104,'project','ORG');
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password) VALUES
            (9201,'mgr','unused','Mgr','','INTERNAL',9301,'ACTIVE',0),
            (9202,'target','unused','Target','','INTERNAL',9301,'ACTIVE',0);
        INSERT INTO user_roles(user_id,role_id) VALUES (9201,9101),(9202,9102);
        """;

    private static readonly CurrentUser Manager = new(9201, "mgr", "INTERNAL", null);

    [Fact(Timeout = 120_000)]
    public async Task SwitchingToAWideRoleWhileClearingTheOverrideIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync(Fixture + """
            INSERT INTO user_data_scopes(user_id,`domain`,`scope`) VALUES (9202,'project','SELF');
            """, null, ct);
        var users = Users(database);

        // Each part alone passes: the override keeps the new role's ALL away, and clearing it falls back to the old
        // role's SELF. Together they would hand an ORG manager's target ALL.
        var error = await Assert.ThrowsAsync<ApiException>(() => users.UpdateAsync(Manager, 9202,
            new UserUpdate(RoleId: 9103, Scopes: [new DataScopeEntry(DataScopeDomains.Project, null)]), ct));
        Assert.Equal(403, error.Status);
        Assert.Equal(9102UL, await database.ScalarAsync<ulong>("SELECT role_id FROM user_roles WHERE user_id=9202", ct));
        Assert.Equal("SELF", await database.ScalarAsync<string>(
            "SELECT `scope` FROM user_data_scopes WHERE user_id=9202 AND `domain`='project'", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOrgRangeCannotBeCarriedOutsideTheManagersSubtree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync(Fixture, null, ct);
        var users = Users(database);
        var otherDivision = JsonSerializer.SerializeToElement(9302UL);

        // Setting ORG and moving in the same request: the move check must see the ORG range it creates.
        var explicitOrg = await Assert.ThrowsAsync<ApiException>(() => users.UpdateAsync(Manager, 9202,
            new UserUpdate(DepartmentId: otherDivision, Scopes: [new DataScopeEntry(DataScopeDomains.Project, "ORG")]), ct));
        Assert.Equal(403, explicitOrg.Status);

        // The same through a role whose default is ORG.
        var roleOrg = await Assert.ThrowsAsync<ApiException>(() => users.UpdateAsync(Manager, 9202,
            new UserUpdate(DepartmentId: otherDivision, RoleId: 9104), ct));
        Assert.Equal(403, roleOrg.Status);

        Assert.Equal(9301UL, await database.ScalarAsync<ulong>("SELECT department_id FROM users WHERE id=9202", ct));
        Assert.Equal(9102UL, await database.ScalarAsync<ulong>("SELECT role_id FROM user_roles WHERE user_id=9202", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM user_data_scopes WHERE user_id=9202", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ChangesWithinTheManagersOwnRangeStillSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync(Fixture, null, ct);
        var users = Users(database);

        await users.UpdateAsync(Manager, 9202,
            new UserUpdate(RoleId: 9104, Scopes: [new DataScopeEntry(DataScopeDomains.Project, null)]), ct);
        await using var ef = EfTestSupport.DbContextFactory(database.Options).CreateDbContext();
        var resolved = await DataScopeService.ResolveAsync(ef, 9202, DataScopeDomains.Project, ct);
        Assert.Equal(DataScopeValues.Org, resolved.Scope);
        Assert.Contains(9301UL, resolved.OrgIds);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOrgManagerCannotGrantOrgToAnotherDivision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync(Fixture + """
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password)
                VALUES (9203,'outsider','unused','Outsider','','INTERNAL',9302,'ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES (9203,9102);
            """, null, ct);
        var users = Users(database);

        // ORG is compared by the organisations it covers: the same level over another division is still wider.
        var direct = await Assert.ThrowsAsync<ApiException>(() => users.SetDataScopesAsync(Manager, 9203,
            new DataScopeAssign([new DataScopeEntry(DataScopeDomains.Project, "ORG")]), ct));
        Assert.Equal(403, direct.Status);
        var throughRole = await Assert.ThrowsAsync<ApiException>(() => users.AssignRoleAsync(Manager, 9203, [9104UL], ct));
        Assert.Equal(403, throughRole.Status);
        var throughCreate = await Assert.ThrowsAsync<ApiException>(() => users.CreateAsync(Manager,
            new UserCreate("newcomer", "Strong#Pass9", "Newcomer", "", 9302, 9104, null), ct));
        Assert.Equal(403, throughCreate.Status);

        // A role default of ORG would give the outsider ORG over the other division as well.
        var roles = new RoleService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
        var roleDefault = await Assert.ThrowsAsync<ApiException>(() => roles.SetDataScopesAsync(Manager, 9102,
            new DataScopeAssign([new DataScopeEntry(DataScopeDomains.Project, "ORG")]), ct));
        Assert.Equal(403, roleDefault.Status);

        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM user_data_scopes WHERE user_id=9203", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM role_data_scopes WHERE role_id=9102", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='newcomer'", ct));

        // Inside the manager's own division the same grant is fine.
        await users.SetDataScopesAsync(Manager, 9202,
            new DataScopeAssign([new DataScopeEntry(DataScopeDomains.Project, "ORG")]), ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task SystemAdminOemTransferScopeRuleAppliesAfterRoleChangesAndOnCreate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync(Fixture, null, ct);
        var users = Users(database);
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);

        // 1. Creating a system administrator with a non-ALL oem_transfer scope is rejected atomically.
        var createNarrow = await Assert.ThrowsAsync<ApiException>(() => users.CreateAsync(admin,
            new UserCreate("bad_admin", "Strong#Pass9", "Bad Admin", "", 9301, 1, null,
                [new DataScopeEntry(DataScopeDomains.OemTransfer, DataScopeValues.Self)]), ct));
        Assert.Equal(400, createNarrow.Status);
        Assert.Contains("ODM/OEMA", createNarrow.Message);
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='bad_admin'", ct));

        // 2. Promoting an ordinary user to system administrator while narrowing oem_transfer is rejected atomically.
        var promoteNarrow = await Assert.ThrowsAsync<ApiException>(() => users.UpdateAsync(admin, 9202,
            new UserUpdate(RoleId: 1, Scopes: [new DataScopeEntry(DataScopeDomains.OemTransfer, DataScopeValues.Self)]), ct));
        Assert.Equal(400, promoteNarrow.Status);
        Assert.Contains("ODM/OEMA", promoteNarrow.Message);
        Assert.Equal(9102UL, await database.ScalarAsync<ulong>("SELECT role_id FROM user_roles WHERE user_id=9202", ct));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM user_data_scopes WHERE user_id=9202", ct));

        // 3. Demoting a second system administrator while narrowing oem_transfer to SELF in one request succeeds.
        var secondAdmin = await users.CreateAsync(admin,
            new UserCreate("second_admin", "Strong#Pass9", "Second Admin", "", 9301, 1, null,
                [new DataScopeEntry(DataScopeDomains.OemTransfer, DataScopeValues.All)]), ct);
        await users.UpdateAsync(admin, secondAdmin.Id,
            new UserUpdate(RoleId: 9102, Scopes: [new DataScopeEntry(DataScopeDomains.OemTransfer, DataScopeValues.Self)]), ct);
        Assert.Equal(9102UL, await database.ScalarAsync<ulong>(
            $"SELECT role_id FROM user_roles WHERE user_id={secondAdmin.Id}", ct));
        Assert.Equal(DataScopeValues.Self, await database.ScalarAsync<string>(
            $"SELECT `scope` FROM user_data_scopes WHERE user_id={secondAdmin.Id} AND `domain`='oem_transfer'", ct));
    }

    private static UserService Users(MigratedTestDatabase database) =>
        new(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
}
