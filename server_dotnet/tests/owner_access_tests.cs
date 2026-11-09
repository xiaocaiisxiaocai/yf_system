using System.Globalization;
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

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class OwnerAccessTests
{
    private const ulong ProjectId = 10_001;
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
    public async Task EveryInternalAccountSeesEveryProjectWhileOnlyTheResponsibleUserChangesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await OwnerAccessDatabase.CreateOrSkipAsync(ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(SeedSql, ct);

        var creator = Internal(CreatorId, "owner-creator");
        var colleague = Internal(OldMemberId, "owner-old-member");
        var oldOwner = Internal(OldOwnerId, "owner-old");
        var newOwner = Internal(NewOwnerId, "owner-new");
        var ownerWithoutList = Internal(NoPermissionOwnerId, "owner-no-permission");
        var matchingSupplier = new CurrentUser(SupplierUserId, "owner-supplier", "SUPPLIER", 8_001);
        var otherSupplier = new CurrentUser(OtherSupplierUserId, "owner-other-supplier", "SUPPLIER", 8_002);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, database.Options);
        var users = new UserService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), audit);
        var roles = new RoleService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), audit);
        var admin = Internal(1, "admin");

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(OldOwnerId, (await ProjectAccessService.RequireViewAsync(conn, null, oldOwner, ProjectId, ct)).ResponsibleUserId);
        // Every internal account with project:list sees every project, owned or not.
        await ProjectAccessService.RequireViewAsync(conn, null, creator, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, colleague, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, colleague, OwnerlessProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, matchingSupplier, ProjectId, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, otherSupplier, ProjectId, ct));
        await AssertForbiddenAsync(() => ProjectAccessService.RequireViewAsync(conn, null, ownerWithoutList, 10_003, ct));

        Assert.Equal(new ulong[] { 10_003UL, OwnerlessProjectId, ProjectId }, await ListedProjectIdsAsync(projects, conn, colleague, ct));
        Assert.Equal(new ulong[] { 10_003UL, OwnerlessProjectId, ProjectId }, await ListedProjectIdsAsync(projects, conn, matchingSupplier, ct));
        Assert.Empty(await ListedProjectIdsAsync(projects, conn, otherSupplier, ct));
        Assert.Equal(new ulong[] { ProjectId }, await ListedProjectIdsAsync(projects, conn, oldOwner, ct, mine: true));
        Assert.Empty(await ListedProjectIdsAsync(projects, conn, colleague, ct, mine: true));

        using (var detail = Json(await projects.DetailAsync(conn, colleague, ProjectId, ct)))
        {
            Assert.Equal(OldOwnerId, detail.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.False(detail.RootElement.GetProperty("canManage").GetBoolean());
            Assert.False(detail.RootElement.TryGetProperty("members", out _));
        }
        Assert.True((await projects.DetailAsync(conn, oldOwner, ProjectId, ct)).CanManage);

        var files = FileService(database, audit);
        Assert.Equal(0UL, Total(await files.ListAsync(Context(oldOwner), ProjectId, ct)));
        Assert.Equal(0UL, Total(await files.ListAsync(Context(colleague), ProjectId, ct)));

        await AssertActiveResponsibilityBlocksChangeAsync(() => users.SetStatusAsync(admin, OldOwnerId, "DISABLED", ct));
        using (var department = JsonDocument.Parse("7002"))
            await AssertActiveResponsibilityBlocksChangeAsync(() => users.UpdateAsync(admin, OldOwnerId,
                new UserUpdate(DepartmentId: department.RootElement.Clone()), ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => users.AssignRoleAsync(admin, OldOwnerId, [9_003], ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => users.DeleteAsync(admin, OldOwnerId, ct));
        await AssertActiveResponsibilityBlocksChangeAsync(() => roles.AssignPermissionsAsync(admin, 9_001, [], ct));

        var updatedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT updated_at FROM projects WHERE id=@Id", new { Id = ProjectId }, cancellationToken: ct));
        var colleagueEdit = await Assert.ThrowsAsync<ApiException>(() =>
            projects.UpdateAsync(conn, colleague, ProjectId, UpdateRequest(updatedAt), null, ct));
        Assert.Equal((403, ProjectAccessService.OwnerOnlyMessage), (colleagueEdit.Status, colleagueEdit.Message));
        await projects.UpdateAsync(conn, oldOwner, ProjectId, UpdateRequest(updatedAt), null, ct);

        var created = await projects.CreateAsync(conn, creator, CreateRequest(), null, ct);
        Assert.Equal(CreatorId, created.ResponsibleUserId);
        Assert.Equal(7001UL, created.SectionId);
        Assert.Equal("IN_PROGRESS", created.Status);
        Assert.False((await projects.DetailAsync(conn, newOwner, created.Id, ct)).CanManage);
    }

    [Fact(Timeout = 120_000)]
    public async Task TransferMovesOwnershipAndWithItTheRightToChangeTheProject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await OwnerAccessDatabase.CreateOrSkipAsync(ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(SeedSql, ct);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, database.Options);
        var admin = Internal(1, "admin");
        var oldOwner = Internal(OldOwnerId, "owner-old");
        var newOwner = Internal(NewOwnerId, "owner-new");
        await using var conn = await database.Database.OpenAsync(ct);

        var options = (await projects.ProjectOwnerOptionsAsync(conn, admin, ct)).Select(owner => owner.Id).ToArray();
        Assert.Contains(NewOwnerId, options);
        Assert.Contains(1UL, options);
        Assert.DoesNotContain(NoPermissionOwnerId, options);
        Assert.DoesNotContain(SupplierUserId, options);
        await AssertForbiddenAsync(() => projects.ProjectOwnerOptionsAsync(conn, oldOwner, ct));

        // The owner lacks project:transfer here; the administrator may transfer any project.
        await AssertForbiddenAsync(() => projects.TransferAsync(conn, oldOwner, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct));
        await AssertBadRequestAsync(() => projects.TransferAsync(conn, admin, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = OldOwnerId }, null, ct));
        await AssertBadRequestAsync(() => projects.TransferAsync(conn, admin, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = NoPermissionOwnerId }, null, ct));
        await AssertBadRequestAsync(() => projects.TransferAsync(conn, admin, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = SupplierUserId }, null, ct));
        await AssertBadRequestAsync(() => projects.TransferAsync(conn, admin, ProjectId,
            new ProjectTransferRequest(), null, ct));

        // Ended projects move too.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET status='COMPLETED' WHERE id=@Id",
            new { Id = ProjectId }, cancellationToken: ct));

        var transferred = await projects.TransferAsync(conn, admin, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct);
        Assert.Equal(NewOwnerId, transferred.ResponsibleUserId);
        Assert.Equal(7001UL, transferred.SectionId);
        Assert.True((await projects.DetailAsync(conn, newOwner, ProjectId, ct)).CanManage);
        Assert.False((await projects.DetailAsync(conn, oldOwner, ProjectId, ct)).CanManage);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_UPDATE' AND target_type='project' AND target_id=@Id",
            new { Id = ProjectId.ToString(CultureInfo.InvariantCulture) }, cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task UnauthorizedWritersAreRejectedBeforeAnyProjectRowLockIsRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await OwnerAccessDatabase.CreateOrSkipAsync(ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync(SeedSql, ct);
        await database.SeedAsync("""
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9001,id FROM permissions
            WHERE code IN ('project:status','project:transfer','message:create','file:upload');
            """, ct);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, database.Options);
        var messages = new MessageService(audit, database.Options);
        // A colleague who can see the project and holds the permission points, but is not responsible for it.
        var colleague = Internal(OldMemberId, "owner-old-member");
        var otherSupplier = new CurrentUser(OtherSupplierUserId, "owner-other-supplier", "SUPPLIER", 8_002);

        // Another transaction owns the project row lock, as a running workflow write would.
        await using var holder = await database.Database.OpenAsync(ct);
        await using var holdTx = await holder.BeginTransactionAsync(ct);
        await holder.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE id=@Id FOR UPDATE", new { Id = ProjectId }, holdTx, cancellationToken: ct));

        await using var conn = await database.Database.OpenAsync(ct);
        // Ownership is authorized on an unlocked read first, so each call fails at once instead of queueing behind the lock.
        await AssertRejectedWithoutWaitingAsync(() => projects.SetStatusAsync(conn, colleague, ProjectId,
            new ProjectStatusRequest { Status = "TERMINATED" }, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => projects.UpdateAsync(conn, colleague, ProjectId,
            UpdateRequest(DateTime.UtcNow), null, ct));
        await AssertRejectedWithoutWaitingAsync(() => projects.DeleteAsync(conn, colleague, ProjectId, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => projects.ReopenAsync(conn, colleague, ProjectId,
            new ProjectReopenRequest { ExpectedUpdatedAt = DateTime.UtcNow, Reason = "越权重开" }, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => projects.ApproveMaterialsAsync(conn, colleague, ProjectId,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = 1 }, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => projects.TransferAsync(conn, colleague, ProjectId,
            new ProjectTransferRequest { ResponsibleUserId = NewOwnerId }, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => messages.CreateAsync(conn, colleague, ProjectId,
            new MessageCreateRequest { Content = "越权留言" }, null, ct));
        await AssertRejectedWithoutWaitingAsync(() => ProjectAccessService.RequireFileUploadAsync(conn, null, colleague, ProjectId, ct));
        await AssertRejectedWithoutWaitingAsync(() => messages.CreateAsync(conn, otherSupplier, ProjectId,
            new MessageCreateRequest { Content = "其他厂商留言" }, null, ct));
        await holdTx.CommitAsync(ct);
    }

    private static async Task AssertRejectedWithoutWaitingAsync(Func<Task> action)
    {
        // A call that queued behind the held lock would hit the timeout (TimeoutException) instead of a 403.
        var error = await Assert.ThrowsAsync<ApiException>(() => action().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(403, error.Status);
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
        INSERT INTO supplier_contacts(id,supplier_id,name,email,status) SELECT id+10000000,id,'Robot 负责人',CONCAT('contact',id,'@example.test'),'ACTIVE' FROM suppliers WHERE id NOT IN (SELECT supplier_id FROM supplier_contacts);
        INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
        VALUES(7001,NULL,'负责人测试课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7002,NULL,'负责人调岗课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO roles(id,name,description,is_built_in,status)
        VALUES
          (9001,'负责人范围用户','项目列表、创建和更新',0,'ACTIVE'),
          (9002,'负责人全局查看','仅项目列表',0,'ACTIVE'),
          (9003,'负责人无项目权限','不含项目列表',0,'ACTIVE'),
          (9004,'负责人测试厂商','厂商上传',0,'ACTIVE');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9004,id FROM permissions WHERE code IN ('project:list','file:upload','message:create');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9001,id FROM permissions WHERE code IN ('project:list','project:create','project:update');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT 9002,id FROM permissions WHERE code IN ('project:list');
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
        VALUES(9101,9001),(9102,9001),(9103,9001),(9104,9001),(9105,9002),(9201,9004),(9202,9004);
        INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
        VALUES(6002,8001,'OWNER-PART','负责人测试型号',1,'ACTIVE');
        INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
        VALUES(6003,'PRIORITY','负责人测试优先级',NULL,1,'ACTIVE'),
              (1006003,'ROBOT_TYPE','负责人测试优先级-Robot类型',NULL,1,'ACTIVE');
        INSERT INTO projects
          (id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,
           responsible_user_id,section_id,priority_id,robot_type_id,robot_owner_name,expected_completion_date,created_at,updated_at)
        VALUES
          (10001,'负责人范围项目','范围测试',8001,'IN_PROGRESS',9101,'M1',6002,9103,7001,6003,1006003,'Robot 负责人','2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10002,'无负责人历史项目','空负责人测试',8001,'IN_PROGRESS',9101,'M1',6002,NULL,7001,6003,1006003,'Robot 负责人','2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10003,'无列表权限负责人项目','权限门禁测试',8001,'IN_PROGRESS',9101,'M1',6002,9106,7001,6003,1006003,'Robot 负责人','2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO project_robot_owners(project_id,contact_id,sort_no)
        VALUES(10001,10008001,0),(10002,10008001,0),(10003,10008001,0);
        INSERT INTO project_work_orders(project_id,work_order_no,sort_no)
        VALUES(10001,'WO-OWNER',0),(10002,'WO-OWNERLESS',0),(10003,'WO-NO-LIST',0);
        """;

    private static ProjectUpsertRequest UpdateRequest(DateTime expectedUpdatedAt) => new()
    {
        Name = "负责人范围项目",
        Description = "范围测试",
        SupplierId = 8_001,
        WorkOrderNos = ["WO-OWNER"],
        MachineModel = "M1",
        RobotPartId = 6_002,
        PriorityId = 6_003,
        RobotTypeId = 1006003,
        RobotOwnerContactIds = [10008001],
        ExpectedCompletionDate = "2026-12-31",
        ExpectedUpdatedAt = expectedUpdatedAt,
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
        RobotTypeId = 1006003,
        RobotOwnerContactIds = [10008001],
        ExpectedCompletionDate = "2027-01-31",
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

    private static async Task<ulong[]> ListedProjectIdsAsync(
        ProjectService service,
        MySqlConnection conn,
        CurrentUser actor,
        CancellationToken ct,
        bool mine = false)
    {
        using var json = Json(await service.ListAsync(conn, actor, 1, 100, null, null, null, null, false, mine, ct));
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
        JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));

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
            var adminOptions = LocalMySqlTestServer.AdministrationOrSkip("Owner access tests");
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
            await SchemaBootstrap.InitializeEmptyAsync(Database, "Owner#" + Guid.NewGuid().ToString("N")[..12], ct);
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
