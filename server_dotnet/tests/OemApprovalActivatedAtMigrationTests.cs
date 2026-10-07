using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class OemApprovalActivatedAtMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task PendingTasksAreBackfilledFromTheirActivationEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("oem_task_activated_at", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006075638_HardenOemIdentityAndIndexes", ct);
        // Only the approval rows matter here; the transfer/template parents are irrelevant to the backfill.
        await database.ExecuteAsync("""
            SET FOREIGN_KEY_CHECKS=0;
            INSERT INTO oem_flow_instances(id,transfer_id,template_id,initiator_user_id,initiator_section_id,status,template_snapshot,concurrency_version,created_at,updated_at)
            VALUES(1,1,1,1,1,'IN_PROGRESS','{}',3,'2026-10-01 08:00:00.000','2026-10-01 12:00:00.000');
            INSERT INTO oem_flow_instance_nodes(id,instance_id,sort_no,name,approver_source,approval_mode,status,used_fallback,completed_at)
            VALUES(1,1,1,'课长','SECTION_LEADER','ANY','APPROVED',0,'2026-10-01 10:00:00.000'),
                  (2,1,2,'部长','DEPARTMENT_LEADER','ALL','PENDING',0,NULL);
            INSERT INTO oem_flow_tasks(id,instance_id,instance_node_id,approver_user_id,status,replaces_task_id,decided_at,concurrency_version,created_at)
            VALUES(1,1,1,10,'APPROVED',NULL,'2026-10-01 10:00:00.000',1,'2026-10-01 08:00:00.000'),
                  (2,1,2,11,'PENDING',NULL,NULL,1,'2026-10-01 08:00:00.000'),
                  (3,1,2,12,'SUPERSEDED',NULL,'2026-10-01 11:00:00.000',2,'2026-10-01 08:00:00.000'),
                  (4,1,2,13,'PENDING',3,NULL,0,'2026-10-01 11:00:00.000');
            SET FOREIGN_KEY_CHECKS=1;
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        var activated = (await connection.QueryAsync<(ulong Id, DateTime? ActivatedAt)>(
            "SELECT id, activated_at FROM oem_flow_tasks ORDER BY id")).ToDictionary(row => row.Id, row => row.ActivatedAt);
        Assert.Null(activated[1]);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), activated[2]);
        Assert.Null(activated[3]);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), activated[4]);
    }
}
