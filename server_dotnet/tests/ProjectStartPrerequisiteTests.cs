using System.Globalization;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>Creating a project starts it, so create and restart share the supplier prerequisites of START.</summary>
public sealed class ProjectStartPrerequisiteTests
{
    [Fact(Timeout = 90_000)]
    public async Task CreateAndRestartRequireAnActiveSupplierWithAnActiveUploader()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync(
            "project_start_supplier_user", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(9701,'项目所属厂商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9702,'其他厂商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO supplier_contacts(id,supplier_id,name,email,status) SELECT id+10000000,id,'Robot 负责人',CONCAT('contact',id,'@example.test'),'ACTIVE' FROM suppliers WHERE id NOT IN (SELECT supplier_id FROM supplier_contacts);
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(9703,NULL,'项目负责人课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9704,'start-owner','unused','项目负责人','start-owner@example.test','INTERNAL',NULL,9703,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9704,2);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(9705,9701,'START-PART','开始条件测试型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(9706,'PRIORITY','开始条件测试优先级',NULL,1,'ACTIVE'),
                  (1009706,'ROBOT_TYPE','开始条件测试优先级-Robot类型',NULL,1,'ACTIVE');
            """, ct);

        var actor = new CurrentUser(9704, "start-owner", UserTypes.Internal, null);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, database.Options);
        await using var conn = await database.Database.OpenAsync(ct);

        // No supplier account at all, one of another supplier, or a disabled one: nothing is created.
        await AssertCreateRejectedAsync(conn, projects, actor, ProjectService.SupplierWithoutActiveUserStartMessage, ct);
        await SqlAsync(conn, """
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9710,'other-supplier-user','unused','其他厂商用户','other-supplier@example.test','SUPPLIER',9702,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9711,'disabled-project-supplier','unused','停用厂商用户','disabled-supplier@example.test','SUPPLIER',9701,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, ct);
        await AssertCreateRejectedAsync(conn, projects, actor, ProjectService.SupplierWithoutActiveUserStartMessage, ct);

        // An active account that cannot upload files does not qualify (no role, a role without upload, a disabled role).
        await SqlAsync(conn, """
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9712,'active-project-supplier','unused','启用厂商用户','active-supplier@example.test','SUPPLIER',9701,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, ct);
        await AssertCreateRejectedAsync(conn, projects, actor, ProjectService.SupplierWithoutUploaderStartMessage, ct);
        await SqlAsync(conn, """
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at)
            VALUES(9720,'无上传权限的厂商角色',0,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9720,id FROM permissions WHERE code IN ('dashboard','project:list','file:download');
            INSERT INTO user_roles(user_id,role_id) VALUES(9712,9720);
            """, ct);
        await AssertCreateRejectedAsync(conn, projects, actor, ProjectService.SupplierWithoutUploaderStartMessage, ct);
        await SqlAsync(conn, "UPDATE roles SET status='DISABLED' WHERE id=4; UPDATE user_roles SET role_id=4 WHERE user_id=9712", ct);
        await AssertCreateRejectedAsync(conn, projects, actor, ProjectService.SupplierWithoutUploaderStartMessage, ct);
        await SqlAsync(conn, "UPDATE roles SET status='ACTIVE' WHERE id=4", ct);
        // A disabled supplier cannot be selected at all.
        await SqlAsync(conn, "UPDATE suppliers SET status='DISABLED' WHERE id=9701", ct);
        var disabled = await Assert.ThrowsAsync<ApiException>(() =>
            projects.CreateAsync(conn, actor, Request("被停用厂商的项目"), null, ct));
        Assert.Equal((400, "供应商已被禁用"), (disabled.Status, disabled.Message));
        await SqlAsync(conn, "UPDATE suppliers SET status='ACTIVE' WHERE id=9701", ct);

        var first = await projects.CreateAsync(conn, actor, Request("开始条件项目"), null, ct);
        var second = await projects.CreateAsync(conn, actor, Request("终止重启项目"), null, ct);
        Assert.Equal(ProjectStatuses.InProgress, first.Status);
        Assert.Equal(ProjectStatuses.InProgress, second.Status);

        // Terminating needs no supplier account; restarting does.
        await SqlAsync(conn,
            "DELETE ur FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE u.user_type='SUPPLIER'; DELETE FROM users WHERE user_type='SUPPLIER'", ct);
        var terminated = await SetStatusAsync(projects, conn, actor, second.Id, ProjectStatuses.Terminated, ct);
        Assert.Equal(ProjectStatuses.Terminated, terminated.Status);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_TERMINATE' AND target_type='project' AND target_id=@TargetId",
            new { TargetId = second.Id.ToString(CultureInfo.InvariantCulture) }, cancellationToken: ct)));
        await AssertRestartRejectedWithoutSideEffectsAsync(conn, projects, actor, second.Id,
            ProjectService.SupplierWithoutActiveUserStartMessage, ct);
        await SqlAsync(conn, """
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9713,'disabled-restart-supplier','unused','停用重启用户','disabled-restart@example.test','SUPPLIER',9701,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, ct);
        await AssertRestartRejectedWithoutSideEffectsAsync(conn, projects, actor, second.Id,
            ProjectService.SupplierWithoutActiveUserStartMessage, ct);
        await SqlAsync(conn, """
            DELETE FROM users WHERE id=9713;
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9714,'active-restart-supplier','unused','启用重启用户','active-restart@example.test','SUPPLIER',9701,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9714,4);
            UPDATE suppliers SET status='DISABLED' WHERE id=9701;
            """, ct);
        await AssertRestartRejectedWithoutSideEffectsAsync(conn, projects, actor, second.Id,
            ProjectService.SupplierDisabledStartMessage, ct);
        await SqlAsync(conn, "UPDATE suppliers SET status='ACTIVE' WHERE id=9701", ct);

        // Material returned in an earlier round stays on the project but is not this round's material.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,9714,'S2C','old.txt','round-old.txt','txt',1,'files/round-old.txt','AVAILABLE')
            """, new { ProjectId = second.Id }, cancellationToken: ct));
        var oldFileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: ct));
        var restarted = await SetStatusAsync(projects, conn, actor, second.Id, ProjectStatuses.InProgress, ct);
        Assert.Equal(ProjectStatuses.InProgress, restarted.Status);
        Assert.Equal(MaterialReviewStatuses.NoMaterial, restarted.MaterialReviewStatus);
        Assert.Null(restarted.LatestSupplierFileId);
        var oldRound = await Assert.ThrowsAsync<ApiException>(() => projects.ApproveMaterialsAsync(conn, actor, second.Id,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = oldFileId }, null, ct));
        Assert.Equal(409, oldRound.Status);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,9714,'S2C','new.txt','round-new.txt','txt',1,'files/round-new.txt','AVAILABLE')
            """, new { ProjectId = second.Id }, cancellationToken: ct));
        var newFileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: ct));
        var detail = await projects.DetailAsync(conn, actor, second.Id, ct);
        Assert.Equal(MaterialReviewStatuses.Pending, detail.MaterialReviewStatus);
        Assert.Equal(newFileId, detail.LatestSupplierFileId);

        // The supplier cannot be disabled while a project waits for its material. The scope pools one
        // connection, so release it before the factory-based admin service runs.
        await SqlAsync(conn, "INSERT INTO role_permissions(role_id,permission_id) SELECT 2,id FROM permissions WHERE code='supplier:manage'", ct);
        await conn.CloseAsync();
        var suppliers = new Yf.Api.Modules.Admin.SupplierService(
            EfTestSupport.DbContextFactory(database.Options), new Yf.Api.Modules.Identity.PermissionService(), audit);
        var refused = await Assert.ThrowsAsync<ApiException>(() => suppliers.SetStatusAsync(
            actor, 9701, AccountStatuses.Disabled, ct));
        Assert.Equal((409, 40901), (refused.Status, refused.Code));
        Assert.Contains("2 个进行中的项目", refused.Message);
    }

    private static ProjectUpsertRequest Request(string name) => new()
    {
        Name = name,
        SupplierId = 9701,
        WorkOrderNos = ["WO-START-PREREQUISITE"],
        MachineModel = "开始条件测试机型",
        RobotPartId = 9705,
        PriorityId = 9706,
        RobotTypeId = 1009706,
        RobotOwnerContactIds = [10009701],
        ExpectedCompletionDate = "2026-12-31",
    };

    private static async Task AssertCreateRejectedAsync(
        MySqlConnection conn, ProjectService projects, CurrentUser actor, string expectedMessage, CancellationToken ct)
    {
        var before = await TotalsAsync(conn, ct);
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            projects.CreateAsync(conn, actor, Request("被拒绝的项目"), null, ct));
        Assert.Equal((409, 40901, expectedMessage), (error.Status, error.Code, error.Message));
        Assert.Equal(before, await TotalsAsync(conn, ct));
    }

    private static async Task AssertRestartRejectedWithoutSideEffectsAsync(
        MySqlConnection conn, ProjectService projects, CurrentUser actor, ulong projectId, string expectedMessage,
        CancellationToken ct)
    {
        var before = await SnapshotAsync(conn, projectId, ct);
        var error = await Assert.ThrowsAsync<ApiException>(() => SetStatusAsync(
            projects, conn, actor, projectId, ProjectStatuses.InProgress, ct));
        Assert.Equal((409, 40901, expectedMessage), (error.Status, error.Code, error.Message));
        Assert.Equal(before, await SnapshotAsync(conn, projectId, ct));
    }

    private static Task SqlAsync(MySqlConnection conn, string sql, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

    private static Task<ProjectResponse> SetStatusAsync(
        ProjectService projects, MySqlConnection conn, CurrentUser actor, ulong projectId, string status, CancellationToken ct) =>
        projects.SetStatusAsync(conn, actor, projectId, new ProjectStatusRequest { Status = status }, null, ct);

    private static Task<(long Projects, long Logs, long Audits, long Mail)> TotalsAsync(MySqlConnection conn, CancellationToken ct) =>
        conn.QuerySingleAsync<(long, long, long, long)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM projects),(SELECT COUNT(*) FROM project_status_logs),
                   (SELECT COUNT(*) FROM audit_logs),(SELECT COUNT(*) FROM email_outbox)
            """, cancellationToken: ct));

    private static Task<WorkflowSnapshot> SnapshotAsync(MySqlConnection conn, ulong projectId, CancellationToken ct) =>
        conn.QuerySingleAsync<WorkflowSnapshot>(new CommandDefinition("""
            SELECT p.status AS ProjectStatus,p.updated_at AS ProjectUpdatedAt,
                   (SELECT COUNT(*) FROM project_status_logs WHERE project_id=p.id) AS ProjectLogCount,
                   (SELECT COUNT(*) FROM audit_logs) AS AuditCount
            FROM projects p
            WHERE p.id=@ProjectId
            """, new { ProjectId = projectId }, cancellationToken: ct));

    private sealed record WorkflowSnapshot
    {
        public string ProjectStatus { get; init; } = string.Empty;
        public DateTime ProjectUpdatedAt { get; init; }
        public long ProjectLogCount { get; init; }
        public long AuditCount { get; init; }
    }
}
