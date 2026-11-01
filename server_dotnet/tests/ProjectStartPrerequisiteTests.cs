using System.Globalization;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectStartPrerequisiteTests
{
    private const string MissingSupplierUserMessage =
        "项目所属厂商尚未添加启用的用户，请先添加或启用厂商用户后再开始项目";

    [Fact(Timeout = 90_000)]
    public async Task StartAndRestartRequireAnActiveSupplierWithAnActiveUploader()
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
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, database.Options, groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        var group = await groups.CreateAsync(conn, actor, new ProjectUpsertRequest
        {
            Name = "厂商用户开始条件主项目",
            SupplierId = 9701,
            WorkOrderNos = ["WO-START-PREREQUISITE"],
            MachineModel = "开始条件测试机型",
            RobotPartId = 9705,
            PriorityId = 9706,
            RobotTypeId = 1009706,
            RobotOwnerContactIds = [10009701],
            ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["开始条件子项目", "终止重启子项目"],
        }, null, ct);
        var projectIds = (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId ORDER BY id",
            new { GroupId = group.Id }, cancellationToken: ct))).ToArray();
        Assert.Equal(2, projectIds.Length);

        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9710,'other-supplier-user','unused','其他厂商用户','other-supplier@example.test','SUPPLIER',9702,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9711,'disabled-project-supplier','unused','停用厂商用户','disabled-supplier@example.test','SUPPLIER',9701,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id=9711", cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9712,'active-project-supplier','unused','启用厂商用户','active-supplier@example.test','SUPPLIER',9701,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, cancellationToken: ct));
        // An active account that cannot upload files does not qualify (no role / inactive role).
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct,
            ProjectService.SupplierWithoutUploaderStartMessage);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at)
            VALUES(9720,'无上传权限的厂商角色',0,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9720,id FROM permissions WHERE code IN ('dashboard','project:list','file:download');
            INSERT INTO user_roles(user_id,role_id) VALUES(9712,9720);
            """, cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct,
            ProjectService.SupplierWithoutUploaderStartMessage);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE roles SET status='DISABLED' WHERE id=4; UPDATE user_roles SET role_id=4 WHERE user_id=9712",
            cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct,
            ProjectService.SupplierWithoutUploaderStartMessage);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE roles SET status='ACTIVE' WHERE id=4", cancellationToken: ct));
        // A disabled supplier company blocks start even with a qualifying account, with its own message.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE suppliers SET status='DISABLED' WHERE id=9701", cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[0], ct,
            ProjectService.SupplierDisabledStartMessage);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE suppliers SET status='ACTIVE' WHERE id=9701", cancellationToken: ct));
        Assert.Equal(ProjectStatuses.InProgress, (await SetStatusAsync(
            projects, conn, actor, projectIds[0], ProjectStatuses.InProgress, ct)).Status);
        Assert.Equal(ProjectStatuses.InProgress, (await SetStatusAsync(
            projects, conn, actor, projectIds[1], ProjectStatuses.InProgress, ct)).Status);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE ur FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE u.user_type='SUPPLIER'; DELETE FROM users WHERE user_type='SUPPLIER'",
            cancellationToken: ct));
        var terminated = await SetStatusAsync(
            projects, conn, actor, projectIds[1], ProjectStatuses.Terminated, ct);
        Assert.Equal(ProjectStatuses.Terminated, terminated.Status);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_status_logs WHERE project_id=@ProjectId AND action='TERMINATE'",
            new { ProjectId = projectIds[1] }, cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='PROJECT_TERMINATE' AND target_type='project' AND target_id=@TargetId",
            new { TargetId = projectIds[1].ToString(CultureInfo.InvariantCulture) }, cancellationToken: ct)));

        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[1], ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9713,'disabled-restart-supplier','unused','停用重启用户','disabled-restart@example.test','SUPPLIER',9701,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[1], ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id=9713", cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[1], ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9714,'active-restart-supplier','unused','启用重启用户','active-restart@example.test','SUPPLIER',9701,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9714,4);
            """, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE suppliers SET status='DISABLED' WHERE id=9701", cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[1], ct,
            ProjectService.SupplierDisabledStartMessage);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE suppliers SET status='ACTIVE' WHERE id=9701", cancellationToken: ct));

        // A terminated main project may be owned by a disabled account; restarting it would leave nobody to review.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9715,'disabled-owner','unused','已停用负责人','disabled-owner@example.test','INTERNAL',NULL,9703,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9715,2);
            UPDATE project_groups SET responsible_user_id=9715 WHERE id=@GroupId;
            UPDATE projects SET responsible_user_id=9715 WHERE project_group_id=@GroupId;
            """, new { GroupId = group.Id }, cancellationToken: ct));
        await AssertStartRejectedWithoutSideEffectsAsync(conn, projects, actor, projectIds[1], ct,
            ProjectService.ResponsibleUserUnavailableStartMessage);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE project_groups SET responsible_user_id=9704 WHERE id=@GroupId;
            UPDATE projects SET responsible_user_id=9704 WHERE project_group_id=@GroupId;
            """, new { GroupId = group.Id }, cancellationToken: ct));

        // Material returned in an earlier round stays on the subproject but is not this round's material.
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,9714,'S2C','old.txt','round-old.txt','txt',1,'files/round-old.txt','AVAILABLE')
            """, new { ProjectId = projectIds[1] }, cancellationToken: ct));
        var oldFileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: ct));
        var restarted = await SetStatusAsync(
            projects, conn, actor, projectIds[1], ProjectStatuses.InProgress, ct);
        Assert.Equal(ProjectStatuses.InProgress, restarted.Status);
        Assert.Equal(ProjectStatuses.InProgress, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM project_groups WHERE id=@GroupId",
            new { GroupId = group.Id }, cancellationToken: ct)));
        Assert.Equal(MaterialReviewStatuses.NoMaterial, restarted.MaterialReviewStatus);
        Assert.Null(restarted.LatestSupplierFileId);
        var oldRound = await Assert.ThrowsAsync<ApiException>(() => projects.ApproveMaterialsAsync(conn, actor, projectIds[1],
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = oldFileId }, null, ct));
        Assert.Equal(409, oldRound.Status);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,9714,'S2C','new.txt','round-new.txt','txt',1,'files/round-new.txt','AVAILABLE')
            """, new { ProjectId = projectIds[1] }, cancellationToken: ct));
        var newFileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: ct));
        var detail = await projects.DetailAsync(conn, actor, projectIds[1], ct);
        Assert.Equal(MaterialReviewStatuses.Pending, detail.MaterialReviewStatus);
        Assert.Equal(newFileId, detail.LatestSupplierFileId);

        // The supplier cannot be disabled while a subproject waits for its material. The scope pools one
        // connection, so release it before the factory-based admin service runs.
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO role_permissions(role_id,permission_id) SELECT 2,id FROM permissions WHERE code='supplier:manage'",
            cancellationToken: ct));
        await conn.CloseAsync();
        var suppliers = new Yf.Api.Modules.Admin.SupplierService(
            EfTestSupport.DbContextFactory(database.Options), new Yf.Api.Modules.Identity.PermissionService(), audit);
        var refused = await Assert.ThrowsAsync<ApiException>(() => suppliers.SetStatusAsync(
            actor, 9701, AccountStatuses.Disabled, ct));
        Assert.Equal((409, 40901), (refused.Status, refused.Code));
        Assert.Contains("2 个进行中的子项目", refused.Message);
    }

    private static async Task AssertStartRejectedWithoutSideEffectsAsync(
        MySqlConnection conn,
        ProjectService projects,
        CurrentUser actor,
        ulong projectId,
        CancellationToken ct,
        string expectedMessage = MissingSupplierUserMessage)
    {
        var before = await SnapshotAsync(conn, projectId, ct);

        var error = await Assert.ThrowsAsync<ApiException>(() => SetStatusAsync(
            projects, conn, actor, projectId, ProjectStatuses.InProgress, ct));

        Assert.Equal(409, error.Status);
        Assert.Equal(40901, error.Code);
        Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(before, await SnapshotAsync(conn, projectId, ct));
    }

    private static Task<ProjectResponse> SetStatusAsync(
        ProjectService projects,
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string status,
        CancellationToken ct) =>
        projects.SetStatusAsync(conn, actor, projectId, new ProjectStatusRequest { Status = status }, null, ct);

    private static Task<WorkflowSnapshot> SnapshotAsync(
        MySqlConnection conn,
        ulong projectId,
        CancellationToken ct) =>
        conn.QuerySingleAsync<WorkflowSnapshot>(new CommandDefinition("""
            SELECT p.status AS ProjectStatus,p.updated_at AS ProjectUpdatedAt,
                   g.status AS GroupStatus,g.updated_at AS GroupUpdatedAt,
                   (SELECT COUNT(*) FROM project_status_logs WHERE project_id=p.id) AS ProjectLogCount,
                   (SELECT COUNT(*) FROM project_group_status_logs WHERE project_group_id=g.id) AS GroupLogCount,
                   (SELECT COUNT(*) FROM audit_logs) AS AuditCount
            FROM projects p
            JOIN project_groups g ON g.id=p.project_group_id
            WHERE p.id=@ProjectId
            """, new { ProjectId = projectId }, cancellationToken: ct));

    private sealed record WorkflowSnapshot
    {
        public string ProjectStatus { get; init; } = string.Empty;
        public DateTime ProjectUpdatedAt { get; init; }
        public string GroupStatus { get; init; } = string.Empty;
        public DateTime GroupUpdatedAt { get; init; }
        public long ProjectLogCount { get; init; }
        public long GroupLogCount { get; init; }
        public long AuditCount { get; init; }
    }
}
