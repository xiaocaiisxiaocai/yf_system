using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectMetadataTests
{
    [Fact(Timeout = 60_000)]
    public async Task MetadataRoundTripsWithDerivedSectionAndDictionaryHistoryProtection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_metadata", ct);
        await database.CreateBaselineAsync(legacyV16: false, ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8001,'测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(7001,NULL,'装配课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES
              (9001,'metadata-admin','test','项目管理员','admin@example.test','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9002,'metadata-owner','test','项目负责人','owner@example.test','INTERNAL',7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9001,1),(9002,1);
            """, ct);

        var actor = new CurrentUser(9001, "metadata-admin", "INTERNAL", null);
        var audit = new AuditService([]);
        var dictionaries = new ProjectDictionaryService(audit);
        var projects = new ProjectService(audit, new AppOptions());
        await using var conn = await database.Database.OpenAsync(ct);

        var vendorId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_VENDOR", Code = "VENDOR_A", Name = "厂商 A", SortNo = 10, Enabled = true,
        }, null, ct));
        var otherVendorId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_VENDOR", Code = "VENDOR_B", Name = "厂商 B", SortNo = 20, Enabled = true,
        }, null, ct));
        var modelId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_MODEL", Code = "MODEL_A", Name = "型号 A", ParentId = vendorId, SortNo = 10, Enabled = true,
        }, null, ct));
        var priorityId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM project_dictionaries WHERE type='PRIORITY' AND code='HIGH'", cancellationToken: ct));

        var created = await projects.CreateAsync(conn, actor, new()
        {
            Name = "元数据集成项目",
            Description = "初始",
            SupplierId = 8001,
            WorkOrderNos = [" WO-002 ", "", "wo-002", "WO-001"],
            MachineModel = " 机型-X ",
            RobotVendorId = vendorId,
            RobotModelId = modelId,
            ResponsibleUserId = 9002,
            PriorityId = priorityId,
            ExpectedCompletionDate = "2026-12-31",
        }, null, ct);
        var projectId = Id(created);
        using (var detail = Json(await projects.DetailAsync(conn, actor, projectId, ct)))
        {
            var root = detail.RootElement;
            Assert.Equal(["WO-002", "WO-001"], root.GetProperty("workOrderNos").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal("机型-X", root.GetProperty("machineModel").GetString());
            Assert.Equal(vendorId, root.GetProperty("robotVendorId").GetUInt64());
            Assert.Equal(modelId, root.GetProperty("robotModelId").GetUInt64());
            Assert.Equal(9002UL, root.GetProperty("responsibleUserId").GetUInt64());
            Assert.Equal(7001UL, root.GetProperty("sectionId").GetUInt64());
            Assert.Equal("装配课", root.GetProperty("sectionName").GetString());
            Assert.Equal("2026-12-31", root.GetProperty("expectedCompletionDate").GetString());
        }
        Assert.True(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM project_members WHERE project_id=@ProjectId AND user_id=9002)",
            new { ProjectId = projectId }, cancellationToken: ct)));

        await dictionaries.UpdateAsync(conn, actor, vendorId, new()
        {
            Type = "ROBOT_VENDOR", Code = "VENDOR_A", Name = "厂商 A", SortNo = 10, Enabled = false,
        }, null, ct);
        await dictionaries.UpdateAsync(conn, actor, modelId, new()
        {
            Type = "ROBOT_MODEL", Code = "MODEL_A", Name = "型号 A", ParentId = vendorId, SortNo = 10, Enabled = false,
        }, null, ct);
        var updated = await projects.UpdateAsync(conn, actor, projectId, new()
        {
            Name = "元数据集成项目",
            Description = "仅修改说明",
            SupplierId = 8001,
            WorkOrderNos = ["WO-002", "WO-001"],
            MachineModel = "机型-X",
            RobotVendorId = vendorId,
            RobotModelId = modelId,
            ResponsibleUserId = 9002,
            PriorityId = priorityId,
            ExpectedCompletionDate = "2026-12-31",
        }, null, ct);
        using (var updatedJson = Json(updated))
        {
            Assert.Equal("厂商 A", updatedJson.RootElement.GetProperty("robotVendorName").GetString());
            Assert.Equal("型号 A", updatedJson.RootElement.GetProperty("robotModelName").GetString());
        }

        var parentChange = await Assert.ThrowsAsync<ApiException>(() => dictionaries.UpdateAsync(conn, actor, modelId, new()
        {
            Type = "ROBOT_MODEL", Code = "MODEL_A", Name = "型号 A", ParentId = otherVendorId, SortNo = 10, Enabled = false,
        }, null, ct));
        Assert.Equal(409, parentChange.Status);
        var delete = await Assert.ThrowsAsync<ApiException>(() => dictionaries.DeleteAsync(conn, actor, modelId, null, ct));
        Assert.Equal(409, delete.Status);
    }

    private static ulong Id(object value)
    {
        using var json = Json(value);
        return json.RootElement.GetProperty("id").GetUInt64();
    }

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
}
