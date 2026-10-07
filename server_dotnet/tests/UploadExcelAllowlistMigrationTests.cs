using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests;

// 上传资料要求契约 2026-10-06：已有数据库的上传白名单只要允许 xlsx 就补上 xlsm/xlsb，且可重复执行。
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class UploadExcelAllowlistMigrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task ExistingAllowlistWithXlsxGainsMacroAndBinaryWorkbooksOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("upload_excel_allowlist", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006075638_HardenOemIdentityAndIndexes", ct);
        await database.ExecuteAsync("""
            INSERT INTO system_configs(cfg_key,cfg_value,description)
            VALUES('upload.allowed_exts','pdf, XLSX,step,xlsb','允许上传的扩展名白名单')
            ON DUPLICATE KEY UPDATE cfg_value=VALUES(cfg_value);
            INSERT INTO system_configs(cfg_key,cfg_value,description)
            VALUES('test.unrelated_exts','xlsx','不相关参数');
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal("pdf, XLSX,step,xlsb,xlsm", await connection.ExecuteScalarAsync<string>(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='upload.allowed_exts'"));
        Assert.Equal("xlsx", await connection.ExecuteScalarAsync<string>(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='test.unrelated_exts'"));
    }

    [Fact(Timeout = 120_000)]
    public async Task AllowlistWithoutXlsxIsLeftUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("upload_excel_no_xlsx", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261006075638_HardenOemIdentityAndIndexes", ct);
        await database.ExecuteAsync("""
            INSERT INTO system_configs(cfg_key,cfg_value,description)
            VALUES('upload.allowed_exts','pdf,step,xls','允许上传的扩展名白名单')
            ON DUPLICATE KEY UPDATE cfg_value=VALUES(cfg_value);
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal("pdf,step,xls", await connection.ExecuteScalarAsync<string>(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='upload.allowed_exts'"));
    }
}
