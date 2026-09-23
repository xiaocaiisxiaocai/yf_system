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
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8001,'测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO departments(id,parent_id,name,kind,status,created_at,updated_at)
            VALUES(7001,NULL,'装配课','SECTION','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7002,NULL,'停用课别','SECTION','DISABLED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7010,NULL,'停用事业部','DIVISION','DISABLED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7011,7010,'停用链部门','DEPARTMENT','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7012,7011,'停用链课别','SECTION','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7020,NULL,'有效事业部','DIVISION','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7021,7020,'有效部门','DEPARTMENT','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (7022,7021,'有效课别','SECTION','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES
              (9001,'metadata-admin','test','项目管理员','admin@example.test','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9002,'metadata-owner','test','项目负责人','owner@example.test','INTERNAL',7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9003,'metadata-disabled-section','test','停用课别负责人','disabled-section@example.test','INTERNAL',7002,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9004,'metadata-disabled-ancestor','test','停用上级负责人','disabled-ancestor@example.test','INTERNAL',7012,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9005,'metadata-valid-tree','test','有效组织链负责人','valid-tree@example.test','INTERNAL',7022,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9001,1),(9002,1),(9003,1),(9004,1),(9005,1);
            """, ct);

        var actor = new CurrentUser(9001, "metadata-admin", "INTERNAL", null);
        var audit = new AuditService([]);
        var dictionaries = new ProjectDictionaryService(audit);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);

        var vendorId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_VENDOR", Name = "厂商 A", SortNo = 10, Enabled = true,
        }, null, ct));
        var otherVendorId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_VENDOR", Name = "厂商 B", SortNo = 20, Enabled = true,
        }, null, ct));
        var modelId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_MODEL", Name = "型号 A", ParentId = vendorId, SortNo = 10, Enabled = true,
        }, null, ct));
        var priorityId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM project_dictionaries WHERE type='PRIORITY' AND name='高'", cancellationToken: ct));

        var complete = new ProjectUpsertRequest
        {
            Name = "必填校验项目", SupplierId = 8001, WorkOrderNos = ["WO-REQUIRED"],
            MachineModel = "M1", RobotVendorId = vendorId, RobotModelId = modelId,
            ResponsibleUserId = 9002, PriorityId = priorityId, ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["必填校验子项目"],
        };
        using (var ownerOptions = Json(await projects.ProjectOwnerOptionsAsync(conn, actor, ct)))
        {
            var options = ownerOptions.RootElement.EnumerateArray().ToArray();
            Assert.Equal([9002UL, 9005UL], options.Select(option => option.GetProperty("id").GetUInt64()).Order().ToArray());
            Assert.All(options, option =>
            {
                Assert.Equal(JsonValueKind.Number, option.GetProperty("sectionId").ValueKind);
                Assert.Equal(JsonValueKind.String, option.GetProperty("sectionName").ValueKind);
            });
        }
        foreach (var invalidOwnerId in new[] { 9003UL, 9004UL })
        {
            var invalidOwner = new ProjectUpsertRequest
            {
                Name = complete.Name,
                SupplierId = complete.SupplierId,
                WorkOrderNos = complete.WorkOrderNos,
                MachineModel = complete.MachineModel,
                RobotVendorId = complete.RobotVendorId,
                RobotModelId = complete.RobotModelId,
                ResponsibleUserId = invalidOwnerId,
                PriorityId = complete.PriorityId,
                ExpectedCompletionDate = complete.ExpectedCompletionDate,
                SubprojectNames = complete.SubprojectNames,
            };
            var invalidOwnerError = await Assert.ThrowsAsync<ApiException>(() =>
                groups.CreateAsync(conn, actor, invalidOwner, null, ct));
            Assert.Equal(400, invalidOwnerError.Status);
        }
        foreach (var field in new[] { "workOrderNos", "machineModel", "robotVendorId", "robotModelId", "responsibleUserId", "priorityId", "expectedCompletionDate" })
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(complete)!;
            node[field] = null;
            var missing = System.Text.Json.JsonSerializer.Deserialize<ProjectUpsertRequest>(node)!;
            var error = await Assert.ThrowsAsync<ApiException>(() => groups.CreateAsync(conn, actor, missing, null, ct));
            Assert.Equal(400, error.Status);
        }
        await conn.ExecuteAsync("UPDATE users SET department_id=NULL WHERE id=9002");
        var noSection = await Assert.ThrowsAsync<ApiException>(() => groups.CreateAsync(conn, actor, complete, null, ct));
        Assert.Equal(400, noSection.Status);
        await conn.ExecuteAsync("UPDATE users SET department_id=7001 WHERE id=9002");

        var created = await groups.CreateAsync(conn, actor, new()
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
            SubprojectNames = ["元数据子项目"],
        }, null, ct);
        var groupId = Id(created);
        var projectId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM projects WHERE project_group_id=@GroupId", new { GroupId = groupId }, cancellationToken: ct));
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
        await dictionaries.UpdateAsync(conn, actor, vendorId, new()
        {
            Type = "ROBOT_VENDOR", Name = "厂商 A", SortNo = 10, Enabled = false,
        }, null, ct);
        await dictionaries.UpdateAsync(conn, actor, modelId, new()
        {
            Type = "ROBOT_MODEL", Name = "型号 A", ParentId = vendorId, SortNo = 10, Enabled = false,
        }, null, ct);
        var updated = await groups.UpdateAsync(conn, actor, groupId, new()
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
            Assert.Equal("仅修改说明", updatedJson.RootElement.GetProperty("description").GetString());
            Assert.Equal("厂商 A", updatedJson.RootElement.GetProperty("robotVendorName").GetString());
            Assert.Equal("型号 A", updatedJson.RootElement.GetProperty("robotModelName").GetString());
        }
        using (var updatedChild = Json(await projects.DetailAsync(conn, actor, projectId, ct)))
        {
            Assert.Equal(JsonValueKind.Null, updatedChild.RootElement.GetProperty("description").ValueKind);
            Assert.Equal("厂商 A", updatedChild.RootElement.GetProperty("robotVendorName").GetString());
        }

        var parentChange = await Assert.ThrowsAsync<ApiException>(() => dictionaries.UpdateAsync(conn, actor, modelId, new()
        {
            Type = "ROBOT_MODEL", Name = "型号 A", ParentId = otherVendorId, SortNo = 10, Enabled = false,
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

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
