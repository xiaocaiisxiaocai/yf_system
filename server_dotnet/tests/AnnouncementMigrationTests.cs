using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests;

public sealed class AnnouncementMigrationTests
{
    [Fact(Timeout = 180_000)]
    public async Task UpgradeSeedsExistingInstallationAndRollbackPreservesCustomizedRoleGrants()
    {
        const string previous = "20261126031537_AddAccountNetworkScope";
        const string current = "20261128074749_AddAnnouncements";
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("announcement_upgrade", ct);
        await database.InitializeAsync(ct);
        await using var db = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrations = db.GetService<IMigrator>();

        // Recreate the state of an existing, pre-announcement installation, including a user-created role.
        await migrations.MigrateAsync(previous, ct);
        await database.ExecuteAsync("""
            DELETE r FROM role_permissions r JOIN permissions p ON p.id=r.permission_id
                WHERE p.code='announcement' OR p.code LIKE 'announcement:%';
            DELETE FROM permissions WHERE code LIKE 'announcement:%';
            DELETE FROM permissions WHERE code='announcement';
            DELETE FROM role_data_scopes WHERE domain='announcement';
            INSERT INTO roles(name,is_built_in,status) VALUES('公告升级自定义角色',0,'ACTIVE');
            """, ct);
        await migrations.MigrateAsync(current, ct);

        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(5, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM role_permissions g JOIN roles r ON r.id=g.role_id
                JOIN permissions p ON p.id=g.permission_id
                WHERE r.is_built_in=1 AND r.name='系统管理员' AND (p.code='announcement' OR p.code LIKE 'announcement:%')
                """));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM role_permissions g JOIN roles r ON r.id=g.role_id WHERE r.name='公告升级自定义角色'
                """));
            await conn.ExecuteAsync("""
                INSERT INTO role_permissions(role_id,permission_id)
                    SELECT r.id,p.id FROM roles r JOIN permissions p ON p.code='announcement:reads'
                    WHERE r.name='公告升级自定义角色';
                INSERT INTO role_data_scopes(role_id,domain,scope)
                    SELECT id,'announcement','ORG' FROM roles WHERE name='公告升级自定义角色';
                """);
        }

        async Task Verify(string expectedMigration, int expectedTableCount)
        {
            await using var conn = await database.Database.OpenAsync(ct);
            Assert.Equal(expectedMigration, await conn.ExecuteScalarAsync<string>("SELECT MAX(MigrationId) FROM __EFMigrationsHistory"));
            Assert.Equal(expectedTableCount, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()
                AND table_name IN ('announcements','announcement_reads','announcement_companies','announcement_departments')
                """));
            Assert.Equal(new[] { "announcement:reads" }, await conn.QueryAsync<string>("""
                SELECT p.code FROM role_permissions g JOIN permissions p ON p.id=g.permission_id
                JOIN roles r ON r.id=g.role_id WHERE r.name='公告升级自定义角色'
                """));
            Assert.Equal("ORG", await conn.ExecuteScalarAsync<string>("""
                SELECT s.scope FROM role_data_scopes s JOIN roles r ON r.id=s.role_id
                WHERE r.name='公告升级自定义角色' AND s.domain='announcement'
                """));
        }

        await Verify(current, 4);
        await migrations.MigrateAsync(previous, ct);
        await Verify(previous, 0);
        await migrations.MigrateAsync(current, ct);
        await Verify(current, 4);

        // Upgrade a genuine plain-text announcement from the original schema, including a pin and a read.
        await database.ExecuteAsync("""
            INSERT INTO announcements(created_by,department_name,title,body,internal_scope,status,version,
                                      created_at,updated_at,published_at,pinned_at)
                VALUES(1,'研发部','旧公告','旧公告的第二行','ALL','PUBLISHED',1,
                       UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO announcement_reads(announcement_id,realm,account_id,account_name,display_name,first_read_at,last_read_at)
                SELECT id,'internal',1,'admin','管理员',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)
                FROM announcements WHERE title='旧公告';
            """, ct);
        const string rich = "20261128092337_AddAnnouncementRichContent";
        await migrations.MigrateAsync(rich, ct);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM announcements a JOIN announcement_reads r ON r.announcement_id=a.id
                WHERE a.title='旧公告' AND a.body='旧公告的第二行' AND a.document_json IS NULL AND a.create_key IS NULL
                AND a.pinned_at IS NOT NULL AND a.normal_sort_order IS NULL AND a.pinned_sort_order IS NULL
                AND r.account_name='admin'
                """));
        }
        await migrations.MigrateAsync(current, ct);
        await Verify(current, 4);
        await migrations.MigrateAsync(rich, ct);
        await Verify(rich, 4);
    }
}
