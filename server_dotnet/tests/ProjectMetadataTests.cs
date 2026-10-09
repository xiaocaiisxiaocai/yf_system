using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectMetadataTests
{
    [Fact(Timeout = 60_000)]
    public async Task RobotPartMetadataRoundTripsWithAutomaticOwnerAndNullableSection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_metadata", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(8001,'测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (8002,'其他供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9001,'metadata-admin','test','项目管理员','admin@example.test','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9001,1);
            """, ct);

        var actor = new CurrentUser(9001, "metadata-admin", "INTERNAL", null);
        var audit = new AuditService([]);
        var dictionaries = new ProjectDictionaryService(audit);
        var robotParts = new RobotPartService(audit);
        var groupStatus = new ProjectGroupStatusService(audit);
        var groups = new ProjectGroupService(audit, groupStatus);
        var projects = new ProjectService(audit, new AppOptions(), groupStatus);
        await using var conn = await database.Database.OpenAsync(ct);

        var priorityId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM project_dictionaries WHERE type='PRIORITY' AND name='高'", cancellationToken: ct));
        // Besides the seeded presets, Robot types are maintained like any dictionary entry.
        var robotTypeId = Id(await dictionaries.CreateAsync(conn, actor, new()
        {
            Type = " robot_type ",
            Name = " 测试类型 ",
            SortNo = 10,
            Enabled = true,
        }, null, ct));
        var partId = Id(await robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8001,
            PartNumber = " RP-001 ",
            Model = " Robot Model A ",
            SortNo = 10,
            Enabled = true,
        }, null, ct));

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8001,
            PartNumber = "RP-001",
            Model = "重复型号",
            Enabled = true,
        }, null, ct));
        Assert.Equal(409, duplicate.Status);
        var otherSupplierPartId = Id(await robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8002,
            PartNumber = "RP-001",
            Model = "其他供应商型号",
            Enabled = true,
        }, null, ct));

        var unicodeBoundaryId = Id(await robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8001,
            PartNumber = string.Concat(Enumerable.Repeat("🤖", 128)),
            Model = string.Concat(Enumerable.Repeat("型", 512)),
            Enabled = true,
        }, null, ct));
        await robotParts.DeleteAsync(conn, actor, unicodeBoundaryId, null, ct);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8001,
            PartNumber = string.Concat(Enumerable.Repeat("🤖", 129)),
            Model = "边界型号",
            Enabled = true,
        }, null, ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => robotParts.CreateAsync(conn, actor, new()
        {
            SupplierId = 8001,
            PartNumber = "MODEL-TOO-LONG",
            Model = string.Concat(Enumerable.Repeat("🤖", 513)),
            Enabled = true,
        }, null, ct))).Status);

        var complete = new ProjectUpsertRequest
        {
            Name = "必填校验项目",
            SupplierId = 8001,
            WorkOrderNos = ["WO-REQUIRED"],
            MachineModel = "M1",
            RobotPartId = partId,
            PriorityId = priorityId,
            RobotTypeId = robotTypeId,
            ExpectedCompletionDate = "2026-12-31",
            SubprojectNames = ["必填校验子项目"],
        };
        foreach (var field in new[] { "workOrderNos", "machineModel", "robotPartId", "priorityId", "robotTypeId", "expectedCompletionDate" })
        {
            var node = JsonSerializer.SerializeToNode(complete)!;
            node[field] = null;
            var missing = JsonSerializer.Deserialize<ProjectUpsertRequest>(node)!;
            var error = await Assert.ThrowsAsync<ApiException>(() => groups.CreateAsync(conn, actor, missing, null, ct));
            Assert.Equal(400, error.Status);
        }

        // A priority id is a dictionary entry of the wrong type for robotTypeId.
        var notRobotType = CopyRequest(complete, name: "类型不匹配项目", robotTypeId: priorityId);
        var notRobotTypeError = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, actor, notRobotType, null, ct));
        Assert.Equal(400, notRobotTypeError.Status);
        Assert.Equal("Robot 类型不存在或类型不匹配", notRobotTypeError.Message);

        var wrongSupplier = CopyRequest(complete, otherSupplierPartId, "供应商不匹配项目");
        var wrongSupplierError = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, actor, wrongSupplier, null, ct));
        Assert.Equal(400, wrongSupplierError.Status);
        Assert.Equal("Robot 料号不属于所选供应商", wrongSupplierError.Message);

        var created = await groups.CreateAsync(conn, actor, new()
        {
            Name = "元数据集成项目",
            Description = "初始",
            SupplierId = 8001,
            WorkOrderNos = [" WO-002 ", "", "wo-002", "WO-001"],
            MachineModel = " 机型-X ",
            RobotPartId = partId,
            PriorityId = priorityId,
            RobotTypeId = robotTypeId,
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
            Assert.Equal(partId, root.GetProperty("robotPartId").GetUInt64());
            Assert.Equal("RP-001", root.GetProperty("robotPartNumber").GetString());
            Assert.Equal("Robot Model A", root.GetProperty("robotModelName").GetString());
            Assert.Equal(robotTypeId, root.GetProperty("robotTypeId").GetUInt64());
            Assert.Equal("测试类型", root.GetProperty("robotTypeName").GetString());
            Assert.Equal(actor.Id, root.GetProperty("responsibleUserId").GetUInt64());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("sectionId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("sectionName").ValueKind);
            Assert.False(root.TryGetProperty("robotVendorId", out _));
            Assert.False(root.TryGetProperty("robotVendorName", out _));
            Assert.False(root.TryGetProperty("robotModelId", out _));
            Assert.Equal("2026-12-31", root.GetProperty("expectedCompletionDate").GetString());
        }

        var disabled = await robotParts.UpdateAsync(conn, actor, partId, new()
        {
            SupplierId = 8001,
            PartNumber = "RP-001",
            Model = "Robot Model A",
            SortNo = 20,
            Enabled = false,
        }, null, ct);
        using (var response = Json(disabled))
        {
            Assert.False(response.RootElement.GetProperty("enabled").GetBoolean());
            Assert.True(response.RootElement.GetProperty("inUse").GetBoolean());
            Assert.Equal("Robot Model A", response.RootElement.GetProperty("model").GetString());
        }
        Assert.Empty(await robotParts.ListAsync(conn, actor, 8001, enabledOnly: true, ct));
        Assert.True(Assert.Single(await robotParts.ListAsync(conn, actor, 8001, enabledOnly: false, ct)).InUse);

        var updated = await groups.UpdateAsync(conn, actor, groupId, new()
        {
            Name = "元数据集成项目",
            Description = "仅修改说明",
            SupplierId = 8001,
            WorkOrderNos = ["WO-002", "WO-001"],
            MachineModel = "机型-X",
            RobotPartId = partId,
            PriorityId = priorityId,
            RobotTypeId = robotTypeId,
            ExpectedCompletionDate = "2026-12-31",
        }, null, ct);
        using (var updatedJson = Json(updated))
        {
            Assert.Equal("仅修改说明", updatedJson.RootElement.GetProperty("description").GetString());
            Assert.Equal("RP-001", updatedJson.RootElement.GetProperty("robotPartNumber").GetString());
            Assert.Equal("Robot Model A", updatedJson.RootElement.GetProperty("robotModelName").GetString());
            Assert.Equal(actor.Id, updatedJson.RootElement.GetProperty("responsibleUserId").GetUInt64());
            Assert.Equal(JsonValueKind.Null, updatedJson.RootElement.GetProperty("sectionId").ValueKind);
        }

        var disabledForNewProject = CopyRequest(complete, name: "停用料号新项目");
        var disabledError = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, actor, disabledForNewProject, null, ct));
        Assert.Equal(400, disabledError.Status);
        Assert.Equal("Robot 料号已停用", disabledError.Message);
        var immutable = await Assert.ThrowsAsync<ApiException>(() => robotParts.UpdateAsync(conn, actor, partId, new()
        {
            SupplierId = 8001,
            PartNumber = "RP-001",
            Model = "Robot Model A2",
            Enabled = false,
        }, null, ct));
        Assert.Equal(409, immutable.Status);
        Assert.Equal("Robot 料号已被项目引用，不能更换供应商、料号或型号", immutable.Message);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            robotParts.DeleteAsync(conn, actor, partId, null, ct))).Status);

        // A referenced robot type can be disabled but not deleted; a disabled one is refused for new projects.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            dictionaries.DeleteAsync(conn, actor, robotTypeId, null, ct))).Status);
        await dictionaries.UpdateAsync(conn, actor, robotTypeId, new()
        {
            Type = "ROBOT_TYPE",
            Name = "测试类型",
            SortNo = 10,
            Enabled = false,
        }, null, ct);
        var disabledRobotType = await Assert.ThrowsAsync<ApiException>(() =>
            groups.CreateAsync(conn, actor, CopyRequest(complete, name: "停用类型新项目"), null, ct));
        Assert.Equal(400, disabledRobotType.Status);
        Assert.Equal("Robot 类型已停用", disabledRobotType.Message);

        var oldDictionaryType = await Assert.ThrowsAsync<ApiException>(() => dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "ROBOT_MODEL",
            Name = "旧字典型号",
            Enabled = true,
        }, null, ct));
        Assert.Equal(400, oldDictionaryType.Status);
        var parentedPriority = await Assert.ThrowsAsync<ApiException>(() => dictionaries.CreateAsync(conn, actor, new()
        {
            Type = "PRIORITY",
            Name = "带上级优先级",
            ParentId = priorityId,
            Enabled = true,
        }, null, ct));
        Assert.Equal(400, parentedPriority.Status);
    }

    private static ProjectUpsertRequest CopyRequest(
        ProjectUpsertRequest source, ulong? robotPartId = null, string? name = null, ulong? robotTypeId = null) => new()
    {
        Name = name ?? source.Name,
        Description = source.Description,
        SupplierId = source.SupplierId,
        WorkOrderNos = source.WorkOrderNos,
        MachineModel = source.MachineModel,
        RobotPartId = robotPartId ?? source.RobotPartId,
        PriorityId = source.PriorityId,
        RobotTypeId = robotTypeId ?? source.RobotTypeId,
        ExpectedCompletionDate = source.ExpectedCompletionDate,
        SubprojectNames = source.SubprojectNames,
    };

    private static ulong Id(object value)
    {
        using var json = Json(value);
        return json.RootElement.GetProperty("id").GetUInt64();
    }

    private static JsonDocument Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));
}
