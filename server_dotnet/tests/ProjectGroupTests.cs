using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectGroupTests
{
    [Fact(Timeout = 90_000)]
    public async Task SubprojectsInheritMainDataAndLastConfirmationCompletesMainProject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_groups", ct);
        await database.InitializeBusinessFixtureAsync(ct);
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
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(8402,8101,'GROUP-PART','主项目型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8403,'PRIORITY','主项目优先级',NULL,1,'ACTIVE');
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
            RobotPartId = 8402,
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
            var internalWithdrawal = await Assert.ThrowsAsync<ApiException>(() => projects.WithdrawAsync(
                conn, actor, children[0].Id,
                new ProjectDecisionRequest { ExpectedSubmissionId = submissionId }, null, ct));
            Assert.Equal(403, internalWithdrawal.Status);
            Assert.Equal(ProjectStatuses.PendingConfirmation, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT status FROM projects WHERE id=@ProjectId", new { ProjectId = children[0].Id })));
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
        await database.InitializeBusinessFixtureAsync(ct);
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
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(8505,8501,'FREEZE-PART','冻结型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8506,'PRIORITY','冻结优先级',NULL,1,'ACTIVE');
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
            RobotPartId = 8505,
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
            RobotPartId = 8505,
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
        Assert.Equal(8503UL, rows[0].ResponsibleUserId);
        Assert.Equal(8502UL, rows[0].SectionId);
        Assert.Equal(new DateTime(2020, 1, 1), rows[0].UpdatedAt);
        Assert.Equal("新机型", rows[1].MachineModel);
        Assert.Equal(new DateTime(2027, 1, 1), rows[1].ExpectedCompletionDate);
        Assert.Equal("WO-NEW", rows[1].WorkOrders);
        Assert.Equal(8503UL, rows[1].ResponsibleUserId);
        Assert.Equal(8502UL, rows[1].SectionId);

        var auditCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action IN ('PROJECT_GROUP_UPDATE','PROJECT_UPDATE')",
            cancellationToken: ct));
        var activeUpdatedAt = rows[1].UpdatedAt;
        await groups.UpdateAsync(conn, actor, groupId, new ProjectUpsertRequest
        {
            Name = "冻结测试主项目-更新",
            SupplierId = 8501,
            WorkOrderNos = ["WO-NEW"],
            MachineModel = "新机型",
            RobotPartId = 8505,
            PriorityId = 8506,
            ExpectedCompletionDate = "2027-01-01",
        }, null, ct);
        Assert.Equal(auditCount, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action IN ('PROJECT_GROUP_UPDATE','PROJECT_UPDATE')",
            cancellationToken: ct)));
        Assert.Equal(activeUpdatedAt, await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT updated_at FROM projects WHERE id=@Id", new { Id = childIds[1] }, cancellationToken: ct)));
    }

    [Fact(Timeout = 90_000)]
    public async Task EmptyMainProjectCanBeDeletedWithAuditAndDependentRowsCascaded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_group_delete", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8701,'删除主项目供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8702,'group-delete-owner','unused','删除主项目负责人','group-delete@example.test','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8702,1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id,created_at,updated_at)
            VALUES(8703,'待删除空主项目',8701,'DRAFT',8702,8702,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no,created_at)
            VALUES(8703,'WO-DELETE-GROUP',0,UTC_TIMESTAMP(3));
            INSERT INTO project_group_status_logs(project_group_id,from_status,to_status,action,operator_id,created_at)
            VALUES(8703,NULL,'DRAFT','CREATE',8702,UTC_TIMESTAMP(3));
            """, ct);

        var actor = new CurrentUser(8702, "group-delete-owner", "INTERNAL", null);
        var audit = new AuditService([]);
        var groups = new ProjectGroupService(audit, new ProjectGroupStatusService(audit));
        await using var conn = await database.Database.OpenAsync(ct);

        await groups.DeleteAsync(conn, actor, 8703, null, ct);

        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_groups WHERE id=8703"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_group_work_orders WHERE project_group_id=8703"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_group_status_logs WHERE project_group_id=8703"));
        var detail = await conn.QuerySingleAsync<string>(new CommandDefinition("""
            SELECT detail FROM audit_logs
            WHERE action='PROJECT_GROUP_DELETE' AND target_type='project_group' AND target_id='8703'
            """, cancellationToken: ct));
        Assert.Contains("待删除空主项目", detail, StringComparison.Ordinal);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(ProjectStatuses.Terminated, ProjectStatuses.Terminated)]
    [InlineData(ProjectStatuses.Draft, ProjectStatuses.InProgress)]
    public async Task DeletingTheLastOpenSubprojectNeverAutoCompletesTheMainProject(string deletedStatus, string groupStatusBefore)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_group_delete_complete", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8801,'删除完成测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(8802,NULL,'删除完成测试课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8803,'delete-complete-owner','unused','删除完成负责人','delete-complete@example.test','INTERNAL',8802,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8803,1);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(8804,8801,'DELETE-COMPLETE-PART','删除完成型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8805,'PRIORITY','删除完成优先级',NULL,1,'ACTIVE');
            """, ct);

        var actor = new CurrentUser(8803, "delete-complete-owner", "INTERNAL", null);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        using var created = Json(await groups.CreateAsync(conn, actor, new ProjectUpsertRequest
        {
            Name = "删除不自动完成主项目",
            SupplierId = 8801,
            WorkOrderNos = ["WO-DELETE-COMPLETE"],
            MachineModel = "删除完成机型",
            RobotPartId = 8804,
            PriorityId = 8805,
            ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["已验收子项目", "待删除子项目"],
        }, null, ct));
        var groupId = created.RootElement.GetProperty("id").GetUInt64();
        var ids = (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId ORDER BY id", new { GroupId = groupId },
            cancellationToken: ct))).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET status='COMPLETED' WHERE id=@Completed; UPDATE projects SET status=@Deleted WHERE id=@Target",
            new { Completed = ids[0], Target = ids[1], Deleted = deletedStatus }, cancellationToken: ct));
        await using (var sync = await AppDb.BeginTransactionAsync(conn, ct))
        {
            await groupStatus.RecalculateAsync(conn, sync, groupId, actor.Id, null, ct);
            await sync.CommitAsync(ct);
        }
        Assert.Equal(groupStatusBefore, await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM project_groups WHERE id=@GroupId", new { GroupId = groupId }, cancellationToken: ct)));

        await projects.DeleteAsync(conn, actor, ids[1], null, ct);

        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE project_group_id=@GroupId", new { GroupId = groupId }, cancellationToken: ct)));
        var state = await conn.QuerySingleAsync<GroupState>(new CommandDefinition(
            "SELECT status AS Status,completed_at AS CompletedAt FROM project_groups WHERE id=@GroupId",
            new { GroupId = groupId }, cancellationToken: ct));
        Assert.Equal(groupStatusBefore, state.Status);
        Assert.Null(state.CompletedAt);
        Assert.Equal(0L, await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_group_status_logs WHERE project_group_id=@GroupId AND action='AUTO_COMPLETE'",
            new { GroupId = groupId }, cancellationToken: ct)));
    }

    [Fact(Timeout = 90_000)]
    public async Task MainProjectChangesNotifyEverySubprojectAndTransferAlsoReachesTheFormerOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_group_realtime", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8901,'实时主项目供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(8902,NULL,'实时主项目课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8903,'realtime-group-owner','unused','实时原负责人','realtime-old@example.test','INTERNAL',8902,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8907,'realtime-group-new','unused','实时新负责人','realtime-new@example.test','INTERNAL',8902,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8903,1),(8907,1);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(8904,8901,'REALTIME-GROUP-PART','实时型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8905,'PRIORITY','实时优先级',NULL,1,'ACTIVE');
            """, ct);

        var actor = new CurrentUser(8903, "realtime-group-owner", "INTERNAL", null);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var publisher = new RecordingPublisher();
        var groups = new ProjectGroupService(audit, groupStatus, publisher);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus, publisher);
        await using var conn = await database.Database.OpenAsync(ct);
        var request = new ProjectUpsertRequest
        {
            Name = "实时主项目",
            SupplierId = 8901,
            WorkOrderNos = ["WO-REALTIME"],
            MachineModel = "实时机型",
            RobotPartId = 8904,
            PriorityId = 8905,
            ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["实时子项目一", "实时子项目二"],
        };
        using var created = Json(await groups.CreateAsync(conn, actor, request, null, ct));
        var groupId = created.RootElement.GetProperty("id").GetUInt64();
        var ids = (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId ORDER BY id", new { GroupId = groupId },
            cancellationToken: ct))).ToArray();

        // Name and description alone write no subproject activity; every subproject still hears about it.
        publisher.Calls.Clear();
        await groups.UpdateAsync(conn, actor, groupId, new ProjectUpsertRequest
        {
            Name = "实时主项目-改名",
            Description = "新的说明",
            SupplierId = request.SupplierId,
            WorkOrderNos = request.WorkOrderNos,
            MachineModel = request.MachineModel,
            RobotPartId = request.RobotPartId,
            PriorityId = request.PriorityId,
            ExpectedCompletionDate = request.ExpectedCompletionDate,
        }, null, ct);
        Assert.Equal(ids.Select(id => (id, RealtimeChangeKinds.Project)).ToArray(),
            publisher.Calls.Select(call => (call.ProjectId, call.Kind)).ToArray());
        Assert.All(publisher.Calls, call => Assert.Null(call.Audience));

        // A transfer reaches the new owner and, from the pre-change snapshot, the former one.
        publisher.Calls.Clear();
        await groups.TransferAsync(conn, actor, groupId, new ProjectGroupTransferRequest { ResponsibleUserId = 8907 }, null, ct);
        Assert.Equal(ids, publisher.Calls.Select(call => call.ProjectId).ToArray());
        Assert.All(publisher.Calls, call =>
        {
            Assert.Equal(RealtimeChangeKinds.Project, call.Kind);
            var audience = Assert.IsType<ProjectRealtimeAudience>(call.Audience);
            Assert.Equal(8907UL, audience.ResponsibleUserId);
            Assert.Equal([8903UL], audience.FormerInternalUserIds!.ToArray());
            Assert.Equal(8901UL, audience.SupplierId);
            Assert.Contains(8903UL, audience.ViewAllUserIds);
        });

        // Deleting a subproject changes the main project every remaining sibling shows.
        publisher.Calls.Clear();
        var newOwner = new CurrentUser(8907, "realtime-group-new", "INTERNAL", null);
        await projects.DeleteAsync(conn, newOwner, ids[1], null, ct);
        Assert.Equal([ids[1], ids[0]], publisher.Calls.Select(call => call.ProjectId).ToArray());
        Assert.All(publisher.Calls, call => Assert.Equal(RealtimeChangeKinds.Project, call.Kind));
    }

    private sealed class RecordingPublisher : IProjectRealtimePublisher
    {
        internal List<(ulong ProjectId, string Kind, ProjectRealtimeAudience? Audience)> Calls { get; } = [];
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        { Calls.Add((projectId, kind, null)); return Task.CompletedTask; }
        public Task PublishAsync(ProjectRealtimeAudience audience, string kind, CancellationToken ct = default)
        { Calls.Add((audience.ProjectId, kind, audience)); return Task.CompletedTask; }
    }

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));
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
