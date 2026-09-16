using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
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
    public async Task ResponsibleUserIsTheOnlyNonGlobalInternalProjectScope()
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
        var projects = new ProjectService(audit, database.Options);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal(OldOwnerId, (await ProjectAccessService.RequireViewAsync(conn, null, oldOwner, ProjectId, ct)).ResponsibleUserId);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, creator, ProjectId, ct));
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, oldMember, ProjectId, ct));
        await ProjectAccessService.RequireViewAsync(conn, null, viewAll, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, viewAll, OwnerlessProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, matchingSupplier, ProjectId, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, matchingSupplier, OwnerlessProjectId, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewAsync(conn, null, otherSupplier, ProjectId, ct));
        await AssertForbiddenAsync(() => ProjectAccessService.RequireViewAsync(conn, null, ownerWithoutList, 10_003, ct));

        Assert.Equal(new ulong[] { ProjectId }, await ListedProjectIdsAsync(projects, conn, oldOwner, ct));
        Assert.Empty(await ListedProjectIdsAsync(projects, conn, creator, ct));
        Assert.Empty(await ListedProjectIdsAsync(projects, conn, oldMember, ct));
        Assert.Equal(new ulong[] { 10_003UL, OwnerlessProjectId, ProjectId },
            await ListedProjectIdsAsync(projects, conn, viewAll, ct));
        Assert.Equal(new ulong[] { 10_003UL, OwnerlessProjectId, ProjectId },
            await ListedProjectIdsAsync(projects, conn, matchingSupplier, ct));

        using (var detail = Json(await projects.DetailAsync(conn, oldOwner, ProjectId, ct)))
        {
            Assert.Equal(OldOwnerId, detail.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.False(detail.RootElement.TryGetProperty("members", out _));
        }
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, creator, ProjectId, ct));
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, oldMember, ProjectId, ct));

        var files = FileService(database, audit);
        Assert.Equal(0UL, Total(await files.ListAsync(Context(oldOwner), ProjectId, ct)));
        await AssertOutOfScopeAsync(() => files.ListAsync(Context(creator), ProjectId, ct));
        await AssertOutOfScopeAsync(() => files.ListAsync(Context(oldMember), ProjectId, ct));

        var historicalMembers = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_members WHERE project_id=@ProjectId",
            new { ProjectId }, cancellationToken: ct));
        Assert.Equal(2, historicalMembers);

        await projects.UpdateAsync(conn, oldOwner, ProjectId, UpdateRequest(NewOwnerId), null, ct);

        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, oldOwner, ProjectId, ct));
        using (var detail = Json(await projects.DetailAsync(conn, newOwner, ProjectId, ct)))
            Assert.Equal(NewOwnerId, detail.RootElement.GetProperty("responsibleUserId").GetUInt64());
        await AssertOutOfScopeAsync(() => files.ListAsync(Context(oldOwner), ProjectId, ct));
        Assert.Equal(0UL, Total(await files.ListAsync(Context(newOwner), ProjectId, ct)));
        Assert.Equal(historicalMembers, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_members WHERE project_id=@ProjectId",
            new { ProjectId }, cancellationToken: ct)));
        Assert.False(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM project_members WHERE project_id=@ProjectId AND user_id=@NewOwnerId)",
            new { ProjectId, NewOwnerId }, cancellationToken: ct)));

        var created = await projects.CreateAsync(conn, creator, CreateRequest(NewOwnerId), null, ct);
        var createdId = Id(created);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_members WHERE project_id=@CreatedId",
            new { CreatedId = createdId }, cancellationToken: ct)));
        await AssertOutOfScopeAsync(() => projects.DetailAsync(conn, creator, createdId, ct));
        await projects.DetailAsync(conn, newOwner, createdId, ct);
    }

    private static readonly string SeedSql = """
        UPDATE users SET must_change_password=0 WHERE id=1;
        INSERT INTO suppliers(id,name,status,created_by,created_at,updated_at)
        VALUES
          (8001,'负责人测试供应商','ACTIVE',1,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (8002,'其他供应商','ACTIVE',1,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
        VALUES(7001,NULL,'负责人测试课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO roles(id,name,description,is_built_in,status)
        VALUES
          (9001,'负责人范围用户','项目列表、创建和更新',0,'ACTIVE'),
          (9002,'负责人全局查看','项目全局查看',0,'ACTIVE');
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
        INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
        VALUES
          (6001,'ROBOT_VENDOR','负责人测试厂商',NULL,1,'ACTIVE'),
          (6002,'ROBOT_MODEL','负责人测试型号',6001,1,'ACTIVE'),
          (6003,'PRIORITY','负责人测试优先级',NULL,1,'ACTIVE');
        INSERT INTO projects
          (id,name,description,supplier_id,status,created_by,machine_model,robot_vendor_id,robot_model_id,
           responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
        VALUES
          (10001,'负责人范围项目','范围测试',8001,'DRAFT',9101,'M1',6001,6002,9103,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10002,'无负责人历史项目','空负责人测试',8001,'DRAFT',9101,'M1',6001,6002,NULL,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
          (10003,'无列表权限负责人项目','权限门禁测试',8001,'DRAFT',9101,'M1',6001,6002,9106,7001,6003,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO project_work_orders(project_id,work_order_no,sort_no)
        VALUES(10001,'WO-OWNER',0),(10002,'WO-OWNERLESS',0),(10003,'WO-NO-LIST',0);
        INSERT INTO project_members(project_id,user_id,created_by)
        VALUES(10001,9101,9101),(10001,9102,9101),(10002,9102,9101);
        """;

    private static ProjectUpsertRequest UpdateRequest(ulong responsibleUserId) => new()
    {
        Name = "负责人范围项目",
        Description = "范围测试",
        SupplierId = 8_001,
        WorkOrderNos = ["WO-OWNER"],
        MachineModel = "M1",
        RobotVendorId = 6_001,
        RobotModelId = 6_002,
        ResponsibleUserId = responsibleUserId,
        PriorityId = 6_003,
        ExpectedCompletionDate = "2026-12-31",
    };

    private static ProjectUpsertRequest CreateRequest(ulong responsibleUserId) => new()
    {
        Name = "创建后仅负责人可见",
        Description = "不写项目成员",
        SupplierId = 8_001,
        WorkOrderNos = ["WO-CREATED-OWNER"],
        MachineModel = "M2",
        RobotVendorId = 6_001,
        RobotModelId = 6_002,
        ResponsibleUserId = responsibleUserId,
        PriorityId = 6_003,
        ExpectedCompletionDate = "2027-01-31",
    };

    private static FileService FileService(OwnerAccessDatabase database, AuditService audit)
    {
        var identity = new IdentityService(
            database.Database,
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
        JsonDocument.Parse(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

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
            var databaseName = $"yf_owner_access_{Guid.NewGuid():N}";
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
