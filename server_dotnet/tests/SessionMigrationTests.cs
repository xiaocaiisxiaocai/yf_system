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

    private sealed class SessionRow
    {
        public string Family { get; set; } = "";
        public DateTime Started { get; set; }
        public DateTime Deadline { get; set; }
        public DateTime Expires { get; set; }
        public bool Revoked { get; set; }
    }
}
