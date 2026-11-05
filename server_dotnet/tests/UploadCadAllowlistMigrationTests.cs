using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests;

// Robot 与 ODM/OEMA 的上传白名单补上 SOLIDWORKS 等 CAD 导出格式：只追加缺失项，管理员的其他配置不动，可重复执行。
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class UploadCadAllowlistMigrationTests
{
    private const string Previous = "20261105082534_MergeOemLeaderApproverSources";

    private static readonly string[] Added =
    [
        "3dxml", "3mf", "ai", "amf", "eprt", "glb", "gltf", "hcg", "hsf", "ifc", "prt", "prtdot", "ply", "psd",
        "sat", "sldftp", "sldlfp", "sldprt", "tif", "vda", "wrl", "x_b", "x_t", "xaml",
    ];

    [Fact(Timeout = 120_000)]
    public async Task ExistingAllowlistsGainTheCadExportFormatsOnceAndKeepAdminChoices()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("upload_cad_allowlist", ct);
        await database.InitializeAsync(ct);
        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous, ct);
        // The admin trimmed the Robot list (no dwg) and already allows one of the new formats in upper case.
        await database.ExecuteAsync("""
            UPDATE system_configs SET cfg_value='step,stp,xlsx,zip, SLDPRT' WHERE cfg_key='upload.allowed_exts';
            UPDATE system_configs SET cfg_value='pdf,step' WHERE cfg_key='oem.upload.allowed_exts.internal_to_oem';
            UPDATE system_configs SET cfg_value='pdf,stp' WHERE cfg_key='oem.upload.allowed_exts.oem_to_internal';
            INSERT INTO system_configs(cfg_key,cfg_value,description) VALUES('test.unrelated_exts','pdf','不相关参数');
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(Previous, ct);
        await migrator.MigrateAsync(cancellationToken: ct);

        await using var connection = await database.Database.OpenAsync(ct);
        var values = (await connection.QueryAsync<(string Key, string Value)>(
            "SELECT cfg_key, cfg_value FROM system_configs WHERE cfg_key LIKE '%allowed_exts%' OR cfg_key='test.unrelated_exts'"))
            .ToDictionary(row => row.Key, row => row.Value);

        var robot = Split(values["upload.allowed_exts"]);
        Assert.Equal(["step", "stp", "xlsx", "zip", "sldprt", .. Added.Where(item => item != "sldprt")], robot);
        Assert.DoesNotContain("dwg", robot);
        Assert.Equal(["pdf", "step", .. Added], Split(values["oem.upload.allowed_exts.internal_to_oem"]));
        Assert.Equal(["pdf", "stp", .. Added], Split(values["oem.upload.allowed_exts.oem_to_internal"]));
        Assert.Equal("pdf", values["test.unrelated_exts"]);
    }

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => item.ToLowerInvariant()).ToArray();
}
