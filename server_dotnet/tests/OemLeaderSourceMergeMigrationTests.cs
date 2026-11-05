using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class OemLeaderSourceMergeMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task EachTemplateKeepsOneDirectLeaderNodeAndStartedApprovalsAreUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("oem_leader_merge", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261102012333_RemoveOemCompanyPrimaryContact", ct);
        // Recreate the pre-merge shapes: the old default template (section enabled, department
        // disabled with a fallback), a template whose first leader node is disabled, a template
        // with only a disabled leader node, and a started approval on a legacy source.
        await database.ExecuteAsync("""
            SET FOREIGN_KEY_CHECKS=0;
            DELETE FROM oem_flow_template_nodes WHERE template_id=1;
            INSERT INTO oem_flow_template_nodes(id,template_id,sort_no,name,approver_source,approval_mode,self_policy,enabled)
            VALUES(101,1,1,'课别主管审批','SECTION_LEADER','SINGLE','SKIP',1),
                  (102,1,2,'部门主管审批','DEPARTMENT_LEADER','SINGLE','DESIGNATED',0),
                  (201,2,1,'事业部审批','DIVISION_LEADER','SINGLE','SKIP',0),
                  (202,2,2,'品保会签','SPECIFIED_USERS','ALL','BLOCK',1),
                  (203,2,3,'部长审批','DEPARTMENT_LEADER','SINGLE','SKIP',1),
                  (301,3,1,'课别主管审批','SECTION_LEADER','SINGLE','SKIP',0);
            INSERT INTO oem_flow_templates(id,name,is_default,status,concurrency_version,created_by,created_at,updated_at)
            VALUES(2,'品保模板',0,'ACTIVE',4,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (3,'停用节点模板',0,'ACTIVE',0,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO oem_flow_template_node_users(node_id,user_id,role) VALUES(102,1,'FALLBACK'),(202,1,'APPROVER');
            INSERT INTO oem_flow_instances(id,transfer_id,template_id,initiator_user_id,initiator_section_id,status,template_snapshot,concurrency_version,created_at,updated_at)
            VALUES(1,1,1,1,1,'IN_PROGRESS','{}',0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO oem_flow_instance_nodes(id,instance_id,sort_no,name,approver_source,approval_mode,status,used_fallback)
            VALUES(1,1,1,'课别主管审批','SECTION_LEADER','SINGLE','PENDING',0);
            SET FOREIGN_KEY_CHECKS=1;
            """, ct);
        Dictionary<ulong, ulong> versionsBefore;
        await using (var before = await database.Database.OpenAsync(ct)) versionsBefore = await VersionsAsync(before);

        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        var nodes = (await connection.QueryAsync<(ulong Id, ulong TemplateId, string Name, string Source, bool Enabled)>(
            "SELECT id, template_id, name, approver_source, enabled FROM oem_flow_template_nodes ORDER BY template_id, sort_no")).ToArray();
        Assert.Equal(
            [
                (101UL, 1UL, "上级主管审批", "DIRECT_LEADER", true),
                (202UL, 2UL, "品保会签", "SPECIFIED_USERS", true),
                (203UL, 2UL, "部长审批", "DIRECT_LEADER", true),
                (301UL, 3UL, "上级主管审批", "DIRECT_LEADER", false),
            ],
            nodes);
        var nodeUsers = (await connection.QueryAsync<ulong>("SELECT node_id FROM oem_flow_template_node_users")).ToArray();
        Assert.Equal([202UL], nodeUsers);
        var versionsAfter = await VersionsAsync(connection);
        foreach (var (id, version) in versionsBefore) Assert.Equal(version + 1, versionsAfter[id]);
        Assert.Equal("SECTION_LEADER", await connection.ExecuteScalarAsync<string>("SELECT approver_source FROM oem_flow_instance_nodes WHERE id=1"));
    }

    private static async Task<Dictionary<ulong, ulong>> VersionsAsync(System.Data.Common.DbConnection connection) => (await connection.QueryAsync<(ulong Id, ulong Version)>("SELECT id, concurrency_version FROM oem_flow_templates"))
            .ToDictionary(row => row.Id, row => row.Version);
}
