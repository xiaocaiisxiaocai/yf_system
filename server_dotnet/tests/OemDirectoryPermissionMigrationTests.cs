using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class OemDirectoryPermissionMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task DeletePermissionsAreAddedByCodeAndGrantedOnlyToTheBuiltInAdministrator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("oem_delete_permissions", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006031500_ReplaceOemMalwareScanningWithValidation", ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at)
            VALUES(99,'厂商管理员',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 99,id FROM permissions WHERE code IN ('oem:company_manage','oem:account_manage');
            INSERT INTO permissions(id,code,name,type,parent_id,sort_no)
            VALUES(214,'custom:retained','保留的自定义权限','ACTION',NULL,100);
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal("custom:retained", await connection.ExecuteScalarAsync<string>("SELECT code FROM permissions WHERE id=214"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM permissions p INNER JOIN permissions parent ON parent.id=p.parent_id
            WHERE p.code IN ('oem:company_delete','oem:account_delete') AND parent.code='oem'
            """));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM role_permissions rp INNER JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=1 AND p.code IN ('oem:company_delete','oem:account_delete')
            """));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM role_permissions rp INNER JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=99 AND p.code IN ('oem:company_delete','oem:account_delete')
            """));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM role_permissions WHERE role_id=99"));
    }
}
