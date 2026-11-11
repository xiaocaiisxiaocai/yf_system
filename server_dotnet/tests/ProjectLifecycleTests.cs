using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>
/// Single-level projects: creating one starts it, every internal account sees every project, and only the responsible
/// user changes it. See 项目协作契约-2026-11-09.
/// </summary>
public sealed class ProjectLifecycleTests
{
    [Fact(Timeout = 90_000)]
    public async Task CreatingAProjectStartsItAndMaterialApprovalCompletesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_lifecycle", ct);
        var fixture = await SeedAsync(database, "生命周期", 8100, ct);
        await using var conn = fixture.Conn;
        var owner = fixture.Owner;
        var supplier = fixture.Supplier;
        var projects = fixture.Projects;

        var created = await projects.CreateAsync(conn, owner, fixture.Request("生命周期项目"), null, ct);
        Assert.Equal(ProjectStatuses.InProgress, created.Status);
        Assert.Equal(owner.Id, created.ResponsibleUserId);
        Assert.True(created.CanManage);
        Assert.Equal([fixture.ContactId], created.RobotOwnerContactIds.ToArray());
        Assert.Equal("NO_MATERIAL", created.MaterialReviewStatus);
        var projectId = created.Id;
        var log = await conn.QuerySingleAsync<(string? From, string To, string Action)>(new CommandDefinition(
            "SELECT from_status,to_status,action FROM project_status_logs WHERE project_id=@Id", new { Id = projectId },
            cancellationToken: ct));
        Assert.Equal(((string?)null, ProjectStatuses.InProgress, "START"), log);
        Assert.Equal(["PROJECT_CREATE", "PROJECT_START"], (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT action FROM audit_logs WHERE target_type='project' AND target_id=@Id ORDER BY id",
            new { Id = projectId.ToString() }, cancellationToken: ct))).ToArray());
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=@Id AND event_type='PROJECT_STARTED'",
            new { Id = projectId }, cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_robot_owners WHERE project_id=@Id", new { Id = projectId }, cancellationToken: ct)));
        using (var dashboard = Json(await new DashboardService().SummaryAsync(conn, owner, ct)))
        {
            Assert.Equal(1, dashboard.RootElement.GetProperty("projectCount").GetInt32());
            Assert.Equal(1, dashboard.RootElement.GetProperty("activeProjectCount").GetInt32());
        }

        var duplicate = await Assert.ThrowsAsync<ApiException>(() =>
            projects.CreateAsync(conn, owner, fixture.Request("生命周期项目"), null, ct));
        Assert.Equal((409, ProjectService.NameExistsMessage), (duplicate.Status, duplicate.Message));

        var messages = new MessageService(new AuditService([]), new AppOptions());
        await messages.CreateAsync(conn, owner, projectId, new MessageCreateRequest { Content = "开始协作" }, null, ct);

        var supplierFileId = await InsertFileAsync(conn, projectId, supplier.Id, "S2C", "a.txt", ct);
        var supplierApproval = await Assert.ThrowsAsync<ApiException>(() => projects.ApproveMaterialsAsync(
            conn, supplier, projectId, new ProjectMaterialReviewRequest { ExpectedSupplierFileId = supplierFileId }, null, ct));
        Assert.Equal(403, supplierApproval.Status);
        var returned = await projects.ReturnMaterialsAsync(conn, owner, projectId,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = supplierFileId, Reason = "缺少动作流程表" }, null, ct);
        Assert.Equal("RETURNED", returned.MaterialReviewStatus);
        await AssertPendingTasksAsync(conn, supplier, ["RETURNED"], ct);

        var newerFileId = await InsertFileAsync(conn, projectId, supplier.Id, "S2C", "a2.txt", ct);
        await AssertPendingTasksAsync(conn, owner, ["PENDING"], ct);
        using (var pendingList = Json(await projects.ListAsync(conn, owner, 1, 20, null, null, null, "PENDING", false, false, ct)))
            Assert.Equal(projectId, Assert.Single(pendingList.RootElement.GetProperty("list").EnumerateArray()).GetProperty("id").GetUInt64());

        var approved = await projects.ApproveMaterialsAsync(conn, owner, projectId,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = newerFileId }, null, ct);
        Assert.Equal(ProjectStatuses.Completed, approved.Status);
        Assert.NotNull(approved.CompletedAt);
        Assert.Null(approved.MaterialReviewStatus);

        var reopened = await projects.ReopenAsync(conn, owner, projectId,
            new ProjectReopenRequest { ExpectedUpdatedAt = approved.UpdatedAt, Reason = "动作不对" }, null, ct);
        Assert.Equal(ProjectStatuses.InProgress, reopened.Status);
        Assert.Equal("RETURNED", reopened.MaterialReviewStatus);
        Assert.Null(reopened.CompletedAt);

        // Terminating and restarting stay with the responsible user; only a terminated project can be deleted.
        var notTerminated = await Assert.ThrowsAsync<ApiException>(() => projects.DeleteAsync(conn, owner, projectId, null, ct));
        Assert.Equal((400, "仅已终止项目可以删除"), (notTerminated.Status, notTerminated.Message));
        var terminated = await projects.SetStatusAsync(conn, owner, projectId,
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct);
        Assert.True(terminated.CanDelete);
        var restarted = await projects.SetStatusAsync(conn, owner, projectId,
            new ProjectStatusRequest { Status = ProjectStatuses.InProgress, ExpectedStatus = ProjectStatuses.Terminated }, null, ct);
        Assert.Equal(ProjectStatuses.InProgress, restarted.Status);
        Assert.Equal("NO_MATERIAL", restarted.MaterialReviewStatus);
        await projects.SetStatusAsync(conn, owner, projectId,
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct);
        await projects.DeleteAsync(conn, owner, projectId, null, ct);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE id=@Id", new { Id = projectId }, cancellationToken: ct)));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_robot_owners WHERE project_id=@Id", new { Id = projectId }, cancellationToken: ct)));
    }

    [Theory(Timeout = 90_000)]
    [InlineData("NO_SUPPLIER_USER", ProjectService.SupplierWithoutActiveUserStartMessage)]
    [InlineData("NO_UPLOADER", ProjectService.SupplierWithoutUploaderStartMessage)]
    public async Task CreateRefusesUnmetSupplierPrerequisitesAndWritesNothing(string breakage, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_create_prerequisite", ct);
        var fixture = await SeedAsync(database, "前提", 8200, ct);
        await using var conn = fixture.Conn;
        await conn.ExecuteAsync(new CommandDefinition(breakage == "NO_SUPPLIER_USER"
                ? "UPDATE users SET status='DISABLED' WHERE id=@Supplier"
                : "DELETE FROM user_roles WHERE user_id=@Supplier",
            new { Supplier = fixture.Supplier.Id }, cancellationToken: ct));
        var before = await CountsAsync(conn, ct);

        var refused = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("前提项目"), null, ct));

        Assert.Equal((409, 40901, message), (refused.Status, refused.Code, refused.Message));
        Assert.Equal(before, await CountsAsync(conn, ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task EveryInternalAccountSeesEveryProjectButOnlyTheResponsibleUserChangesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_visibility", ct);
        var fixture = await SeedAsync(database, "可见", 8300, ct);
        await using var conn = fixture.Conn;
        var projects = fixture.Projects;
        var created = await projects.CreateAsync(conn, fixture.Owner, fixture.Request("可见项目"), null, ct);
        var other = fixture.Colleague;

        // The colleague finds the project in the full list and its detail, read-only.
        var page = await projects.ListAsync(conn, other, 1, 20, null, null, null, null, false, false, ct);
        var listed = Assert.Single(page.List);
        Assert.Equal(created.Id, listed.Id);
        Assert.False(listed.CanManage);
        Assert.Empty((await projects.ListAsync(conn, other, 1, 20, null, null, null, null, false, true, ct)).List);
        Assert.Single((await projects.ListAsync(conn, fixture.Owner, 1, 20, null, null, null, null, false, true, ct)).List);
        var detail = await projects.DetailAsync(conn, other, created.Id, ct);
        Assert.False(detail.CanManage);

        // The workbench and collaboration feed follow only the colleague's own projects.
        using (var dashboard = Json(await new DashboardService().SummaryAsync(conn, other, ct)))
            Assert.Equal(0, dashboard.RootElement.GetProperty("projectCount").GetInt32());
        // Capture activities so the collaboration feed has something to show.
        var messages = new MessageService(new AuditService([new ProjectActivityService()]), new AppOptions());
        await messages.CreateAsync(conn, fixture.Supplier, created.Id,
            new MessageCreateRequest { Content = "厂商留言", SignerName = "Robot 负责人" }, null, ct);
        Assert.Equal(0UL, (await projects.SummaryAsync(conn, other, created.Id, ct)).UnreadMessages);
        Assert.Equal(1UL, (await projects.SummaryAsync(conn, fixture.Owner, created.Id, ct)).UnreadMessages);
        Assert.Equal(0UL, (await new CollaborationService().NotificationsAsync(conn, other, 1, 20, false, ct)).Total);
        Assert.True((await new CollaborationService().NotificationsAsync(conn, fixture.Owner, 1, 20, false, ct)).Total > 0);

        // Every write by the colleague is refused, although its role grants the permission points.
        await AssertOwnerOnlyAsync(() => projects.UpdateAsync(conn, other, created.Id,
            fixture.Request("可见项目-改名", created.UpdatedAt), null, ct));
        await AssertOwnerOnlyAsync(() => projects.SetStatusAsync(conn, other, created.Id,
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct));
        await AssertOwnerOnlyAsync(() => messages.CreateAsync(conn, other, created.Id,
            new MessageCreateRequest { Content = "非负责人留言" }, null, ct));
        var transfer = await Assert.ThrowsAsync<ApiException>(() => projects.TransferAsync(conn, other, created.Id,
            new ProjectTransferRequest { ResponsibleUserId = other.Id }, null, ct));
        Assert.Equal(403, transfer.Status);
        var supplierFileId = await InsertFileAsync(conn, created.Id, fixture.Supplier.Id, "S2C", "v.txt", ct);
        var approval = await Assert.ThrowsAsync<ApiException>(() => projects.ApproveMaterialsAsync(conn, other, created.Id,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = supplierFileId }, null, ct));
        Assert.Equal(403, approval.Status);
        Assert.Equal("可见项目", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT name FROM projects WHERE id=@Id", new { Id = created.Id }, cancellationToken: ct)));

        // After the owner hands the project over, the roles swap.
        var transferred = await projects.TransferAsync(conn, fixture.Owner, created.Id,
            new ProjectTransferRequest { ResponsibleUserId = other.Id, ExpectedResponsibleUserId = fixture.Owner.Id }, null, ct);
        Assert.Equal(other.Id, transferred.ResponsibleUserId);
        Assert.False(transferred.CanManage);
        await AssertOwnerOnlyAsync(() => messages.CreateAsync(conn, fixture.Owner, created.Id,
            new MessageCreateRequest { Content = "原负责人留言" }, null, ct));
        await messages.CreateAsync(conn, other, created.Id, new MessageCreateRequest { Content = "新负责人留言" }, null, ct);
        Assert.Single((await projects.ListAsync(conn, other, 1, 20, null, null, null, null, false, true, ct)).List);
    }

    [Fact(Timeout = 90_000)]
    public async Task OrganizationLeadersTransferAndDeleteOnlyInsideTheirScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_leader_scope", ct);
        var fixture = await SeedAsync(database, "主管", 8400, ct);
        await using var conn = fixture.Conn;
        await conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO departments(id,parent_id,name,kind,leader_account_id,created_at,updated_at)
            VALUES(8480,NULL,'主管无关课别','SECTION',{fixture.Colleague.Id},UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            """, cancellationToken: ct));
        var created = await fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("主管项目"), null, ct);
        Assert.Equal(fixture.SectionId, created.SectionId);

        // The section leader may hand the project over even though it is not theirs; an unrelated leader may not.
        var unrelated = await Assert.ThrowsAsync<ApiException>(() => fixture.Projects.TransferAsync(conn, fixture.Colleague,
            created.Id, new ProjectTransferRequest { ResponsibleUserId = fixture.Colleague.Id }, null, ct));
        Assert.Equal(403, unrelated.Status);
        var moved = await fixture.Projects.TransferAsync(conn, fixture.Leader, created.Id,
            new ProjectTransferRequest { ResponsibleUserId = fixture.Colleague.Id }, null, ct);
        Assert.Equal(fixture.Colleague.Id, moved.ResponsibleUserId);

        var colleague = fixture.Colleague;
        await fixture.Projects.SetStatusAsync(conn, colleague, created.Id,
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct);
        // The owner itself has no deletion authority; the leader of the project's section does.
        await Assert.ThrowsAsync<ApiException>(() => fixture.Projects.DeleteAsync(conn, colleague, created.Id, null, ct));
        var leaderView = await fixture.Projects.DetailAsync(conn, fixture.Leader, created.Id, ct);
        Assert.True(leaderView.CanDelete);
        await fixture.Projects.DeleteAsync(conn, fixture.Leader, created.Id, null, ct);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE id=@Id", new { Id = created.Id }, cancellationToken: ct)));
    }

    [Fact(Timeout = 90_000)]
    public async Task UpdateRequiresTheSeenVersionAndKeepsTheSupplier()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_update_version", ct);
        var fixture = await SeedAsync(database, "编辑", 8500, ct);
        await using var conn = fixture.Conn;
        var created = await fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("编辑项目"), null, ct);
        // The editor's snapshot travels through JSON exactly as the frontend sends it back.
        var loaded = Json(await fixture.Projects.DetailAsync(conn, fixture.Owner, created.Id, ct)).RootElement
            .GetProperty("updatedAt").GetDateTime();

        var missing = await Assert.ThrowsAsync<ApiException>(() => fixture.Projects.UpdateAsync(conn, fixture.Owner, created.Id,
            fixture.Request("无版本"), null, ct));
        Assert.Equal((400, "expectedUpdatedAt 必须为当前项目的 updatedAt"), (missing.Status, missing.Message));
        var stale = await Assert.ThrowsAsync<ApiException>(() => fixture.Projects.UpdateAsync(conn, fixture.Owner, created.Id,
            fixture.Request("过期编辑", loaded.AddMinutes(-5)), null, ct));
        Assert.Equal((409, 40903, ProjectService.ProjectChangedMessage), (stale.Status, stale.Code, stale.Message));
        var otherSupplier = await Assert.ThrowsAsync<ApiException>(() => fixture.Projects.UpdateAsync(conn, fixture.Owner, created.Id,
            new ProjectUpsertRequest
            {
                Name = "换厂商", SupplierId = 1, WorkOrderNos = ["WO-X"], MachineModel = "机型", RobotPartId = fixture.PartId,
                PriorityId = fixture.PriorityId, RobotTypeId = fixture.RobotTypeId, RobotOwnerContactIds = [fixture.ContactId],
                ExpectedCompletionDate = "2099-12-31", ExpectedUpdatedAt = loaded,
            }, null, ct));
        Assert.Equal(400, otherSupplier.Status);

        var renamed = await fixture.Projects.UpdateAsync(conn, fixture.Owner, created.Id,
            fixture.Request("编辑项目-已核对", loaded), null, ct);
        Assert.Equal("编辑项目-已核对", renamed.Name);
        Assert.Equal(["PROJECT_UPDATE"], (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT action FROM audit_logs WHERE target_id=@Id AND action='PROJECT_UPDATE'",
            new { Id = created.Id.ToString() }, cancellationToken: ct))).ToArray());
    }

    [Fact(Timeout = 90_000)]
    public async Task ReopeningRebuildsTheRobotOwnerSnapshotFrozenWhileCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_reopen_snapshot", ct);
        var fixture = await SeedAsync(database, "快照", 8600, ct);
        await using var conn = fixture.Conn;
        var created = await fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("快照项目"), null, ct);
        var fileId = await InsertFileAsync(conn, created.Id, fixture.Supplier.Id, "S2C", "snap.txt", ct);
        var completed = await fixture.Projects.ApproveMaterialsAsync(conn, fixture.Owner, created.Id,
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = fileId }, null, ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE supplier_contacts SET name='改名后的负责人' WHERE id=@Id", new { Id = fixture.ContactId }, cancellationToken: ct));
        await using (var db = EfDb.Use(conn))
            await RobotOwners.RefreshSnapshotsAsync(db, fixture.ContactId, ct);
        Assert.Equal("Robot 负责人", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT robot_owner_name FROM projects WHERE id=@Id", new { Id = created.Id }, cancellationToken: ct)));

        var reopened = await fixture.Projects.ReopenAsync(conn, fixture.Owner, created.Id,
            new ProjectReopenRequest { ExpectedUpdatedAt = completed.UpdatedAt, Reason = "补充资料" }, null, ct);
        Assert.Equal("改名后的负责人", reopened.RobotOwnerName);
    }

    [Fact(Timeout = 90_000)]
    public async Task SuppliersSeeTheirProjectsWithoutTheResponsibleUsersEmployeeNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_employee_no", ct);
        var fixture = await SeedAsync(database, "工号", 8700, ct);
        await using var conn = fixture.Conn;
        var created = await fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("工号项目"), null, ct);

        Assert.Equal("owner8700", (await fixture.Projects.DetailAsync(conn, fixture.Owner, created.Id, ct)).ResponsibleUserEmployeeNo);
        var supplierDetail = await fixture.Projects.DetailAsync(conn, fixture.Supplier, created.Id, ct);
        Assert.Null(supplierDetail.ResponsibleUserEmployeeNo);
        Assert.False(supplierDetail.CanManage);
        Assert.Equal("负责人8700", supplierDetail.ResponsibleUserName);
        var supplierPage = await fixture.Projects.ListAsync(conn, fixture.Supplier, 1, 20, null, null, null, null, false, false, ct);
        Assert.Null(Assert.Single(supplierPage.List).ResponsibleUserEmployeeNo);
    }

    [Fact(Timeout = 90_000)]
    public async Task KeywordFindsProjectsByWorkOrderMachineModelPartAndRobotOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_keyword", ct);
        var fixture = await SeedAsync(database, "搜索", 8900, ct);
        await using var conn = fixture.Conn;
        var projects = fixture.Projects;
        var first = await projects.CreateAsync(conn, fixture.Owner, fixture.Request("甲项目"), null, ct);
        var second = await projects.CreateAsync(conn, fixture.Owner, new ProjectUpsertRequest
        {
            Name = "乙项目", Description = "公共资料", SupplierId = fixture.BaseId + 1, WorkOrderNos = ["WO-OTHER-7", "WO-EXTRA-8"],
            MachineModel = "另一机型", RobotPartId = fixture.PartId, PriorityId = fixture.PriorityId, RobotTypeId = fixture.RobotTypeId,
            RobotOwnerContactIds = [fixture.ContactId], ExpectedCompletionDate = "2099-12-31",
        }, null, ct);

        async Task<ulong[]> Search(CurrentUser actor, string keyword) =>
            (await projects.ListAsync(conn, actor, 1, 20, keyword, null, null, null, false, false, ct)).List
                .Select(project => project.Id).Order().ToArray();
        ulong[] both = [first.Id, second.Id];

        Assert.Equal([first.Id], await Search(fixture.Colleague, "甲项目"));
        // Any of several work order numbers, matched as a substring.
        Assert.Equal([second.Id], await Search(fixture.Colleague, "EXTRA-8"));
        Assert.Equal([first.Id], await Search(fixture.Colleague, $"WO-{fixture.BaseId}"));
        Assert.Equal([second.Id], await Search(fixture.Colleague, "另一机型"));
        Assert.Equal(both, await Search(fixture.Colleague, $"PART-{fixture.BaseId}"));
        Assert.Equal(both, await Search(fixture.Colleague, "搜索型号"));
        Assert.Equal(both, await Search(fixture.Colleague, "Robot 负责人"));
        Assert.Empty(await Search(fixture.Colleague, "不存在的工令"));
        // LIKE wildcards in the input are literal.
        Assert.Empty(await Search(fixture.Colleague, "WO-%"));
        // The supplier searches only inside its own scope, with the same fields.
        Assert.Equal([second.Id], await Search(fixture.Supplier, "OTHER-7"));
    }

    [Fact(Timeout = 90_000)]
    public async Task ProjectChangesNotifyViewersOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_realtime", ct);
        var publisher = new RecordingPublisher();
        var fixture = await SeedAsync(database, "实时", 8800, ct, publisher);
        await using var conn = fixture.Conn;
        var created = await fixture.Projects.CreateAsync(conn, fixture.Owner, fixture.Request("实时项目"), null, ct);

        publisher.Calls.Clear();
        var renamed = await fixture.Projects.UpdateAsync(conn, fixture.Owner, created.Id,
            fixture.Request("实时项目-改名", created.UpdatedAt), null, ct);
        Assert.Equal([(created.Id, RealtimeChangeKinds.Project)], publisher.Calls.Select(call => (call.ProjectId, call.Kind)).ToArray());

        publisher.Calls.Clear();
        await fixture.Projects.TransferAsync(conn, fixture.Owner, created.Id,
            new ProjectTransferRequest { ResponsibleUserId = fixture.Colleague.Id }, null, ct);
        Assert.Equal([(created.Id, RealtimeChangeKinds.Project)], publisher.Calls.Select(call => (call.ProjectId, call.Kind)).ToArray());

        await fixture.Projects.SetStatusAsync(conn, fixture.Colleague, renamed.Id,
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct);
        publisher.Calls.Clear();
        await fixture.Projects.DeleteAsync(conn, fixture.Leader, created.Id, null, ct);
        var deletion = Assert.Single(publisher.Calls);
        var audience = Assert.IsType<ProjectRealtimeAudience>(deletion.Audience);
        Assert.Contains(fixture.Colleague.Id, audience.InternalUserIds);
        Assert.Contains(fixture.Owner.Id, audience.InternalUserIds);
        Assert.Contains(fixture.Supplier.Id, audience.SupplierUserIds);
    }

    private static async Task AssertOwnerOnlyAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal((403, ProjectAccessService.OwnerOnlyMessage), (error.Status, error.Message));
    }

    private static async Task<(long Projects, long WorkOrders, long Owners, long Logs, long Audits, long Mail)> CountsAsync(
        MySqlConnector.MySqlConnection conn, CancellationToken ct) =>
        await conn.QuerySingleAsync<(long, long, long, long, long, long)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM projects),(SELECT COUNT(*) FROM project_work_orders),
                   (SELECT COUNT(*) FROM project_robot_owners),(SELECT COUNT(*) FROM project_status_logs),
                   (SELECT COUNT(*) FROM audit_logs WHERE target_type='project'),(SELECT COUNT(*) FROM email_outbox)
            """, cancellationToken: ct));

    private static async Task<ulong> InsertFileAsync(
        MySqlConnector.MySqlConnection conn, ulong projectId, ulong uploaderId, string direction, string name, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,@UploaderId,@Direction,@Name,CONCAT(UUID(),'.txt'),'txt',1,CONCAT('files/',UUID(),'.txt'),'AVAILABLE')
            """, new { ProjectId = projectId, UploaderId = uploaderId, Direction = direction, Name = name }, cancellationToken: ct));
        return await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: ct));
    }

    private static async Task AssertPendingTasksAsync(
        MySqlConnector.MySqlConnection conn, CurrentUser viewer, string[] expectedStates, CancellationToken ct)
    {
        using var summary = Json(await new DashboardService().SummaryAsync(conn, viewer, ct));
        Assert.Equal(expectedStates.Length, summary.RootElement.GetProperty("pendingTasks").GetInt32());
        using var pending = Json(await new DashboardService().PendingProjectsAsync(conn, viewer, 1, 20, ct));
        Assert.Equal(expectedStates, pending.RootElement.GetProperty("list").EnumerateArray()
            .Select(item => item.GetProperty("materialReviewStatus").GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A supplier (base+1) with an active contact and an uploader account (base+5); an owner (base+3, built-in
    /// administrator) and a colleague (base+4, project administrator role without delete/transfer scope) in one
    /// section (base+2); a section leader account (base+8); the catalog the create request needs.
    /// </summary>
    private static async Task<Fixture> SeedAsync(
        SchemaShapeTests.SchemaDatabaseScope database, string label, ulong baseId, CancellationToken ct,
        IProjectRealtimePublisher? publisher = null)
    {
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync($"""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES({baseId + 1},'{label}供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO supplier_contacts(id,supplier_id,name,email,status)
            VALUES({10_000_000 + baseId},{baseId + 1},'Robot 负责人','contact{baseId}@example.test','ACTIVE');
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES({baseId + 2},NULL,'{label}课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES({baseId + 3},'owner{baseId}','unused','负责人{baseId}','owner{baseId}@example.test','INTERNAL',NULL,{baseId + 2},'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  ({baseId + 4},'colleague{baseId}','unused','同事{baseId}','colleague{baseId}@example.test','INTERNAL',NULL,{baseId + 2},'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  ({baseId + 5},'supplier{baseId}','unused','厂商{baseId}','supplier{baseId}@example.test','SUPPLIER',{baseId + 1},NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  ({baseId + 8},'leader{baseId}','unused','主管{baseId}','leader{baseId}@example.test','INTERNAL',NULL,{baseId + 2},'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            UPDATE departments SET leader_account_id={baseId + 8} WHERE id={baseId + 2};
            INSERT INTO user_roles(user_id,role_id) VALUES({baseId + 3},1),({baseId + 4},2),({baseId + 5},4),({baseId + 8},2);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES({baseId + 6},{baseId + 1},'PART-{baseId}','{label}型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES({baseId + 7},'PRIORITY','{label}优先级',NULL,1,'ACTIVE'),
                  ({1_000_000 + baseId + 7},'ROBOT_TYPE','{label}Robot类型',NULL,1,'ACTIVE');
            """, ct);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, new AppOptions(), publisher);
        var conn = await database.Database.OpenAsync(ct);
        return new Fixture(projects, conn, baseId,
            new CurrentUser(baseId + 3, $"owner{baseId}", "INTERNAL", null),
            new CurrentUser(baseId + 4, $"colleague{baseId}", "INTERNAL", null),
            new CurrentUser(baseId + 8, $"leader{baseId}", "INTERNAL", null),
            new CurrentUser(baseId + 5, $"supplier{baseId}", "SUPPLIER", baseId + 1));
    }

    private sealed record Fixture(
        ProjectService Projects, MySqlConnector.MySqlConnection Conn, ulong BaseId,
        CurrentUser Owner, CurrentUser Colleague, CurrentUser Leader, CurrentUser Supplier)
    {
        internal ulong SectionId => BaseId + 2;
        internal ulong ContactId => 10_000_000 + BaseId;
        internal ulong PartId => BaseId + 6;
        internal ulong PriorityId => BaseId + 7;
        internal ulong RobotTypeId => 1_000_000 + BaseId + 7;

        internal ProjectUpsertRequest Request(string name, DateTime? expectedUpdatedAt = null) => new()
        {
            Name = name,
            Description = "公共资料",
            SupplierId = BaseId + 1,
            WorkOrderNos = [$"WO-{BaseId}"],
            MachineModel = "机型",
            RobotPartId = PartId,
            PriorityId = PriorityId,
            RobotTypeId = RobotTypeId,
            RobotOwnerContactIds = [ContactId],
            ExpectedCompletionDate = "2099-12-31",
            ExpectedUpdatedAt = expectedUpdatedAt,
        };
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
}
