using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectGroupTests
{
    [Fact(Timeout = 60_000)]
    public async Task MigrationWrapsExistingProjectsWithoutChangingTheirBusinessTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_group_backfill", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
            VALUES(1,'migration-owner','unused','迁移负责人','migration@example.test','INTERNAL','ACTIVE',0);
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8051,'迁移供应商','ACTIVE','2026-01-01 00:00:00.000','2026-01-01 00:00:00.000');
            INSERT INTO projects(id,name,supplier_id,status,created_by,created_at,updated_at)
            VALUES(8052,'迁移前项目',8051,'IN_PROGRESS',1,'2026-01-02 03:04:05','2026-01-03 04:05:06');
            """, ct);

        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await using var conn = await database.Database.OpenAsync(ct);
        Assert.Equal("2026-01-03 04:05:06", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT DATE_FORMAT(updated_at,'%Y-%m-%d %H:%i:%s') FROM projects WHERE id=8052", cancellationToken: ct)));
        Assert.Equal("迁移前项目", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT g.name FROM projects p JOIN project_groups g ON g.id=p.project_group_id WHERE p.id=8052", cancellationToken: ct)));
        Assert.Equal("NO", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='projects' AND column_name='project_group_id'",
            cancellationToken: ct)));
    }

    [Fact(Timeout = 90_000)]
    public async Task SubprojectsInheritMainDataAndLastConfirmationCompletesMainProject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_groups", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await using (var schema = await database.Database.OpenAsync(ct))
            Assert.Equal("NO", await schema.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='projects' AND column_name='project_group_id'",
                cancellationToken: ct)));
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8101,'主项目供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(8201,NULL,'主项目课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8301,'group-owner','unused','主项目负责人','owner@example.test','INTERNAL',NULL,8201,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8302,'group-supplier','unused','供应商提交人','supplier@example.test','SUPPLIER',8101,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8301,1),(8302,4);
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status) VALUES
              (8401,'ROBOT_VENDOR','主项目厂商',NULL,1,'ACTIVE'),
              (8402,'ROBOT_MODEL','主项目型号',8401,1,'ACTIVE'),
              (8403,'PRIORITY','主项目优先级',NULL,1,'ACTIVE');
            """, ct);

        var actor = new CurrentUser(8301, "group-owner", "INTERNAL", null);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        using var created = Json(await groups.CreateAsync(conn, actor, new ProjectUpsertRequest
        {
            Name = "集成主项目",
            Description = "公共资料",
            SupplierId = 8101,
            WorkOrderNos = ["WO-GROUP-1", "WO-GROUP-2"],
            MachineModel = "机型-G",
            RobotVendorId = 8401,
            RobotModelId = 8402,
            ResponsibleUserId = 8301,
            PriorityId = 8403,
            ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["子项目-A", "子项目-B"],
        }, null, ct));
        var groupId = created.RootElement.GetProperty("id").GetUInt64();
        var children = (await conn.QueryAsync<Child>(new CommandDefinition(
            """
            SELECT id AS Id,project_group_id AS ProjectGroupId,supplier_id AS SupplierId,
                   machine_model AS MachineModel,responsible_user_id AS ResponsibleUserId,status AS Status
            FROM projects WHERE project_group_id=@GroupId ORDER BY id
            """, new { GroupId = groupId }, cancellationToken: ct))).ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, child =>
        {
            Assert.Equal(groupId, child.ProjectGroupId);
            Assert.Equal(8101UL, child.SupplierId);
            Assert.Equal("机型-G", child.MachineModel);
            Assert.Equal(8301UL, child.ResponsibleUserId);
            Assert.Equal(ProjectStatuses.Draft, child.Status);
        });
        using (var dashboard = Json(await new DashboardService().SummaryAsync(conn, actor, ct)))
        {
            Assert.Equal(1, dashboard.RootElement.GetProperty("projectCount").GetInt32());
            Assert.Equal(0, dashboard.RootElement.GetProperty("activeProjectCount").GetInt32());
        }

        foreach (var child in children)
            await projects.SetStatusAsync(conn, actor, child.Id,
                new ProjectStatusRequest { Status = ProjectStatuses.InProgress }, null, ct);
        Assert.Equal(ProjectStatuses.InProgress, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM project_groups WHERE id=@GroupId", new { GroupId = groupId })));

        var messages = new MessageService(audit, new AppOptions());
        await messages.CreateAsync(conn, actor, children[0].Id, new MessageCreateRequest { Content = "A 独立留言" }, null, ct);
        await messages.CreateAsync(conn, actor, children[1].Id, new MessageCreateRequest { Content = "B 独立留言" }, null, ct);
        using (var firstMessages = Json(await messages.ListAsync(conn, actor, children[0].Id, 1, 20, null, null, ct)))
        using (var secondMessages = Json(await messages.ListAsync(conn, actor, children[1].Id, 1, 20, null, null, ct)))
        {
            Assert.Equal("A 独立留言", Assert.Single(firstMessages.RootElement.GetProperty("list").EnumerateArray()).GetProperty("content").GetString());
            Assert.Equal("B 独立留言", Assert.Single(secondMessages.RootElement.GetProperty("list").EnumerateArray()).GetProperty("content").GetString());
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@First,@ActorId,'C2S','a.txt','group-a.txt','txt',1,'files/group-a.txt','AVAILABLE'),
                  (@Second,@ActorId,'C2S','b.txt','group-b.txt','txt',1,'files/group-b.txt','AVAILABLE')
            """, new { First = children[0].Id, Second = children[1].Id, ActorId = actor.Id }));
        Assert.Equal(["a.txt"], (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT original_name FROM files WHERE project_id=@ProjectId", new { ProjectId = children[0].Id }))).ToArray());
        Assert.Equal(["b.txt"], (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT original_name FROM files WHERE project_id=@ProjectId", new { ProjectId = children[1].Id }))).ToArray());

        var internalSubmission = await Assert.ThrowsAsync<ApiException>(() => projects.SubmitAsync(
            conn, actor, children[0].Id, new ProjectSubmitRequest(), null, ct));
        Assert.Equal(403, internalSubmission.Status);
        var supplier = new CurrentUser(8302, "group-supplier", "SUPPLIER", 8101);
        using (var submitted = Json(await projects.SubmitAsync(
                   conn, supplier, children[0].Id, new ProjectSubmitRequest(), null, ct)))
        {
            var submissionId = submitted.RootElement.GetProperty("latestSubmissionId").GetUInt64();
            Assert.Equal(ProjectStatuses.PendingConfirmation,
                submitted.RootElement.GetProperty("status").GetString());
            await projects.WithdrawAsync(conn, supplier, children[0].Id,
                new ProjectDecisionRequest { ExpectedSubmissionId = submissionId }, null, ct);
        }

        var submissions = new List<(ulong ProjectId, ulong SubmissionId)>();
        foreach (var child in children)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET status='PENDING_CONFIRMATION',confirm_side='COMPANY' WHERE id=@ProjectId",
                new { ProjectId = child.Id }));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,created_at)
                VALUES(@ProjectId,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',@ActorId,'COMPANY',UTC_TIMESTAMP(3))
                """, new { ProjectId = child.Id, ActorId = supplier.Id }));
            submissions.Add((child.Id, await conn.ExecuteScalarAsync<ulong>("SELECT LAST_INSERT_ID()")));
        }
        await using (var sync = await AppDb.BeginTransactionAsync(conn, ct))
        {
            await groupStatus.RecalculateAsync(conn, sync, groupId, actor.Id, null, ct);
            await sync.CommitAsync(ct);
        }

        using (var supplierDashboard = Json(await new DashboardService().SummaryAsync(conn, supplier, ct)))
            Assert.Equal(2, supplierDashboard.RootElement.GetProperty("pendingConfirmations").GetInt32());
        using (var supplierPending = Json(await new DashboardService().PendingProjectsAsync(conn, supplier, 1, 20, ct)))
            Assert.Equal(2, supplierPending.RootElement.GetProperty("total").GetInt32());

        await projects.ConfirmAsync(conn, actor, submissions[0].ProjectId,
            new ProjectDecisionRequest { ExpectedSubmissionId = submissions[0].SubmissionId }, null, ct);
        Assert.Equal(ProjectStatuses.InProgress, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM project_groups WHERE id=@GroupId", new { GroupId = groupId })));

        await projects.ConfirmAsync(conn, actor, submissions[1].ProjectId,
            new ProjectDecisionRequest { ExpectedSubmissionId = submissions[1].SubmissionId }, null, ct);
        var final = await conn.QuerySingleAsync<GroupState>(new CommandDefinition(
            "SELECT status AS Status,completed_at AS CompletedAt FROM project_groups WHERE id=@GroupId",
            new { GroupId = groupId }));
        Assert.Equal(ProjectStatuses.Completed, final.Status);
        Assert.NotNull(final.CompletedAt);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_group_status_logs WHERE project_group_id=@GroupId AND action='AUTO_COMPLETE'",
            new { GroupId = groupId })));
    }

    [Fact(Timeout = 90_000)]
    public async Task UpdatingMainProjectKeepsCompletedSubprojectMetadataAndWorkOrdersFrozen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_group_freeze", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8501,'冻结测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(8502,NULL,'冻结测试课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8508,NULL,'新负责人课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8503,'freeze-owner','unused','冻结测试负责人','freeze@example.test','INTERNAL',8502,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8507,'new-owner','unused','新负责人','new-owner@example.test','INTERNAL',8508,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8503,1),(8507,1);
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status) VALUES
              (8504,'ROBOT_VENDOR','冻结厂商',NULL,1,'ACTIVE'),
              (8505,'ROBOT_MODEL','冻结型号',8504,1,'ACTIVE'),
              (8506,'PRIORITY','冻结优先级',NULL,1,'ACTIVE');
            """, ct);

        var actor = new CurrentUser(8503, "freeze-owner", "INTERNAL", null);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        using var created = Json(await groups.CreateAsync(conn, actor, new ProjectUpsertRequest
        {
            Name = "冻结测试主项目",
            SupplierId = 8501,
            WorkOrderNos = ["WO-OLD"],
            MachineModel = "旧机型",
            RobotVendorId = 8504,
            RobotModelId = 8505,
            ResponsibleUserId = 8503,
            PriorityId = 8506,
            ExpectedCompletionDate = "2026-12-01",
            SubprojectNames = ["已完成子项目", "活动子项目"],
        }, null, ct));
        var groupId = created.RootElement.GetProperty("id").GetUInt64();
        var childIds = (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId ORDER BY id",
            new { GroupId = groupId }, cancellationToken: ct))).ToArray();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE projects SET status='COMPLETED',updated_at='2020-01-01 00:00:00.000' WHERE id=@CompletedId;
            DELETE FROM project_work_orders WHERE project_id=@CompletedId;
            INSERT INTO project_work_orders(project_id,work_order_no,sort_no) VALUES(@CompletedId,'WO-FROZEN',0);
            """, new { CompletedId = childIds[0] }, cancellationToken: ct));
        await using (var sync = await AppDb.BeginTransactionAsync(conn, ct))
        {
            await groupStatus.RecalculateAsync(conn, sync, groupId, actor.Id, childIds[0], ct);
            await sync.CommitAsync(ct);
        }

        await groups.UpdateAsync(conn, actor, groupId, new ProjectUpsertRequest
        {
            Name = "冻结测试主项目-更新",
            SupplierId = 8501,
            WorkOrderNos = ["WO-NEW"],
            MachineModel = "新机型",
            RobotVendorId = 8504,
            RobotModelId = 8505,
            ResponsibleUserId = 8507,
            PriorityId = 8506,
            ExpectedCompletionDate = "2027-01-01",
        }, null, ct);

        var rows = (await conn.QueryAsync<FrozenChild>(new CommandDefinition("""
            SELECT p.id AS Id,p.machine_model AS MachineModel,p.expected_completion_date AS ExpectedCompletionDate,
                   p.responsible_user_id AS ResponsibleUserId,p.section_id AS SectionId,
                   p.updated_at AS UpdatedAt,
                   (SELECT GROUP_CONCAT(pwo.work_order_no ORDER BY pwo.sort_no,pwo.id)
                    FROM project_work_orders pwo WHERE pwo.project_id=p.id) AS WorkOrders
            FROM projects p WHERE p.id IN @Ids ORDER BY p.id
            """, new { Ids = childIds }, cancellationToken: ct))).ToArray();
        Assert.Equal("旧机型", rows[0].MachineModel);
        Assert.Equal(new DateTime(2026, 12, 1), rows[0].ExpectedCompletionDate);
        Assert.Equal("WO-FROZEN", rows[0].WorkOrders);
        Assert.Equal(8507UL, rows[0].ResponsibleUserId);
        Assert.Equal(8508UL, rows[0].SectionId);
        Assert.True(rows[0].UpdatedAt > new DateTime(2020, 1, 1));
        Assert.Equal("新机型", rows[1].MachineModel);
        Assert.Equal(new DateTime(2027, 1, 1), rows[1].ExpectedCompletionDate);
        Assert.Equal("WO-NEW", rows[1].WorkOrders);
        Assert.Equal(8507UL, rows[1].ResponsibleUserId);
        Assert.Equal(8508UL, rows[1].SectionId);
    }

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
    private sealed class Child
    {
        public ulong Id { get; init; }
        public ulong ProjectGroupId { get; init; }
        public ulong SupplierId { get; init; }
        public string MachineModel { get; init; } = string.Empty;
        public ulong ResponsibleUserId { get; init; }
        public string Status { get; init; } = string.Empty;
    }
    private sealed class GroupState { public string Status { get; init; } = string.Empty; public DateTime? CompletedAt { get; init; } }
    private sealed class FrozenChild
    {
        public ulong Id { get; init; }
        public string MachineModel { get; init; } = string.Empty;
        public DateTime ExpectedCompletionDate { get; init; }
        public ulong ResponsibleUserId { get; init; }
        public ulong SectionId { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string WorkOrders { get; init; } = string.Empty;
    }
}
