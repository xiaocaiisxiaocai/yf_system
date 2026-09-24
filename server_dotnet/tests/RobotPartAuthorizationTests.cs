using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class RobotPartAuthorizationTests
{
    [Fact(Timeout = 60_000)]
    public async Task CatalogPermissionsSeparateMaintainersCreatorsAndSuppliers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("robot_part_auth", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8001,'启用供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8002,'停用供应商','DISABLED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO roles(id,name,description,is_built_in,status)
            VALUES(9801,'Robot目录维护员','仅目录权限',0,'ACTIVE'),
                  (9802,'普通项目创建人','项目选项读取权限',0,'ACTIVE'),
                  (9803,'误授权供应商','供应商不得进入内部目录',0,'ACTIVE'),
                  (9804,'系统参数管理员','系统参数不再包含数据字典',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9804,id FROM permissions WHERE code='config:manage';
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9801,id FROM permissions WHERE code='dict:manage';
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9802,id FROM permissions WHERE code IN ('project:list','project:create');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9803,id FROM permissions WHERE code='dict:manage';
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9901,'part-maintainer','unused','目录维护员','','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9902,'part-creator','unused','普通创建人','','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9903,'part-supplier','unused','误授权供应商','','SUPPLIER',8001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9904,'config-only','unused','系统参数管理员','','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9901,9801),(9902,9802),(9903,9803),(9904,9804);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(9951,8001,'AUTH-A','启用料号',1,'ACTIVE'),
                  (9952,8002,'AUTH-B','停用供应商料号',2,'ACTIVE');
            """, ct);

        var service = new RobotPartService(new AuditService([]));
        var maintainer = new CurrentUser(9901, "part-maintainer", "INTERNAL", null);
        var creator = new CurrentUser(9902, "part-creator", "INTERNAL", null);
        var supplier = new CurrentUser(9903, "part-supplier", "SUPPLIER", 8001);
        var configOnly = new CurrentUser(9904, "config-only", "INTERNAL", null);
        await using var conn = await database.Database.OpenAsync(ct);

        var supplierOptions = await service.SupplierOptionsAsync(conn, maintainer, ct);
        Assert.Contains(supplierOptions, option => option is { Id: 8001, Status: "ACTIVE" });
        Assert.Contains(supplierOptions, option => option is { Id: 8002, Status: "DISABLED" });
        var maintainedParts = await service.ListAsync(conn, maintainer, null, enabledOnly: false, ct);
        Assert.Contains(maintainedParts, part => part.Id == 9951);
        Assert.Contains(maintainedParts, part => part.Id == 9952);

        var creatorParts = await service.ListAsync(conn, creator, 8001, enabledOnly: true, ct);
        Assert.Equal(9951UL, Assert.Single(creatorParts).Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.SupplierOptionsAsync(conn, creator, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, creator, new()
        {
            SupplierId = 8001,
            PartNumber = "AUTH-C",
            Model = "无管理权限",
            Enabled = true,
        }, null, ct))).Status);

        // Dictionary maintenance has its own permission; system parameters alone no longer grant it.
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.SupplierOptionsAsync(conn, configOnly, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, configOnly, new()
        {
            SupplierId = 8001,
            PartNumber = "AUTH-E",
            Model = "仅系统参数",
            Enabled = true,
        }, null, ct))).Status);

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.SupplierOptionsAsync(conn, supplier, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.ListAsync(conn, supplier, 8001, enabledOnly: true, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, supplier, new()
        {
            SupplierId = 8001,
            PartNumber = "AUTH-D",
            Model = "供应商越权",
            Enabled = true,
        }, null, ct))).Status);
    }
}
