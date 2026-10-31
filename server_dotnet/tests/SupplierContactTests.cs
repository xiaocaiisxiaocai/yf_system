using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class SupplierContactTests
{
    [Fact(Timeout = 90_000)]
    public async Task ContactsDriveRobotOwnersTypedSignaturesAndSupplierMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("supplier_contacts", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(9101,'联络人供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9102,'其他供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(9201,NULL,'联络人课别','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9301,'contact-owner','unused','联络人负责人','owner@example.test','INTERNAL',NULL,9201,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9302,'shared-supplier','unused','共用账号','shared@example.test','SUPPLIER',9101,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9301,1),(9302,4);
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES(9402,9101,'CONTACT-PART','联络人型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES(9403,'PRIORITY','联络人优先级',NULL,1,'ACTIVE'),
                  (1009403,'ROBOT_TYPE','联络人Robot类型',NULL,1,'ACTIVE');
            """, ct);
        var admin = new CurrentUser(9301, "contact-owner", "INTERNAL", null);
        var supplier = new CurrentUser(9302, "shared-supplier", "SUPPLIER", 9101);
        var audit = new AuditService([]);
        var contacts = new SupplierContactService(EfTestSupport.DbContextFactory(database.Options), audit);

        var zhang = await contacts.CreateAsync(admin, 9101, new SupplierContactUpsert("张三", "zhang@example.test", 1), ct);
        var li = await contacts.CreateAsync(admin, 9101, new SupplierContactUpsert("李四", "li@example.test", 2), ct);
        var other = await contacts.CreateAsync(admin, 9102, new SupplierContactUpsert("王五", "wang@example.test"), ct);
        var duplicate = await Assert.ThrowsAsync<ApiException>(() =>
            contacts.CreateAsync(admin, 9101, new SupplierContactUpsert(" 张三 ", "z2@example.test"), ct));
        Assert.Equal(409, duplicate.Status);
        await Assert.ThrowsAsync<ApiException>(() =>
            contacts.CreateAsync(admin, 9101, new SupplierContactUpsert("赵六", "not-an-email"), ct));
        await Assert.ThrowsAsync<ApiException>(() =>
            contacts.CreateAsync(supplier, 9101, new SupplierContactUpsert("赵六", "zhao@example.test"), ct));
        Assert.Equal(["张三", "李四"], (await contacts.ListAsync(admin, 9101, ct)).Select(item => item.Name));

        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);
        ProjectUpsertRequest Request(string name, ulong[] owners, string? description = null, DateTime? expectedUpdatedAt = null) => new()
        {
            Name = name, Description = description, SupplierId = 9101, WorkOrderNos = ["WO-CONTACT"], MachineModel = "机型",
            RobotPartId = 9402, PriorityId = 9403, RobotTypeId = 1009403, ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = [name + "子项目"], RobotOwnerContactIds = owners, ExpectedUpdatedAt = expectedUpdatedAt,
        };
        var request = Request("联络人主项目", [li.Id, zhang.Id]);
        var foreign = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, admin, Request("跨供应商", [other.Id]), null, ct));
        Assert.Equal("Robot 负责人必须是该供应商的联络人", foreign.Message);
        var missing = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, admin, Request("无负责人", []), null, ct));
        Assert.Equal("请选择 Robot 负责人", missing.Message);

        using var created = Json(await groups.CreateAsync(conn, admin, request, null, ct));
        var groupId = created.RootElement.GetProperty("id").GetUInt64();
        Assert.Equal("李四、张三", created.RootElement.GetProperty("robotOwnerName").GetString());
        Assert.Equal([li.Id, zhang.Id], created.RootElement.GetProperty("robotOwnerContactIds")
            .EnumerateArray().Select(item => item.GetUInt64()));
        var projectId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId", new { GroupId = groupId }, cancellationToken: ct));
        Assert.Equal("李四、张三", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT robot_owner_name FROM projects WHERE id=@ProjectId", new { ProjectId = projectId }, cancellationToken: ct)));

        // Renaming a contact refreshes the snapshot of open projects without bumping their version.
        var updatedAt = created.RootElement.GetProperty("updatedAt").GetDateTime();
        // The schema fixture pools a single connection; release it while the admin service opens its own.
        await conn.CloseAsync();
        await contacts.UpdateAsync(admin, li.Id, new SupplierContactUpsert("李四四", "li@example.test", 2), ct);
        // A disabled contact that is already selected may stay; it can no longer be added anywhere new.
        await contacts.SetStatusAsync(admin, li.Id, AccountStatuses.Disabled, ct);
        await conn.OpenAsync(ct);
        Assert.Equal("李四四、张三", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT robot_owner_name FROM project_groups WHERE id=@GroupId", new { GroupId = groupId }, cancellationToken: ct)));
        using (var kept = Json(await groups.UpdateAsync(conn, admin, groupId,
                   Request("联络人主项目", [li.Id, zhang.Id], "保留已停用的负责人", updatedAt), null, ct)))
            Assert.Equal("李四四、张三", kept.RootElement.GetProperty("robotOwnerName").GetString());
        var disabledNew = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, admin, Request("停用联络人", [li.Id]), null, ct));
        Assert.Equal("联络人「李四四」已停用，不能选为 Robot 负责人", disabledNew.Message);

        await projects.SetStatusAsync(conn, admin, projectId,
            new ProjectStatusRequest { Status = ProjectStatuses.InProgress }, null, ct);
        var messages = new MessageService(audit, database.Options);
        var unsigned = await Assert.ThrowsAsync<ApiException>(() => messages.CreateAsync(
            conn, supplier, projectId, new MessageCreateRequest { Content = "没有签名" }, null, ct));
        Assert.Equal("请填写签名（提供人姓名）", unsigned.Message);
        var stranger = await Assert.ThrowsAsync<ApiException>(() => messages.CreateAsync(
            conn, supplier, projectId, new MessageCreateRequest { Content = "冒名", SignerName = "王五" }, null, ct));
        Assert.Equal("签名「王五」不是该供应商登记的联络人，请核对姓名", stranger.Message);
        var disabledSigner = await Assert.ThrowsAsync<ApiException>(() => messages.CreateAsync(
            conn, supplier, projectId, new MessageCreateRequest { Content = "已停用", SignerName = "李四四" }, null, ct));
        Assert.Equal(400, disabledSigner.Status);
        using (var signed = Json(await messages.CreateAsync(conn, supplier, projectId,
                   new MessageCreateRequest { Content = "张三提供的资料说明", SignerName = " 张三 " }, null, ct)))
            Assert.Equal("张三", signed.RootElement.GetProperty("signerName").GetString());
        using (var internalMessage = Json(await messages.CreateAsync(conn, admin, projectId,
                   new MessageCreateRequest { Content = "内部留言不签名", SignerName = "张三" }, null, ct)))
            Assert.Equal(JsonValueKind.Null, internalMessage.RootElement.GetProperty("signerName").ValueKind);

        // Supplier-facing mail goes to every active contact of the supplier, not the shared account address.
        var recipients = (await conn.QueryAsync<(ulong UserId, string Email)>(new CommandDefinition(
            "SELECT recipient_user_id,recipient_email FROM email_outbox WHERE project_id=@ProjectId AND event_type='MESSAGE_CREATED' ORDER BY id",
            new { ProjectId = projectId }, cancellationToken: ct))).Where(row => row.UserId == 9302).Select(row => row.Email).ToArray();
        Assert.Equal(["zhang@example.test"], recipients);
    }

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));
}
