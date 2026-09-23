using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class SessionMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task UpgradePreservesFamilyAgeAndClampsExpiryWithoutRenewingSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("session_upgrade", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260923094642_AddRobotPartCatalog", ct);
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
            VALUES(9101,'migration-user','unused','migration','migration@example.invalid','INTERNAL','ACTIVE',0);
            INSERT INTO refresh_tokens(user_id,session_id,token_hash,expires_at,revoked,created_at)
            VALUES(9101,'family-a',REPEAT('a',64),'2026-09-05',1,'2026-08-01'),
                  (9101,'family-a',REPEAT('b',64),'2026-09-15',0,'2026-08-25'),
                  (9101,'family-b',REPEAT('c',64),'2026-09-03',0,'2026-09-01');
            """, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await using var connection = await database.Database.OpenAsync(ct);
        var rows = (await connection.QueryAsync<SessionRow>("""
            SELECT session_id AS Family, session_created_at AS Started, session_expires_at AS Deadline,
                   expires_at AS Expires, revoked AS Revoked FROM refresh_tokens ORDER BY id
            """)).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows.Take(2), row =>
        {
            Assert.Equal(new DateTime(2026, 8, 1), row.Started);
            Assert.Equal(new DateTime(2026, 8, 31), row.Deadline);
            Assert.Equal(row.Deadline, row.Expires);
        });
        Assert.True(rows[0].Revoked);
        Assert.False(rows[1].Revoked);
        Assert.Equal(new DateTime(2026, 9, 1), rows[2].Started);
        Assert.Equal(new DateTime(2026, 10, 1), rows[2].Deadline);
        Assert.Equal(new DateTime(2026, 9, 3), rows[2].Expires);
        var indexes = (await connection.QueryAsync<string>("""
            SELECT DISTINCT index_name FROM information_schema.statistics
            WHERE table_schema=DATABASE() AND index_name IN
              ('idx_refresh_tokens_session_expires','idx_msg_project_status_id','idx_audit_action_time','idx_audit_target')
            """)).ToArray();
        Assert.Equal(4, indexes.Length);
    }

    [Fact(Timeout = 120_000)]
    public async Task UpgradeRetiresManualAuditDeletionGrantAndRedundantActionIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("audit_delete_upgrade", ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260923141854_AddProjectCopyJobs", ct);
        await database.ExecuteAsync("""
            INSERT INTO permissions(id,code,name,type,parent_id,sort_no) VALUES
                (7,'log:audit','操作日志','MENU',NULL,6),(34,'log:view','日志查看','ACTION',7,25),
                (35,'log:delete','删除日志','ACTION',7,26);
            INSERT INTO roles(id,name,description,is_built_in,status) VALUES(9201,'日志管理员','迁移测试',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id) VALUES(9201,34),(9201,35);
            """, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions WHERE code='log:delete'"));
        Assert.Equal(new ulong[] { 34 }, (await connection.QueryAsync<ulong>(
            "SELECT permission_id FROM role_permissions WHERE role_id=9201")).ToArray());
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema=DATABASE() AND table_name='audit_logs' AND index_name='idx_audit_action'
            """));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics
            WHERE table_schema=DATABASE() AND table_name='audit_logs' AND index_name='idx_audit_action_time'
            """));
    }

    private sealed class SessionRow
    {
        public string Family { get; set; } = "";
        public DateTime Started { get; set; }
        public DateTime Deadline { get; set; }
        public DateTime Expires { get; set; }
        public bool Revoked { get; set; }
    }
}
