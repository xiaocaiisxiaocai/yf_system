using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>
/// The data range decides whose records an internal account may see, so these pin the resolution order
/// (explicit override, then the active role default, then the organisation-leader fallback), the disabled
/// organisation rule, several role rows, and that a single account and a whole audience share one implementation.
/// </summary>
public sealed class DataScopeResolutionTests
{
    private const ulong AllUserId = 9_201;
    private const ulong SelfOverrideId = 9_202;
    private const ulong SelfRoleWithLeadershipId = 9_203;
    private const ulong LeaderId = 9_204;
    private const ulong OrgRoleId = 9_205;
    private const ulong MultiRoleId = 9_206;
    private const ulong DisabledLeaderId = 9_207;
    private const ulong MemberOnlyId = 9_208;
    private const ulong OrgOverrideId = 9_209;

    [Fact(Timeout = 120_000)]
    public async Task ResolveFollowsOverrideThenRoleDefaultThenLeadershipAndSkipsDisabledOrganisations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await using var db = EfDb.Use(connection);

        Assert.Equal(DataScopeValues.All, await ScopeAsync(db, AllUserId, ct));
        // An explicit override wins over the role default in both directions.
        Assert.Equal(DataScopeValues.Self, await ScopeAsync(db, SelfOverrideId, ct));
        Assert.Equal(DataScopeValues.Org, await ScopeAsync(db, OrgOverrideId, ct));
        // A role default outranks the organisation-leader fallback.
        Assert.Equal(DataScopeValues.Self, await ScopeAsync(db, SelfRoleWithLeadershipId, ct));
        // Without a setting, leading an active organisation means ORG over the own organisation plus the led subtree.
        var leader = await ResolveAsync(db, LeaderId, ct);
        Assert.Equal(DataScopeValues.Org, leader.Scope);
        Assert.Equal(new ulong[] { 9_102, 9_103 }, leader.OrgIds.Order().ToArray());
        // An ORG role default covers the caller's own organisation; it does not follow the leadership tree.
        var orgRole = await ResolveAsync(db, OrgRoleId, ct);
        Assert.Equal(DataScopeValues.Org, orgRole.Scope);
        Assert.Equal(new ulong[] { 9_103 }, orgRole.OrgIds.Order().ToArray());
        // The schema allows one role row per account (`uk_user_roles_user_id`), so a role default is unambiguous.
        Assert.Equal(DataScopeValues.All, await ScopeAsync(db, MultiRoleId, ct));
        // A disabled organisation grants no authority, so its leadership does not imply ORG.
        Assert.Equal(DataScopeValues.Self, await ScopeAsync(db, DisabledLeaderId, ct));
        // A plain member with no setting sees only its own records.
        Assert.Equal(DataScopeValues.Self, await ScopeAsync(db, MemberOnlyId, ct));
        // Domains are independent: the same account has no ODM/OEMA default.
        Assert.Equal(DataScopeValues.Self,
            (await DataScopeService.ResolveAsync(db, AllUserId, DataScopeDomains.OemTransfer, ct)).Scope);
    }

    [Fact(Timeout = 120_000)]
    public async Task ResolveManyReturnsTheSameRangesAsTheSingleAccountQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await using var db = EfDb.Use(connection);

        ulong[] ids = [AllUserId, SelfOverrideId, SelfRoleWithLeadershipId, LeaderId,
            OrgRoleId, MultiRoleId, DisabledLeaderId, MemberOnlyId, OrgOverrideId];
        var many = await DataScopeService.ResolveManyAsync(db, ids, DataScopeDomains.Project, ct);
        Assert.Equal(ids.Length, many.Count);
        foreach (var id in ids)
        {
            var single = await DataScopeService.ResolveAsync(db, id, DataScopeDomains.Project, ct);
            Assert.Equal(single.Scope, many[id].Scope);
            Assert.Equal(single.OrgIds.Order().ToArray(), many[id].OrgIds.Order().ToArray());
        }
    }

    private static async Task<string> ScopeAsync(YfDbContext db, ulong userId, CancellationToken ct) =>
        (await ResolveAsync(db, userId, ct)).Scope;

    private static Task<DataScopeResolution> ResolveAsync(YfDbContext db, ulong userId, CancellationToken ct) =>
        DataScopeService.ResolveAsync(db, userId, DataScopeDomains.Project, ct);

    private static Task SeedAsync(MigratedTestDatabase database, CancellationToken ct) => database.ExecuteAsync("""
        INSERT INTO departments(id,name,parent_id,sort_no,status,kind) VALUES
            (9101,'division',NULL,1,'ACTIVE','DIVISION'),
            (9102,'department',9101,2,'ACTIVE','DEPARTMENT'),
            (9103,'section',9102,3,'ACTIVE','SECTION'),
            (9104,'disabled division',NULL,4,'DISABLED','DIVISION'),
            (9105,'led-by-9203',NULL,5,'ACTIVE','DIVISION');
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password) VALUES
            (9201,'scope-all','unused','Scope All','','INTERNAL',9103,'ACTIVE',0),
            (9202,'scope-self-override','unused','Scope Self Override','','INTERNAL',9103,'ACTIVE',0),
            (9203,'scope-self-role-leader','unused','Scope Self Role Leader','','INTERNAL',9102,'ACTIVE',0),
            (9204,'scope-leader','unused','Scope Leader','','INTERNAL',9103,'ACTIVE',0),
            (9205,'scope-org-role','unused','Scope Org Role','','INTERNAL',9103,'ACTIVE',0),
            (9206,'scope-multi-role','unused','Scope Multi Role','','INTERNAL',9101,'ACTIVE',0),
            (9207,'scope-disabled-leader','unused','Scope Disabled Leader','','INTERNAL',9104,'ACTIVE',0),
            (9208,'scope-member','unused','Scope Member','','INTERNAL',9101,'ACTIVE',0),
            (9209,'scope-org-override','unused','Scope Org Override','','INTERNAL',9103,'ACTIVE',0);
        -- Leadership is written after the accounts exist: the column is plain data, not an insert-order dependency.
        UPDATE departments SET leader_account_id=9204 WHERE id=9102;
        UPDATE departments SET leader_account_id=9203 WHERE id=9105;
        UPDATE departments SET leader_account_id=9207 WHERE id=9104;
        INSERT INTO roles(id,name,status,is_built_in) VALUES
            (9101,'scope all','ACTIVE',0),(9102,'scope self','ACTIVE',0),(9103,'scope org','ACTIVE',0);
        INSERT INTO role_data_scopes(role_id,domain,scope) VALUES
            (9101,'project','ALL'),(9102,'project','SELF'),(9103,'project','ORG');
        INSERT INTO user_roles(user_id,role_id) VALUES
            (9201,9101),(9202,9101),(9203,9102),(9205,9103),(9206,9101);
        INSERT INTO user_data_scopes(user_id,domain,scope) VALUES
            (9202,'project','SELF'),(9209,'project','ORG');
        """, null, ct);
}
