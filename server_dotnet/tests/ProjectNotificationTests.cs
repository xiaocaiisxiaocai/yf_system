using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>Robot 仿真协作第 2、3 批：草稿禁言、状态邮件开关、列表筛选与到期提醒。</summary>
public sealed class ProjectNotificationTests
{
    [Fact(Timeout = 90_000)]
    public async Task DraftsRejectMessagesAndStatusChangesMailTheSupplierPerSwitch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_status_mail", ct);
        var (groups, projects, audit, actor, supplier, children) = await SeedAsync(database, ct);
        await using var conn = await database.Database.OpenAsync(ct);
        var messages = new MessageService(audit, new AppOptions());

        var draftMessage = await Assert.ThrowsAsync<ApiException>(() => messages.CreateAsync(
            conn, supplier, children[0], new MessageCreateRequest { Content = "草稿留言" }, null, ct));
        Assert.Equal((409, "项目尚未开始，暂不能留言"), (draftMessage.Status, draftMessage.Message));
        var internalDraftMessage = await Assert.ThrowsAsync<ApiException>(() => messages.CreateAsync(
            conn, actor, children[0], new MessageCreateRequest { Content = "草稿留言" }, null, ct));
        Assert.Equal(409, internalDraftMessage.Status);

        await Start(projects, conn, actor, children[0], ct);
        Assert.Equal(1, await CountAsync(conn, children[0], "PROJECT_STARTED", ct));
        Assert.Equal("contact8101@example.test", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT recipient_email FROM email_outbox WHERE project_id=@ProjectId AND event_type='PROJECT_STARTED'",
            new { ProjectId = children[0] }, cancellationToken: ct)));
        await messages.CreateAsync(conn, supplier, children[0], new MessageCreateRequest { Content = "开始后可留言", SignerName = "Robot 负责人" }, null, ct);

        await SqlAsync(conn, "UPDATE system_configs SET cfg_value='false' WHERE cfg_key IN ('notify.event.project_started','notify.event.project_terminated')", ct);
        await Start(projects, conn, actor, children[1], ct);
        await projects.SetStatusAsync(conn, actor, children[1],
            new ProjectStatusRequest { Status = ProjectStatuses.Terminated }, null, ct);
        await Start(projects, conn, actor, children[1], ct);
        Assert.Equal(0, await CountAsync(conn, children[1], "PROJECT_STARTED", ct));
        Assert.Equal(0, await CountAsync(conn, children[1], "PROJECT_TERMINATED", ct));
        Assert.Equal(1, await CountAsync(conn, children[1], "PROJECT_RESTARTED", ct));
        // Supplier-side mail never goes to the internal actor.
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE recipient_user_id=@ActorId AND event_type LIKE 'PROJECT_%'",
            new { ActorId = actor.Id }, cancellationToken: ct)));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,@SupplierId,'S2C','a.txt','mail-a.txt','txt',1,'files/mail-a.txt','AVAILABLE')
            """, new { ProjectId = children[0], SupplierId = supplier.Id }, cancellationToken: ct));
        var supplierFileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM files WHERE project_id=@ProjectId", new { ProjectId = children[0] }, cancellationToken: ct));
        using var approved = Json(await projects.ApproveMaterialsAsync(conn, actor, children[0],
            new ProjectMaterialReviewRequest { ExpectedSupplierFileId = supplierFileId }, null, ct));
        await projects.ReopenAsync(conn, actor, children[0], new ProjectReopenRequest
        {
            ExpectedUpdatedAt = approved.RootElement.GetProperty("updatedAt").GetDateTime(),
            Reason = "动作流程需要返工",
        }, null, ct);
        Assert.Equal(1, await CountAsync(conn, children[0], "PROJECT_REOPENED", ct));
        Assert.Contains("重开原因：动作流程需要返工", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT body FROM email_outbox WHERE project_id=@ProjectId AND event_type='PROJECT_REOPENED'",
            new { ProjectId = children[0] }, cancellationToken: ct)));
        _ = groups;
    }

    [Fact(Timeout = 90_000)]
    public async Task MainProjectListFiltersByMaterialStateAndOverdue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_list_filters", ct);
        var (groups, projects, _, actor, supplier, children) = await SeedAsync(database, ct);
        await using var conn = await database.Database.OpenAsync(ct);

        async Task<int> Total(string? materialState, bool overdue)
        {
            var page = await groups.ListAsync(conn, actor, 1, 20, null, null, null, materialState, overdue, ct);
            return (int)page.Total;
        }

        Assert.Equal(0, await Total("NO_MATERIAL", false));
        await Start(projects, conn, actor, children[0], ct);
        Assert.Equal(1, await Total("NO_MATERIAL", false));
        Assert.Equal(0, await Total("PENDING", false));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
            VALUES(@ProjectId,@SupplierId,'S2C','a.txt','filter-a.txt','txt',1,'files/filter-a.txt','AVAILABLE')
            """, new { ProjectId = children[0], SupplierId = supplier.Id }, cancellationToken: ct));
        Assert.Equal(1, await Total("PENDING", false));
        Assert.Equal(0, await Total("NO_MATERIAL", false));
        var invalid = await Assert.ThrowsAsync<ApiException>(() => Total("APPROVED", false));
        Assert.Equal(400, invalid.Status);

        Assert.Equal(0, await Total(null, true));
        await SqlAsync(conn, "UPDATE project_groups SET expected_completion_date=DATE(UTC_TIMESTAMP()+INTERVAL 8 HOUR)-INTERVAL 1 DAY", ct);
        Assert.Equal(1, await Total(null, true));
        await SqlAsync(conn, "UPDATE project_groups SET status='COMPLETED'", ct);
        Assert.Equal(0, await Total(null, true));
    }

    [Fact(Timeout = 90_000)]
    public async Task DueRemindersAreQueuedOncePerDueDateAndHonourTheirSwitches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_due_reminders", ct);
        var (_, projects, audit, actor, supplier, children) = await SeedAsync(database, ct);
        await using var conn = await database.Database.OpenAsync(ct);
        var reminders = new ProjectDueReminderService(database.Database, database.Options, audit,
            NullLogger<ProjectDueReminderService>.Instance);
        await Start(projects, conn, actor, children[0], ct);
        await SetDueAsync(conn, children[1], 1, ct);

        // Outside the 3-day window nothing is due; drafts are never reminded.
        await SetDueAsync(conn, children[0], 4, ct);
        Assert.Equal(0, await reminders.RunOnceAsync(conn, ct));

        await SetDueAsync(conn, children[0], 3, ct);
        Assert.Equal(1, await reminders.RunOnceAsync(conn, ct));
        Assert.Equal(0, await reminders.RunOnceAsync(conn, ct));
        Assert.Equal([actor.Id, supplier.Id], await RecipientsAsync(conn, children[0], "PROJECT_DUE_SOON", ct));
        Assert.Contains("将在 3 天后到期", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT subject FROM email_outbox WHERE project_id=@ProjectId AND event_type='PROJECT_DUE_SOON' LIMIT 1",
            new { ProjectId = children[0] }, cancellationToken: ct)));

        // Overdue goes out once; with the responsible-user switch off only the supplier side is mailed.
        await SqlAsync(conn, "UPDATE system_configs SET cfg_value='false' WHERE cfg_key='notify.due.internal.enabled'", ct);
        await SetDueAsync(conn, children[0], -2, ct);
        Assert.Equal(1, await reminders.RunOnceAsync(conn, ct));
        Assert.Equal(0, await reminders.RunOnceAsync(conn, ct));
        Assert.Equal([supplier.Id], await RecipientsAsync(conn, children[0], "PROJECT_OVERDUE", ct));

        // A new due date reminds again; the disabled event switch stops the scan entirely.
        await SqlAsync(conn, "UPDATE system_configs SET cfg_value='false' WHERE cfg_key='notify.event.project_overdue'", ct);
        await SetDueAsync(conn, children[0], -1, ct);
        Assert.Equal(0, await reminders.RunOnceAsync(conn, ct));
        await SqlAsync(conn, "UPDATE system_configs SET cfg_value='true' WHERE cfg_key='notify.event.project_overdue'", ct);
        Assert.Equal(1, await reminders.RunOnceAsync(conn, ct));
        Assert.Equal(2, await CountAsync(conn, children[0], "PROJECT_OVERDUE", ct));

        // The scan never bumps updated_at, so clients keep their optimistic-concurrency baseline.
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE id=@ProjectId AND updated_at>UTC_TIMESTAMP(3)",
            new { ProjectId = children[0] }, cancellationToken: ct)));
    }

    private static async Task<(ProjectGroupService Groups, ProjectService Projects, AuditService Audit, CurrentUser Actor,
        CurrentUser Supplier, ulong[] Children)> SeedAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8101,'通知供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO supplier_contacts(id,supplier_id,name,email,status) VALUES(10008101,8101,'Robot 负责人','contact8101@example.test','ACTIVE');
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(8201,NULL,'通知课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(8301,'notify-owner','unused','通知负责人','owner@example.test','INTERNAL',NULL,8201,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8302,'notify-supplier','unused','通知厂商','supplier@example.test','SUPPLIER',8101,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(8301,1),(8302,4);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(8402,8101,'NOTIFY-PART','通知型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(8403,'PRIORITY','通知优先级',NULL,1,'ACTIVE'),
                  (1008403,'ROBOT_TYPE','通知 Robot 类型',NULL,1,'ACTIVE');
            """, ct);
        var actor = new CurrentUser(8301, "notify-owner", "INTERNAL", null);
        var supplier = new CurrentUser(8302, "notify-supplier", "SUPPLIER", 8101);
        var audit = new AuditService([]);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        var group = await groups.CreateAsync(conn, actor, new ProjectUpsertRequest
        {
            Name = "通知主项目",
            SupplierId = 8101,
            WorkOrderNos = ["WO-NOTIFY-1"],
            MachineModel = "机型-N",
            RobotPartId = 8402,
            PriorityId = 8403,
            RobotTypeId = 1008403,
            RobotOwnerContactIds = [10008101],
            ExpectedCompletionDate = "2099-12-31",
            SubprojectNames = ["通知子项目-A", "通知子项目-B"],
        }, null, ct);
        var children = (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId ORDER BY id", new { GroupId = group.Id },
            cancellationToken: ct))).ToArray();
        return (groups, projects, audit, actor, supplier, children);
    }

    private static Task Start(ProjectService projects, MySqlConnector.MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct) =>
        projects.SetStatusAsync(conn, actor, projectId, new ProjectStatusRequest { Status = ProjectStatuses.InProgress }, null, ct);

    private static Task SetDueAsync(MySqlConnector.MySqlConnection conn, ulong projectId, int daysFromToday, CancellationToken ct) =>
        SqlAsync(conn,
            $"UPDATE projects SET expected_completion_date=DATE(UTC_TIMESTAMP()+INTERVAL 8 HOUR)+INTERVAL {daysFromToday} DAY WHERE id={projectId}", ct);

    private static Task SqlAsync(MySqlConnector.MySqlConnection conn, string sql, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

    private static Task<int> CountAsync(MySqlConnector.MySqlConnection conn, ulong projectId, string eventType, CancellationToken ct) =>
        conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=@ProjectId AND event_type=@EventType",
            new { ProjectId = projectId, EventType = eventType }, cancellationToken: ct));

    private static async Task<ulong[]> RecipientsAsync(MySqlConnector.MySqlConnection conn, ulong projectId, string eventType, CancellationToken ct) =>
        (await conn.QueryAsync<ulong>(new CommandDefinition(
            "SELECT DISTINCT recipient_user_id FROM email_outbox WHERE project_id=@ProjectId AND event_type=@EventType ORDER BY recipient_user_id",
            new { ProjectId = projectId, EventType = eventType }, cancellationToken: ct))).ToArray();

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));
}
