using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectGroupService(
    AuditService audit,
    ProjectGroupStatusService groupStatus)
{
    internal async Task<object> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        string? keyword,
        string? status,
        ulong? supplierId,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var (scope, args) = await ProjectGroupAccessService.VisibleScopeAsync(conn, tx, current, ct);
        var clauses = new List<string> { scope };
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add("g.name LIKE CONCAT('%',@Keyword,'%')");
            args.Add("Keyword", keyword.Trim());
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            clauses.Add("g.status=@Status");
            args.Add("Status", status.Trim());
        }
        if (supplierId is not null)
        {
            clauses.Add("g.supplier_id=@SupplierId");
            args.Add("SupplierId", supplierId.Value);
        }
        args.Add("UserId", current.Id);
        args.Add("Offset", (actualPage - 1) * size);
        args.Add("Size", size);
        var where = string.Join(" AND ", clauses);
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"SELECT COUNT(*) FROM project_groups g WHERE {where}", args, tx, cancellationToken: ct));
        var rows = (await conn.QueryAsync<ProjectGroupRow>(new CommandDefinition(
            GroupSelect + $" WHERE {where} ORDER BY g.id DESC LIMIT @Size OFFSET @Offset",
            args, tx, cancellationToken: ct))).AsList();
        await LoadWorkOrdersAsync(conn, tx, rows, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(rows.Select(ProjectJson.ProjectGroup).ToArray(), total, actualPage, size);
    }

    internal async Task<object> DetailAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, false, ct);
        var group = await LoadGroupAsync(conn, tx, groupId, current.Id, ct);
        var projects = (await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            ChildSelect + " WHERE p.project_group_id=@GroupId ORDER BY p.id",
            new { GroupId = groupId, UserId = current.Id }, tx, cancellationToken: ct))).AsList();
        await ProjectService.LoadWorkOrdersAsync(conn, tx, projects, ct);
        await tx.CommitAsync(ct);
        return new
        {
            group = ProjectJson.ProjectGroup(group),
            projects = projects.Select(ProjectJson.Project).ToArray(),
        };
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForCreate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        var childNames = NormalizeSubprojectNames(request.SubprojectNames);
        var metadata = ProjectService.NormalizeMetadata(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await EnsureSupplierAsync(conn, tx, request.SupplierId, ct);
        await EnsureGroupNameUniqueAsync(conn, tx, name, null, ct);
        foreach (var childName in childNames) await ProjectService.EnsureNameUniqueAsync(conn, tx, childName, null, ct);
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, null, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_groups(name,description,supplier_id,status,created_by,machine_model,robot_vendor_id,
                robot_model_id,responsible_user_id,section_id,priority_id,expected_completion_date,completed_at,created_at,updated_at)
            VALUES(@Name,@Description,@SupplierId,'DRAFT',@CreatedBy,@MachineModel,@RobotVendorId,@RobotModelId,
                @ResponsibleUserId,@SectionId,@PriorityId,@ExpectedCompletionDate,NULL,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
            """, new { Name = name, request.Description, request.SupplierId, CreatedBy = current.Id,
                metadata.MachineModel, metadata.RobotVendorId, metadata.RobotModelId, metadata.ResponsibleUserId,
                metadata.SectionId, metadata.PriorityId, metadata.ExpectedCompletionDate }, tx, cancellationToken: ct));
        var groupId = await LastInsertIdAsync(conn, tx, ct);
        await ReplaceGroupWorkOrdersAsync(conn, tx, groupId, metadata.WorkOrderNos, ct);
        var childIds = new List<ulong>(childNames.Length);
        foreach (var childName in childNames)
            childIds.Add(await InsertChildAsync(conn, tx, groupId, childName, null, request.SupplierId, current.Id, metadata, ct));
        for (var index = 0; index < childIds.Count; index++)
            await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", childIds[index], new
            {
                name = childNames[index],
                projectGroupId = groupId,
                projectGroupName = name,
                inheritedFromMainProject = true,
            }, ip, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_group_status_logs(project_group_id,from_status,to_status,action,trigger_project_id,operator_id,created_at)
            VALUES(@GroupId,NULL,'DRAFT','CREATE',@TriggerProjectId,@UserId,UTC_TIMESTAMP(3))
            """, new { GroupId = groupId, TriggerProjectId = childIds.FirstOrDefault(), UserId = current.Id }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_CREATE", "project_group", groupId, new
        {
            name,
            request.SupplierId,
            metadata.WorkOrderNos,
            metadata.MachineModel,
            metadata.RobotVendorId,
            metadata.RobotModelId,
            metadata.ResponsibleUserId,
            metadata.SectionId,
            metadata.PriorityId,
            metadata.ExpectedCompletionDate,
            subprojectIds = childIds,
            subprojectNames = childNames,
        }, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(conn, tx, groupId, current.Id, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> UpdateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForUpdate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        var metadata = ProjectService.NormalizeMetadata(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        var access = await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:update", ct);
        if (access.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能修改公共资料");
        if (access.SupplierId != request.SupplierId)
            throw ApiException.BadRequest("主项目创建后不可更换供应商");
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM projects WHERE project_group_id=@GroupId AND status='PENDING_CONFIRMATION')",
                new { GroupId = groupId }, tx, cancellationToken: ct)))
            throw ApiException.Conflict("存在待验收子项目，暂不能修改主项目资料");
        var before = await LoadGroupAsync(conn, tx, groupId, current.Id, ct);
        await EnsureGroupNameUniqueAsync(conn, tx, name, groupId, ct);
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, new ProjectRow
        {
            RobotVendorId = before.RobotVendorId,
            RobotModelId = before.RobotModelId,
            PriorityId = before.PriorityId,
        }, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE project_groups SET name=@Name,description=@Description,machine_model=@MachineModel,
                robot_vendor_id=@RobotVendorId,robot_model_id=@RobotModelId,responsible_user_id=@ResponsibleUserId,
                section_id=@SectionId,priority_id=@PriorityId,expected_completion_date=@ExpectedCompletionDate,
                updated_at=UTC_TIMESTAMP(3)
            WHERE id=@GroupId
            """, new { Name = name, request.Description, metadata.MachineModel, metadata.RobotVendorId,
                metadata.RobotModelId, metadata.ResponsibleUserId, metadata.SectionId, metadata.PriorityId,
                metadata.ExpectedCompletionDate, GroupId = groupId }, tx, cancellationToken: ct));
        var childIds = (await conn.QueryAsync<ulong>(new CommandDefinition(
            """
            SELECT id FROM projects
            WHERE project_group_id=@GroupId
              AND (status<>'COMPLETED'
                   OR NOT(responsible_user_id <=> @ResponsibleUserId)
                   OR NOT(section_id <=> @SectionId))
            ORDER BY id
            """, new { GroupId = groupId, metadata.ResponsibleUserId, metadata.SectionId },
            tx, cancellationToken: ct))).ToArray();
        // Access ownership always follows the main project, including completed
        // children. Accepted business metadata and work orders remain frozen.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE projects SET responsible_user_id=@ResponsibleUserId,section_id=@SectionId,
                updated_at=CASE
                    WHEN NOT(responsible_user_id <=> @ResponsibleUserId) OR NOT(section_id <=> @SectionId)
                    THEN UTC_TIMESTAMP(3) ELSE updated_at END
            WHERE project_group_id=@GroupId
            """, new { metadata.ResponsibleUserId, metadata.SectionId, GroupId = groupId },
            tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE projects SET machine_model=@MachineModel,robot_vendor_id=@RobotVendorId,robot_model_id=@RobotModelId,
                responsible_user_id=@ResponsibleUserId,section_id=@SectionId,priority_id=@PriorityId,
                expected_completion_date=@ExpectedCompletionDate,updated_at=UTC_TIMESTAMP(3)
            WHERE project_group_id=@GroupId AND status<>'COMPLETED'
            """, new { metadata.MachineModel, metadata.RobotVendorId, metadata.RobotModelId,
                metadata.ResponsibleUserId, metadata.SectionId, metadata.PriorityId, metadata.ExpectedCompletionDate,
                GroupId = groupId }, tx, cancellationToken: ct));
        await ReplaceGroupWorkOrdersAsync(conn, tx, groupId, metadata.WorkOrderNos, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE pwo FROM project_work_orders pwo INNER JOIN projects p ON p.id=pwo.project_id WHERE p.project_group_id=@GroupId AND p.status<>'COMPLETED'",
            new { GroupId = groupId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_work_orders(project_id,work_order_no,sort_no,created_at)
            SELECT p.id,gwo.work_order_no,gwo.sort_no,UTC_TIMESTAMP(3)
            FROM projects p CROSS JOIN project_group_work_orders gwo
            WHERE p.project_group_id=@GroupId AND p.status<>'COMPLETED' AND gwo.project_group_id=@GroupId
            """, new { GroupId = groupId }, tx, cancellationToken: ct));
        foreach (var childId in childIds)
            await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", childId, new
            {
                projectGroupId = groupId,
                inheritedFromMainProject = true,
                changedByMainProject = true,
            }, ip, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_UPDATE", "project_group", groupId, new
        {
            name,
            changes = AuditChange.OnlyChanged(
                new("name", "主项目名称", before.Name, name),
                new("description", "项目说明", before.Description, request.Description),
                new("workOrderNos", "工令号", before.WorkOrderNos, metadata.WorkOrderNos),
                new("machineModel", "机型", before.MachineModel, metadata.MachineModel),
                new("robotVendorId", "Robot 厂商", before.RobotVendorId, metadata.RobotVendorId),
                new("robotModelId", "Robot 型号", before.RobotModelId, metadata.RobotModelId),
                new("responsibleUserId", "负责人", before.ResponsibleUserId, metadata.ResponsibleUserId),
                new("sectionId", "课别", before.SectionId, metadata.SectionId),
                new("priorityId", "优先级", before.PriorityId, metadata.PriorityId),
                new("expectedCompletionDate", "预计完成日期", before.ExpectedCompletionDate, metadata.ExpectedCompletionDate)),
        }, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(conn, tx, groupId, current.Id, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> CreateSubprojectAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        SubprojectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForCreate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        var access = await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        if (access.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能新增子项目");
        await ProjectService.EnsureNameUniqueAsync(conn, tx, name, null, ct);
        var group = await LoadGroupAsync(conn, tx, groupId, current.Id, ct);
        var metadata = Metadata(group);
        var projectId = await InsertChildAsync(conn, tx, groupId, name, request.Description, group.SupplierId, current.Id, metadata, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", projectId, new
        {
            name,
            projectGroupId = groupId,
            projectGroupName = group.Name,
            inheritedFromMainProject = true,
        }, ip, ct);
        await groupStatus.RecalculateAsync(conn, tx, groupId, current.Id, projectId, ct);
        var result = ProjectJson.Project(await LoadChildAsync(conn, tx, projectId, current.Id, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:delete", ct);
        var group = await LoadGroupAsync(conn, tx, groupId, current.Id, ct);
        if (group.SubprojectCount > 0) throw ApiException.Conflict("主项目仍有子项目，请先逐个处理子项目");
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_DELETE", "project_group", groupId,
            new { name = group.Name }, ip, ct);
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_groups WHERE id=@GroupId", new { GroupId = groupId }, tx, cancellationToken: ct));
        if (deleted != 1) throw ApiException.Conflict("主项目已被删除，请刷新后重试");
        await tx.CommitAsync(ct);
    }

    private static async Task<ulong> InsertChildAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong groupId,
        string name,
        string? description,
        ulong supplierId,
        ulong actorId,
        ProjectService.ProjectMetadataInput metadata,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects(project_group_id,name,description,supplier_id,status,confirm_side,created_by,created_at,updated_at,
                machine_model,robot_vendor_id,robot_model_id,responsible_user_id,section_id,priority_id,expected_completion_date)
            VALUES(@GroupId,@Name,@Description,@SupplierId,'DRAFT',NULL,@ActorId,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),
                @MachineModel,@RobotVendorId,@RobotModelId,@ResponsibleUserId,@SectionId,@PriorityId,@ExpectedCompletionDate)
            """, new { GroupId = groupId, Name = name, Description = description, SupplierId = supplierId, ActorId = actorId,
                metadata.MachineModel, metadata.RobotVendorId, metadata.RobotModelId, metadata.ResponsibleUserId,
                metadata.SectionId, metadata.PriorityId, metadata.ExpectedCompletionDate }, tx, cancellationToken: ct));
        var projectId = await LastInsertIdAsync(conn, tx, ct);
        await ProjectService.ReplaceWorkOrdersAsync(conn, tx, projectId, metadata.WorkOrderNos, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at)
            VALUES(@ProjectId,NULL,'DRAFT','CREATE',@ActorId,NULL,NULL,UTC_TIMESTAMP(3))
            """, new { ProjectId = projectId, ActorId = actorId }, tx, cancellationToken: ct));
        return projectId;
    }

    private static async Task<ProjectGroupRow> LoadGroupAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong groupId,
        ulong userId,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<ProjectGroupRow>(new CommandDefinition(
            GroupSelect + " WHERE g.id=@GroupId",
            new { GroupId = groupId, UserId = userId }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        await LoadWorkOrdersAsync(conn, tx, [row], ct);
        return row;
    }

    private static async Task<ProjectRow> LoadChildAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        ulong userId,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            ChildSelect + " WHERE p.id=@ProjectId",
            new { ProjectId = projectId, UserId = userId }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound();
        await ProjectService.LoadWorkOrdersAsync(conn, tx, [row], ct);
        return row;
    }

    private static async Task LoadWorkOrdersAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        IReadOnlyCollection<ProjectGroupRow> groups,
        CancellationToken ct)
    {
        if (groups.Count == 0) return;
        var lookup = (await conn.QueryAsync<GroupWorkOrderRow>(new CommandDefinition(
            """
            SELECT project_group_id AS ProjectGroupId,work_order_no AS WorkOrderNo
            FROM project_group_work_orders WHERE project_group_id IN @Ids
            ORDER BY project_group_id,sort_no,id
            """, new { Ids = groups.Select(group => group.Id).ToArray() }, tx, cancellationToken: ct)))
            .GroupBy(row => row.ProjectGroupId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        foreach (var group in groups) group.WorkOrderNos = lookup.GetValueOrDefault(group.Id) ?? [];
    }

    private static async Task ReplaceGroupWorkOrdersAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong groupId,
        string[] values,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_group_work_orders WHERE project_group_id=@GroupId",
            new { GroupId = groupId }, tx, cancellationToken: ct));
        for (var index = 0; index < values.Length; index++)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no,created_at)
                VALUES(@GroupId,@Value,@SortNo,UTC_TIMESTAMP(3))
                """, new { GroupId = groupId, Value = values[index], SortNo = index }, tx, cancellationToken: ct));
    }

    private static async Task EnsureSupplierAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong supplierId,
        CancellationToken ct)
    {
        var status = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM suppliers WHERE id=@SupplierId",
            new { SupplierId = supplierId }, tx, cancellationToken: ct));
        if (status is null) throw ApiException.BadRequest("供应商不存在");
        if (status != "ACTIVE") throw ApiException.BadRequest("供应商已被禁用");
    }

    private static async Task EnsureGroupNameUniqueAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string name,
        ulong? excludeId,
        CancellationToken ct)
    {
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM project_groups WHERE name=@Name AND (@ExcludeId IS NULL OR id<>@ExcludeId))",
                new { Name = name, ExcludeId = excludeId }, tx, cancellationToken: ct)))
            throw ApiException.Conflict("主项目名称已存在");
    }

    private static string[] NormalizeSubprojectNames(string?[]? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (name.EnumerateRunes().Count() > 128) throw ApiException.BadRequest("子项目名称不能超过 128 个字符");
            if (seen.Add(name)) result.Add(name);
        }
        if (result.Count == 0) throw ApiException.BadRequest("请至少创建一个子项目");
        if (result.Count > 50) throw ApiException.BadRequest("单个主项目最多创建 50 个子项目");
        return result.ToArray();
    }

    private static ProjectService.ProjectMetadataInput Metadata(ProjectGroupRow group) => new(
        group.WorkOrderNos,
        group.MachineModel,
        group.RobotVendorId,
        group.RobotModelId,
        group.ResponsibleUserId,
        group.SectionId,
        group.PriorityId,
        group.ExpectedCompletionDate);

    private static Task<ulong> LastInsertIdAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));

    private const string GroupSelect = """
        SELECT g.id AS Id,g.name AS Name,g.description AS Description,g.supplier_id AS SupplierId,
               supplier.name AS SupplierName,g.status AS Status,g.created_by AS CreatedBy,
               creator.real_name AS CreatedByName,g.machine_model AS MachineModel,
               g.robot_vendor_id AS RobotVendorId,rv.name AS RobotVendorName,
               g.robot_model_id AS RobotModelId,rm.name AS RobotModelName,
               g.responsible_user_id AS ResponsibleUserId,owner.employee_no AS ResponsibleUserEmployeeNo,
               owner.real_name AS ResponsibleUserName,g.section_id AS SectionId,section.name AS SectionName,
               g.priority_id AS PriorityId,priority.name AS PriorityName,
               g.expected_completion_date AS ExpectedCompletionDate,g.completed_at AS CompletedAt,
               g.created_at AS CreatedAt,g.updated_at AS UpdatedAt,
               (SELECT COUNT(*) FROM projects p WHERE p.project_group_id=g.id) AS SubprojectCount,
               (SELECT COUNT(*) FROM projects p WHERE p.project_group_id=g.id AND p.status='COMPLETED') AS CompletedCount,
               (SELECT COUNT(*) FROM projects p WHERE p.project_group_id=g.id AND p.status='PENDING_CONFIRMATION') AS PendingCount,
               (SELECT COUNT(*) FROM projects p WHERE p.project_group_id=g.id AND p.status='TERMINATED') AS TerminatedCount,
               (SELECT COUNT(*) FROM messages message
                INNER JOIN projects p ON p.id=message.project_id
                WHERE p.project_group_id=g.id AND message.status='NORMAL' AND message.sender_id<>@UserId
                  AND NOT EXISTS(SELECT 1 FROM message_reads receipt WHERE receipt.message_id=message.id AND receipt.user_id=@UserId)) AS UnreadMessages
        FROM project_groups g
        LEFT JOIN suppliers supplier ON supplier.id=g.supplier_id
        LEFT JOIN users creator ON creator.id=g.created_by
        LEFT JOIN project_dictionaries rv ON rv.id=g.robot_vendor_id
        LEFT JOIN project_dictionaries rm ON rm.id=g.robot_model_id
        LEFT JOIN users owner ON owner.id=g.responsible_user_id
        LEFT JOIN departments section ON section.id=g.section_id AND section.kind='SECTION'
        LEFT JOIN project_dictionaries priority ON priority.id=g.priority_id
        """;

    private const string ChildSelect = """
        SELECT p.id AS Id,p.project_group_id AS ProjectGroupId,g.name AS ProjectGroupName,
               p.name AS Name,p.description AS Description,p.supplier_id AS SupplierId,
               supplier.name AS SupplierName,p.status AS Status,p.confirm_side AS ConfirmSide,
               p.created_by AS CreatedBy,creator.real_name AS CreatedByName,
               p.machine_model AS MachineModel,p.robot_vendor_id AS RobotVendorId,rv.name AS RobotVendorName,
               p.robot_model_id AS RobotModelId,rm.name AS RobotModelName,
               p.responsible_user_id AS ResponsibleUserId,owner.employee_no AS ResponsibleUserEmployeeNo,
               owner.real_name AS ResponsibleUserName,p.section_id AS SectionId,section.name AS SectionName,
               p.priority_id AS PriorityId,priority.name AS PriorityName,
               CASE WHEN p.status='PENDING_CONFIRMATION' THEN (
                   SELECT MAX(psl.id) FROM project_status_logs psl
                   WHERE psl.project_id=p.id AND psl.action='SUBMIT'
               ) ELSE NULL END AS LatestSubmissionId,
               p.expected_completion_date AS ExpectedCompletionDate,p.created_at AS CreatedAt,p.updated_at AS UpdatedAt,
               EXISTS(SELECT 1 FROM project_copies copy WHERE copy.source_project_id=p.id OR copy.target_project_id=p.id) AS HasCopyHistory,
               (SELECT COUNT(*) FROM messages message WHERE message.project_id=p.id AND message.status='NORMAL'
                  AND message.sender_id<>@UserId
                  AND NOT EXISTS(SELECT 1 FROM message_reads receipt WHERE receipt.message_id=message.id AND receipt.user_id=@UserId)) AS UnreadMessages
        FROM projects p
        INNER JOIN project_groups g ON g.id=p.project_group_id
        LEFT JOIN suppliers supplier ON supplier.id=p.supplier_id
        LEFT JOIN users creator ON creator.id=p.created_by
        LEFT JOIN project_dictionaries rv ON rv.id=p.robot_vendor_id
        LEFT JOIN project_dictionaries rm ON rm.id=p.robot_model_id
        LEFT JOIN users owner ON owner.id=p.responsible_user_id
        LEFT JOIN departments section ON section.id=p.section_id AND section.kind='SECTION'
        LEFT JOIN project_dictionaries priority ON priority.id=p.priority_id
        """;

    private sealed class GroupWorkOrderRow
    {
        public ulong ProjectGroupId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
    }
}
