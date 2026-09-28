using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class AdminLifecycleTests
{
    [Fact(Timeout = 120_000)]
    public async Task DisabledDepartmentBlocksInternalAccountReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("admin_lifecycle", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(1,'lifecycle-admin','unused','生命周期管理员','admin@example.invalid','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO user_roles(user_id,role_id) VALUES(1,1);
            INSERT INTO departments(id,name,parent_id,kind,sort_no,status,created_at,updated_at)
            VALUES(7001,'已禁用课别',NULL,'SECTION',0,'DISABLED',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(7002,'disabled-dept-user','unused','组织账号','dept@example.invalid','INTERNAL',7001,'DISABLED',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO user_roles(user_id,role_id) VALUES(7002,3);
            """, ct);

        var actor = new CurrentUser(1, "lifecycle-admin", "INTERNAL", null);
        var audit = new AuditService([]);
        var users = new UserService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), audit);
        var departmentError = await Assert.ThrowsAsync<ApiException>(
            () => users.SetStatusAsync(actor, 7002, "ACTIVE", ct));
        Assert.Equal("组织已被禁用", departmentError.Message);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal("DISABLED", await conn.QuerySingleAsync<string>(
            "SELECT status FROM users WHERE id=7002"));
    }

    [Fact(Timeout = 120_000)]
    public async Task ActiveProjectOwnerKeepsAnEffectiveSectionAcrossDepartmentChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("department_owner_guard", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(1,'department-admin','unused','组织管理员','admin@example.invalid','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO user_roles(user_id,role_id) VALUES(1,1);
            INSERT INTO suppliers(id,name,status,created_by,created_at,updated_at)
            VALUES(8001,'组织边界供应商','ACTIVE',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO departments(id,name,parent_id,kind,sort_no,status,created_at,updated_at)
            VALUES
              (7100,'在用事业部',NULL,'DIVISION',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7101,'在用部门',7100,'DEPARTMENT',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7102,'在用课别',7101,'SECTION',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7200,'停用事业部',NULL,'DIVISION',2,'DISABLED',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7201,'停用链部门',7200,'DEPARTMENT',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7202,'停用链课别',7201,'SECTION',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7300,'目标事业部',NULL,'DIVISION',3,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7301,'目标部门',7300,'DEPARTMENT',1,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
              (7500,'既有根课别',NULL,'SECTION',4,'ACTIVE',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(7103,'department-owner','unused','在用负责人','owner@example.invalid','INTERNAL',7102,'ACTIVE',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                  (7203,'legacy-invalid-owner','unused','既有失效负责人','legacy@example.invalid','INTERNAL',7202,'ACTIVE',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                  (7501,'root-section-owner','unused','根课别负责人','root@example.invalid','INTERNAL',7500,'ACTIVE',0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO user_roles(user_id,role_id) VALUES(7103,1),(7203,1),(7501,1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id,section_id,created_at,updated_at)
            VALUES(7400,'组织边界主项目',8001,'IN_PROGRESS',1,7103,7102,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                  (7401,'既有失效组织主项目',8001,'IN_PROGRESS',1,7203,7202,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                  (7402,'既有根课别主项目',8001,'DRAFT',1,7501,7500,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, ct);

        var actor = new CurrentUser(1, "department-admin", "INTERNAL", null);
        var departments = new DepartmentService(EfTestSupport.DbContextFactory(database.Options), new AuditService([]));

        var renamed = Json(await departments.UpdateAsync(actor, 7102,
            new DepartmentUpsert("在用课别（已重命名）", 7101, 8), ct));
        Assert.Equal("SECTION", renamed.GetProperty("kind").GetString());
        Assert.Equal(8, renamed.GetProperty("sortNo").GetInt32());

        var renamedRootSection = Json(await departments.UpdateAsync(actor, 7500,
            new DepartmentUpsert("既有根课别（已重命名）", null, 9), ct));
        Assert.Equal("SECTION", renamedRootSection.GetProperty("kind").GetString());
        Assert.Equal(9, renamedRootSection.GetProperty("sortNo").GetInt32());

        var existingInvalidBranch = Json(await departments.SetStatusAsync(actor, 7201, "DISABLED", ct));
        Assert.Equal("DISABLED", existingInvalidBranch.GetProperty("status").GetString());

        var disableError = await Assert.ThrowsAsync<ApiException>(() =>
            departments.SetStatusAsync(actor, 7100, "DISABLED", ct));
        Assert.Contains("请先结束相关项目", disableError.Message, StringComparison.Ordinal);

        var invalidMove = await Assert.ThrowsAsync<ApiException>(() =>
            departments.UpdateAsync(actor, 7102,
                new DepartmentUpsert("在用课别（已重命名）", 7201, 8), ct));
        Assert.Contains("失去有效课别", invalidMove.Message, StringComparison.Ordinal);

        var moved = Json(await departments.UpdateAsync(actor, 7102,
            new DepartmentUpsert("在用课别（已重命名）", 7301, 8), ct));
        Assert.Equal((ulong)7301, moved.GetProperty("parentId").GetUInt64());
        Assert.Equal("SECTION", moved.GetProperty("kind").GetString());

        var newAncestorDisable = await Assert.ThrowsAsync<ApiException>(() =>
            departments.SetStatusAsync(actor, 7300, "DISABLED", ct));
        Assert.Contains("请先结束相关项目", newAncestorDisable.Message, StringComparison.Ordinal);

        await using var context = await EfTestSupport.DbContextFactory(database.Options).CreateDbContextAsync(ct);
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var conn = context.Database.Connection();
        var inactiveAncestor = await Assert.ThrowsAsync<ApiException>(() =>
            DepartmentService.EnsureActiveAsync(context, 7202, ct));
        Assert.Equal("组织已被禁用", inactiveAncestor.Message);
        await tx.RollbackAsync(ct);

        var persisted = await conn.QuerySingleAsync<DepartmentState>(
            "SELECT parent_id ParentId,kind Kind,status Status FROM departments WHERE id=7102");
        Assert.Equal((ulong)7301, persisted.ParentId);
        Assert.Equal("SECTION", persisted.Kind);
        Assert.Equal("ACTIVE", persisted.Status);
        Assert.Equal("ACTIVE", await conn.QuerySingleAsync<string>(
            "SELECT status FROM departments WHERE id=7300"));
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value, TestJson.Web);

    private sealed class DepartmentState
    {
        public ulong? ParentId { get; init; }
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
    }
}
