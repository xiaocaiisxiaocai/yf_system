using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class OwnerAccessTests
{
    private const ulong ProjectId = 10_001;
    private const ulong ProjectGroupId = 11_001;
    private const ulong OwnerlessProjectId = 10_002;
    private const ulong CreatorId = 9_101;
    private const ulong OldMemberId = 9_102;
    private const ulong OldOwnerId = 9_103;
    private const ulong NewOwnerId = 9_104;
    private const ulong ViewAllId = 9_105;
    private const ulong NoPermissionOwnerId = 9_106;
    private const ulong SupplierUserId = 9_201;
    private const ulong OtherSupplierUserId = 9_202;

    [Fact(Timeout = 120_000)]
    public async Task ResponsibleUserAndMainProjectCreatorScopeAccessAndCreationAssignsCreator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await OwnerAccessDatabase.CreateOrSkipAsync(ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(SeedSql, ct);

        var creator = Internal(CreatorId, "owner-creator");
        var oldMember = Internal(OldMemberId, "owner-old-member");
        var oldOwner = Internal(OldOwnerId, "owner-old");
        var newOwner = Internal(NewOwnerId, "owner-new");
        var viewAll = Internal(ViewAllId, "owner-view-all");
        var ownerWithoutList = Internal(NoPermissionOwnerId, "owner-no-permission");
        var matchingSupplier = new CurrentUser(SupplierUserId, "owner-supplier", "SUPPLIER", 8_001);
        var otherSupplier = new CurrentUser(OtherSupplierUserId, "owner-other-supplier", "SUPPLIER", 8_002);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, database.Options, groupStatus);
        var users = new UserService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), audit);
        var roles = new RoleService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), audit);
        var admin = Internal(1, "admin");

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(OldOwnerId, (await ProjectAccessService.RequireViewAsync(conn, null, oldOwner, ProjectId, ct)).ResponsibleUserId);
        // The main project's creator keeps access even though someone else is now responsible.
        await ProjectAccessService.RequireViewAsync(conn, null, creator, ProjectId, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, oldMember, ProjectId, ct));
        await ProjectAccessService.RequireViewAsync(conn, null, viewAll, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, viewAll, OwnerlessProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, matchingSupplier, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, matchingSupplier, OwnerlessProjectId, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, otherSupplier, ProjectId, ct));
        await AssertForbiddenAsync(() => ProjectAccessService.RequireViewAsync(conn, null, ownerWithoutList, 10_003, ct));

        Assert.Equal(new ulong[] { ProjectGroupId }, await ListedProjectGroupIdsAsync(groups, conn, oldOwner, ct));
        Assert.Equal(new ulong[] { 11_003UL, 11_002UL, ProjectGroupId },
            await ListedProjectGroupIdsAsync(groups, conn, creator, ct));
        Assert.Empty(await ListedProjectGroupIdsAsync(groups, conn, oldMember, ct));
        Assert.Equal(new ulong[] { 11_003UL, 11_002UL, ProjectGroupId },
            await ListedProjectGroupIdsAsync(groups, conn, viewAll, ct));
        Assert.Equal(new ulong[] { 11_003UL, 11_002UL, ProjectGroupId },
            await ListedProjectGroupIdsAsync(groups, conn, matchingSupplier, ct));

        using (var detail = Json(await projects.DetailAsync(conn, oldOwner, ProjectId, ct)))
        {
            Assert.Equal(OldOwnerId, detail.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.False(detail.RootElement.TryGetProperty("members", out _));
        }
        await projects.DetailAsync(conn, creator, ProjectId, ct);
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, oldMember, ProjectId, ct));

        var files = FileService(database, audit);
        Assert.Equal(0UL, Total(await files.ListAsync(Context(oldOwner), ProjectId, ct)));
        Assert.Equal(0UL, Total(await files.ListAsync(Context(creator), ProjectId, ct)));
        await AssertOutOfScopeAsync(() => files.ListAsync(Context(oldMember), ProjectId, ct));

        await AssertActiveResponsibilityBlocksChangeAsync(() => users.SetStatusAsync(admin, OldOwnerId, "DISABLED", ct));
        using (var department = JsonDocument.Parse("7002"))
            await AssertActiveResponsibilityBlocksChangeAsync(() => users.UpdateAsync(admin, OldOwnerId,
                new UserUpdate(DepartmentId: department.RootElement.Clone()), ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => users.AssignRoleAsync(admin, OldOwnerId, [9_003], ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => users.DeleteAsync(admin, OldOwnerId, ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => roles.AssignPermissionsAsync(admin, 9_001, [], ct));

        await groups.UpdateAsync(conn, oldOwner, ProjectGroupId, UpdateRequest(), null, ct);

        using (var detail = Json(await projects.DetailAsync(conn, oldOwner, ProjectId, ct)))
            Assert.Equal(OldOwnerId, detail.RootElement.GetProperty("responsibleUserId").GetUInt64());
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, newOwner, ProjectId, ct));
        Assert.Equal(0UL, Total(await files.ListAsync(Context(oldOwner), ProjectId, ct)));
        await AssertOutOfScopeAsync(() => files.ListAsync(Context(newOwner), ProjectId, ct));

        var createdGroup = await groups.CreateAsync(conn, creator, CreateRequest(), null, ct);
        var createdGroupId = Id(createdGroup);
        var createdId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId",
            new { GroupId = createdGroupId }, cancellationToken: ct));
        using (var created = Json(await projects.DetailAsync(conn, creator, createdId, ct)))
        {
            Assert.Equal(CreatorId, created.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.Equal(7001UL, created.RootElement.GetProperty("sectionId").GetUInt64());
        }
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, newOwner, createdId, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task TransferMovesOwnershipToEveryChildAndKeepsCreatorAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await OwnerAccessDatabase.CreateOrSkipAsync(ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(SeedSql, ct);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, database.Options, groupStatus);
        var admin = Internal(1, "admin");
        var creator = Internal(CreatorId, "owner-creator");
        var oldOwner = Internal(OldOwnerId, "owner-old");
        var newOwner = Internal(NewOwnerId, "owner-new");
        await using var conn = await database.Database.OpenAsync(ct);

        var options = (await projects.ProjectOwnerOptionsAsync(conn, admin, ct)).Select(owner => owner.Id).ToArray();
        Assert.Contains(NewOwnerId, options);
        Assert.Contains(1UL, options);
        Assert.DoesNotContain(NoPermissionOwnerId, options);
        Assert.DoesNotContain(SupplierUserId, options);
        await AssertForbiddenAsync(() => projects.ProjectOwnerOptionsAsync(conn, oldOwner, ct));

        await AssertForbiddenAsync(() => groups.TransferAsync(conn, oldOwner, ProjectGroupId,
            new ProjectGroupTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct));
        await AssertBadRequestAsync(() => groups.TransferAsync(conn, admin, ProjectGroupId,
            new ProjectGroupTransferRequest { ResponsibleUserId = OldOwnerId }, null, ct));
        await AssertBadRequestAsync(() => groups.TransferAsync(conn, admin, ProjectGroupId,
            new ProjectGroupTransferRequest { ResponsibleUserId = NoPermissionOwnerId }, null, ct));
        await AssertBadRequestAsync(() => groups.TransferAsync(conn, admin, ProjectGroupId,
            new ProjectGroupTransferRequest { ResponsibleUserId = SupplierUserId }, null, ct));
        await AssertBadRequestAsync(() => groups.TransferAsync(conn, admin, ProjectGroupId,
            new ProjectGroupTransferRequest(), null, ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET status='PENDING_CONFIRMATION',confirm_side='COMPANY' WHERE id=@Id",
            new { Id = ProjectId }, cancellationToken: ct));
        var pending = await Assert.ThrowsAsync<ApiException>(() => groups.TransferAsync(conn, admin, ProjectGroupId,
            new ProjectGroupTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct));
        Assert.Equal(409, pending.Status);
        // Ended subprojects move too: access follows the owner.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET status='COMPLETED',confirm_side=NULL WHERE id=@Id",
            new { Id = ProjectId }, cancellationToken: ct));

        using (var transferred = Json(await groups.TransferAsync(conn, admin, ProjectGroupId,
                   new ProjectGroupTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct)))
        {
            Assert.Equal(NewOwnerId, transferred.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.Equal(7001UL, transferred.RootElement.GetProperty("sectionId").GetUInt64());
        }
        Assert.Equal(NewOwnerId, await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT responsible_user_id FROM projects WHERE id=@Id", new { Id = ProjectId }, cancellationToken: ct)));
        await ProjectAccessService.RequireViewAsync(conn, null, newOwner, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, creator, ProjectId, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, oldOwner, ProjectId, ct));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_GROUP_TRANSFER' AND target_id=@Id",
            new { Id = ProjectGroupId.ToString() }, cancellationToken: ct)));
    }

    private static async Task AssertBadRequestAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(400, error.Status);
    }

    private static readonly string SeedSql = """
        UPDATE users SET must_change_password=0 WHERE id=1;
        INSERT INTO suppliers(id,name,status,created_by,created_at,updated_at)
        VALUES
          (8001,'负责人测试供应商','ACTIVE',1,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (8002,'其他供应商','ACTIVE',1,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
        VALUES(7001,NULL,'负责人测试课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7002,NULL,'负责人调岗课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO roles(id,name,description,is_built_in,status)
        VALUES
          (9001,'负责人范围用户','项目列表、创建和更新',0,'ACTIVE'),
          (9002,'负责人全局查看','项目全局查看',0,'ACTIVE'),
          (9003,'负责人无项目权限','不含项目列表',0,'ACTIVE');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9001,id FROM permissions WHERE code IN ('project:list','project:create','project:update');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9002,id FROM permissions WHERE code IN ('project:list','project:view_all');
        INSERT INTO users
          (id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
        VALUES
          (9101,'owner-creator','unused','旧创建人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9102,'owner-old-member','unused','旧成员','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9103,'owner-old','unused','旧负责人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9104,'owner-new','unused','新负责人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9105,'owner-view-all','unused','全局查看人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9106,'owner-no-permission','unused','无列表权限负责人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9201,'owner-supplier','unused','匹配供应商用户','','SUPPLIER',8001,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (9202,'owner-other-supplier','unused','其他供应商用户','','SUPPLIER',8002,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO user_roles(user_id,role_id)
        VALUES(9101,9001),(9102,9001),(9103,9001),(9104,9001),(9105,9002),(9201,9001),(9202,9001);
        INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
        VALUES(6002,8001,'OWNER-PART','负责人测试型号',1,'ACTIVE');
        INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
        VALUES(6003,'PRIORITY','负责人测试优先级',NULL,1,'ACTIVE');
        INSERT INTO project_groups
          (id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,
           responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
        VALUES
          (11001,'负责人范围主项目','范围测试',8001,'DRAFT',9101,'M1',6002,9103,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (11002,'无负责人历史主项目','空负责人测试',8001,'DRAFT',9101,'M1',6002,NULL,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (11003,'无列表权限负责人主项目','权限门禁测试',8001,'DRAFT',9101,'M1',6002,9106,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO projects
          (id,project_group_id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,
           responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
        VALUES
          (10001,11001,'负责人范围项目','范围测试',8001,'DRAFT',9101,'M1',6002,9103,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10002,11002,'无负责人历史项目','空负责人测试',8001,'DRAFT',9101,'M1',6002,NULL,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10003,11003,'无列表权限负责人项目','权限门禁测试',8001,'DRAFT',9101,'M1',6002,9106,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no)
        VALUES(11001,'WO-OWNER',0),(11002,'WO-OWNERLESS',0),(11003,'WO-NO-LIST',0);
        INSERT INTO project_work_orders(project_id,work_order_no,sort_no)
        VALUES(10001,'WO-OWNER',0),(10002,'WO-OWNERLESS',0),(10003,'WO-NO-LIST',0);
        """;

    private static ProjectUpsertRequest UpdateRequest() => new()
    {
        Name = "负责人范围项目",
        Description = "范围测试",
        SupplierId = 8_001,
        WorkOrderNos = ["WO-OWNER"],
        MachineModel = "M1",
        RobotPartId = 6_002,
        PriorityId = 6_003,
        ExpectedCompletionDate = "2026-12-31",
    };

    private static ProjectUpsertRequest CreateRequest() => new()
    {
        Name = "创建后仅负责人可见",
        Description = "不写项目成员",
        SupplierId = 8_001,
        WorkOrderNos = ["WO-CREATED-OWNER"],
        MachineModel = "M2",
        RobotPartId = 6_002,
        PriorityId = 6_003,
        ExpectedCompletionDate = "2027-01-31",
        SubprojectNames = ["创建后子项目"],
    };

    private static FileService FileService(OwnerAccessDatabase database, AuditService audit)
    {
        var identity = new IdentityService(
            EfTestSupport.DbContextFactory(database.Options),
            database.Options,
            new LoginRateLimiter(),
            new TokenService(database.Options),
            new PermissionService(),
            audit);
        return new(
            database.Database,
            database.Options,
            audit,
            new BatchDownloadLimiter(),
            new MediaGrantService(database.Options),
            new DownloadGrantService(),
            identity);
    }

    private static DefaultHttpContext Context(CurrentUser actor)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(CurrentUser)] = actor;
        return context;
    }

    private static CurrentUser Internal(ulong id, string employeeNo) => new(id, employeeNo, "INTERNAL", null);

    private static async Task<ulong[]> ListedProjectGroupIdsAsync(
        ProjectGroupService service,
        MySqlConnection conn,
        CurrentUser actor,
        CancellationToken ct)
    {
        using var json = Json(await service.ListAsync(conn, actor, 1, 100, null, null, null, ct));
        return json.RootElement.GetProperty("list").EnumerateArray()
            .Select(item => item.GetProperty("id").GetUInt64()).ToArray();
    }

    private static async Task AssertOutOfScopeAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(403, error.Status);
        Assert.Equal(40302, error.Code);
    }

    private static async Task AssertForbiddenAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(403, error.Status);
        Assert.Equal(40301, error.Code);
    }

    private static async Task AssertActiveResponsibilityBlocksChangeAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(400, error.Status);
        Assert.Contains("结束相关项目", error.Message, StringComparison.Ordinal);
    }

    private static ulong Total(object value)
    {
        using var json = Json(value);
        return json.RootElement.GetProperty("total").GetUInt64();
    }

    private static ulong Id(object value)
    {
        using var json = Json(value);
        return json.RootElement.GetProperty("id").GetUInt64();
    }

    private static JsonDocument Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private sealed class OwnerAccessDatabase(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        internal AppDb Database { get; } = database;
        internal AppOptions Options { get; } = options;

        internal static async Task<OwnerAccessDatabase> CreateOrSkipAsync(CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Owner access tests only allow local MySQL");
            var credentials = uri.UserInfo.Split(':', 2);
            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None,
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_t_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var storageRoot = Path.Combine(Path.GetTempPath(), "yf_owner_access_" + Guid.NewGuid().ToString("N"));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 5,
                        MinimumPoolSize = 0,
                    }.ConnectionString,
                    StorageRoot = storageRoot,
                    JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    WorkerEnabled = false,
                };
                return new(administration, databaseName, new AppDb(options), options);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        internal async Task InitializeAsync(CancellationToken ct)
        {
            var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "Owner#" + Guid.NewGuid().ToString("N")[..12]);
            try { await SchemaBootstrap.InitializeEmptyAsync(Database, ct); }
            finally { Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword); }
        }

        internal async Task SeedAsync(string sql, CancellationToken ct)
        {
            await using var conn = await Database.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }
}
