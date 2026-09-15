using System.Text;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectService(
    AuditService audit,
    AppOptions options)
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
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var clauses = new List<string>();
        var args = new DynamicParameters();
        if (!current.IsInternal)
        {
            if (current.SupplierId is null)
            {
                throw ApiException.OutOfScope();
            }
            clauses.Add("p.supplier_id = @ActorSupplierId");
            args.Add("ActorSupplierId", current.SupplierId.Value);
        }
        var canViewAll = false;
        if (current.IsInternal)
            canViewAll = await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct);
        if (current.IsInternal && !canViewAll)
        {
            clauses.Add("p.responsible_user_id = @ActorId");
            args.Add("ActorId", current.Id);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add("p.name LIKE CONCAT('%', @Keyword, '%')");
            args.Add("Keyword", keyword.Trim());
        }
        if (status is not null)
        {
            clauses.Add("p.status = @Status");
            args.Add("Status", status);
        }
        if (supplierId is not null)
        {
            clauses.Add("p.supplier_id = @SupplierId");
            args.Add("SupplierId", supplierId.Value);
        }
        var where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects p" + where,
            args,
            tx,
            cancellationToken: ct));
        args.Add("Offset", (actualPage - 1) * size);
        args.Add("Size", size);
        var rows = (await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.name AS Name, p.description AS Description,p.machine_model AS MachineModel,
                   p.supplier_id AS SupplierId, p.status AS Status, p.confirm_side AS ConfirmSide,
                   p.robot_vendor_id AS RobotVendorId,rv.code AS RobotVendorCode,rv.name AS RobotVendorName,
                   p.robot_model_id AS RobotModelId,rm.code AS RobotModelCode,rm.name AS RobotModelName,
                   p.responsible_user_id AS ResponsibleUserId,owner.employee_no AS ResponsibleUserEmployeeNo,
                   owner.real_name AS ResponsibleUserName,p.section_id AS SectionId,section.name AS SectionName,
                   p.priority_id AS PriorityId,priority.code AS PriorityCode,priority.name AS PriorityName,
                   p.expected_completion_date AS ExpectedCompletionDate,
                   CASE WHEN p.status='PENDING_CONFIRMATION' THEN (
                       SELECT MAX(psl.id) FROM project_status_logs psl
                       WHERE psl.project_id=p.id AND psl.action='SUBMIT'
                   ) ELSE NULL END AS LatestSubmissionId,
                   p.created_by AS CreatedBy, p.created_at AS CreatedAt, p.updated_at AS UpdatedAt,
                   s.name AS SupplierName, u.real_name AS CreatedByName
            FROM projects p
            LEFT JOIN suppliers s ON s.id = p.supplier_id
            LEFT JOIN users u ON u.id = p.created_by
            LEFT JOIN project_dictionaries rv ON rv.id=p.robot_vendor_id
            LEFT JOIN project_dictionaries rm ON rm.id=p.robot_model_id
            LEFT JOIN users owner ON owner.id=p.responsible_user_id
            LEFT JOIN departments section ON section.id=p.section_id AND section.kind='SECTION'
            LEFT JOIN project_dictionaries priority ON priority.id=p.priority_id
            """ + where + " ORDER BY p.id DESC LIMIT @Size OFFSET @Offset",
            args,
            tx,
            cancellationToken: ct))).AsList();
        await LoadWorkOrdersAsync(conn, tx, rows, ct);
        await LoadCopyLineageAsync(conn, tx, rows, current, canViewAll, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(rows.Select(ProjectJson.Project).ToArray(), total, actualPage, size);
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        var name = ValidateNameForCreate(request.Name);
        ValidateDescription(request.Description);
        var metadata = NormalizeMetadata(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        var supplierStatus = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM suppliers WHERE id = @SupplierId",
            new { request.SupplierId },
            tx,
            cancellationToken: ct));
        if (supplierStatus is null)
        {
            throw ApiException.BadRequest("供应商不存在");
        }
        if (supplierStatus != "ACTIVE")
        {
            throw ApiException.BadRequest("供应商已被禁用");
        }
        await EnsureNameUniqueAsync(conn, tx, name, null, ct);
        metadata = await ValidateMetadataAsync(conn, tx, metadata, null, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO projects(name,description,supplier_id,status,confirm_side,created_by,created_at,updated_at,
                    machine_model,robot_vendor_id,robot_model_id,responsible_user_id,section_id,priority_id,expected_completion_date)
                VALUES(@Name,@Description,@SupplierId,'DRAFT',NULL,@CreatedBy,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),
                    @MachineModel,@RobotVendorId,@RobotModelId,@ResponsibleUserId,@SectionId,@PriorityId,@ExpectedCompletionDate)
                """,
                new { Name = name, request.Description, request.SupplierId, CreatedBy = current.Id,
                    metadata.MachineModel, metadata.RobotVendorId, metadata.RobotModelId, metadata.ResponsibleUserId,
                    metadata.SectionId, metadata.PriorityId, metadata.ExpectedCompletionDate },
                tx,
                cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("项目名称已存在");
        }
        var projectId = await LastInsertIdAsync(conn, tx, ct);
        await ReplaceWorkOrdersAsync(conn, tx, projectId, metadata.WorkOrderNos, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at)
            VALUES(@ProjectId,NULL,'DRAFT','CREATE',@UserId,NULL,NULL,UTC_TIMESTAMP(3))
            """,
            new { ProjectId = projectId, UserId = current.Id },
            tx,
            cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", projectId, new
        {
            name,
            metadata.WorkOrderNos,
            metadata.MachineModel,
            metadata.RobotVendorId,
            metadata.RobotModelId,
            metadata.ResponsibleUserId,
            metadata.SectionId,
            metadata.PriorityId,
            metadata.ExpectedCompletionDate,
        }, ip, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> DetailAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        var latest = await conn.QuerySingleOrDefaultAsync<ProjectDetailHistory>(new CommandDefinition(
            """
            SELECT
              (SELECT reason FROM project_status_logs WHERE project_id=@ProjectId AND action='REJECT' ORDER BY id DESC LIMIT 1) AS RejectReason,
              (SELECT operator_id FROM project_status_logs WHERE project_id=@ProjectId AND action='SUBMIT' ORDER BY id DESC LIMIT 1) AS LatestSubmitterId
            """,
            new { ProjectId = projectId },
            tx,
            cancellationToken: ct));
        var hasCopyHistory = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM project_copies WHERE source_project_id=@ProjectId OR target_project_id=@ProjectId)",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        var sourceCopy = await conn.QuerySingleOrDefaultAsync<ProjectCopySourceRow>(new CommandDefinition(
            """
            SELECT pc.source_project_id AS ProjectId,source.name AS Name
            FROM project_copies pc JOIN projects source ON source.id=pc.source_project_id
            WHERE pc.target_project_id=@ProjectId
            """, new { ProjectId = projectId }, tx, cancellationToken: ct));
        object? copySource = null;
        if (sourceCopy is not null)
        {
            try
            {
                await ProjectAccessService.RequireViewForValidatedActorAsync(
                    conn, tx, current, sourceCopy.ProjectId, false, ct);
                copySource = new { projectId = sourceCopy.ProjectId, name = sourceCopy.Name };
            }
            catch (ApiException error) when (error.Status is 403 or 404)
            {
                copySource = null;
            }
        }
        var result = new
        {
            id = project.Id,
            name = project.Name,
            description = project.Description,
            supplierId = project.SupplierId,
            supplierName = project.SupplierName,
            status = project.Status,
            confirmSide = project.ConfirmSide,
            createdBy = project.CreatedBy,
            createdByName = project.CreatedByName,
            createdAt = ProjectJson.Utc(project.CreatedAt),
            updatedAt = ProjectJson.Utc(project.UpdatedAt),
            rejectReason = latest?.RejectReason,
            latestSubmitterId = latest?.LatestSubmitterId,
            latestSubmissionId = project.LatestSubmissionId,
            workOrderNos = project.WorkOrderNos,
            machineModel = project.MachineModel,
            robotVendorId = project.RobotVendorId,
            robotVendorCode = project.RobotVendorCode,
            robotVendorName = project.RobotVendorName,
            robotModelId = project.RobotModelId,
            robotModelCode = project.RobotModelCode,
            robotModelName = project.RobotModelName,
            responsibleUserId = project.ResponsibleUserId,
            responsibleUserEmployeeNo = project.ResponsibleUserEmployeeNo,
            responsibleUserName = project.ResponsibleUserName,
            sectionId = project.SectionId,
            sectionName = project.SectionName,
            priorityId = project.PriorityId,
            priorityCode = project.PriorityCode,
            priorityName = project.PriorityName,
            expectedCompletionDate = project.ExpectedCompletionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            hasCopyHistory,
            copySource,
        };
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> UpdateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        var name = ValidateNameForUpdate(request.Name);
        ValidateDescription(request.Description);
        var metadata = NormalizeMetadata(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:update", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        if (project.Status is not (ProjectStatuses.Draft or ProjectStatuses.InProgress))
        {
            throw ApiException.Conflict("项目当前状态不可编辑");
        }
        if (project.SupplierId != request.SupplierId)
        {
            throw ApiException.BadRequest("项目创建后不可更换供应商；请新建项目以避免历史数据越权");
        }
        await EnsureNameUniqueAsync(conn, tx, name, projectId, ct);
        metadata = await ValidateMetadataAsync(conn, tx, metadata, project, ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE projects SET name=@Name,description=@Description,machine_model=@MachineModel,
                    robot_vendor_id=@RobotVendorId,robot_model_id=@RobotModelId,responsible_user_id=@ResponsibleUserId,
                    section_id=@SectionId,priority_id=@PriorityId,expected_completion_date=@ExpectedCompletionDate,
                    updated_at=UTC_TIMESTAMP(3) WHERE id=@ProjectId
                """,
                new { Name = name, request.Description, ProjectId = projectId, metadata.MachineModel,
                    metadata.RobotVendorId, metadata.RobotModelId, metadata.ResponsibleUserId, metadata.SectionId,
                    metadata.PriorityId, metadata.ExpectedCompletionDate },
                tx,
                cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("项目名称已存在");
        }
        await ReplaceWorkOrdersAsync(conn, tx, projectId, metadata.WorkOrderNos, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", projectId, new
        {
            name,
            changes = AuditChange.OnlyChanged(
                new("name", "项目名称", project.Name, name),
                new("description", "项目说明", project.Description, request.Description),
                new("workOrderNos", "工令号", project.WorkOrderNos, metadata.WorkOrderNos),
                new("machineModel", "机台机型", project.MachineModel, metadata.MachineModel),
                new("robotVendorId", "机器人厂商", project.RobotVendorId, metadata.RobotVendorId),
                new("robotModelId", "机器人型号", project.RobotModelId, metadata.RobotModelId),
                new("responsibleUserId", "负责人", project.ResponsibleUserId, metadata.ResponsibleUserId),
                new("sectionId", "课别", project.SectionId, metadata.SectionId),
                new("priorityId", "优先级", project.PriorityId, metadata.PriorityId),
                new("expectedCompletionDate", "预计完成日期", project.ExpectedCompletionDate, metadata.ExpectedCompletionDate)),
        }, ip, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> SetStatusAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectStatusRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:status", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        var requested = request.Status ?? string.Empty;
        var (to, action) = ProjectWorkflowRules.ManagementTransition(project.Status, requested);
        if (action == "TERMINATE" && await CountActiveUploadsAsync(conn, tx, projectId, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，不能终止");
        }
        await ApplyTransitionAsync(conn, tx, current, project, to, action, null, null, ip, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> SubmitAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectSubmitRequest request,
        string? ip,
        CancellationToken ct)
    {
        var side = ProjectWorkflowRules.NormalizeConfirmSide(request.ConfirmSide);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:submit", ct);
        if (project.Status != ProjectStatuses.InProgress)
        {
            throw ApiException.Conflict("只有进行中的项目可以提交验收");
        }
        var availableFiles = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM files WHERE project_id=@ProjectId AND status='AVAILABLE'",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        if (availableFiles == 0)
        {
            throw ApiException.Conflict("项目至少上传一个可用文件后才能提交验收");
        }
        if (await CountActiveUploadsAsync(conn, tx, projectId, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，不能提交验收");
        }
        var reviewers = await ProjectReviewerService.ListAsync(conn, tx, project, ct);
        if (reviewers.Count == 0)
        {
            throw ApiException.Conflict("项目没有可执行验收的公司内部用户，请为项目负责人配置验收权限，或配置具备全局查看权限的验收人员");
        }
        var submissionId = await ApplyTransitionAsync(
            conn, tx, current, project, ProjectStatuses.PendingConfirmation, "SUBMIT", side, null, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, "SUBMIT", side, null, null, current, options.WebBaseUrl, audit, ct);
        var submitted = await LoadProjectAsync(conn, tx, projectId, false, ct);
        if (submitted.LatestSubmissionId != submissionId)
        {
            throw new InvalidOperationException("待确认项目的提交版本与状态历史不一致");
        }
        var result = ProjectJson.Project(submitted);
        await tx.CommitAsync(ct);
        return result;
    }

    internal Task<object> ConfirmAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectDecisionRequest request,
        string? ip,
        CancellationToken ct) =>
        DecideAsync(conn, actor, projectId, "CONFIRM", RequireExpectedSubmissionId(request.ExpectedSubmissionId), null, ip, ct);

    internal async Task<object> RejectAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectRejectRequest request,
        string? ip,
        CancellationToken ct)
    {
        var expectedSubmissionId = RequireExpectedSubmissionId(request.ExpectedSubmissionId);
        ProjectWorkflowRules.RequireInternalDecisionActor(actor);
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw ApiException.BadRequest("驳回原因不能为空");
        }
        if (RuneCount(reason) > 500)
        {
            throw ApiException.BadRequest("驳回原因过长（最多 500 字）");
        }
        return await DecideAsync(conn, actor, projectId, "REJECT", expectedSubmissionId, reason, ip, ct);
    }

    internal async Task<object> WithdrawAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectDecisionRequest request,
        string? ip,
        CancellationToken ct)
    {
        var expectedSubmissionId = RequireExpectedSubmissionId(request.ExpectedSubmissionId);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:withdraw", ct);
        if (project.Status != ProjectStatuses.PendingConfirmation)
        {
            throw ApiException.Conflict("项目当前不在待确认状态");
        }
        var latestSubmit = await LatestSubmissionAsync(conn, tx, projectId, ct);
        EnsureExpectedSubmission(latestSubmit, expectedSubmissionId);
        var privileged = current.IsInternal
            && await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct);
        if (latestSubmit.OperatorId != current.Id && !privileged)
        {
            throw ApiException.Forbidden();
        }
        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.InProgress, "WITHDRAW", project.ConfirmSide, null, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, "WITHDRAW", project.ConfirmSide, null, latestSubmit.OperatorId, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> SummaryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await ProjectAccessService.RequireViewForValidatedActorAsync(
            conn, tx, current, projectId, true, ct);
        var unread = await MessageService.UnreadCountAsync(conn, tx, current.Id, projectId, ct);
        var activityRevision = await ProjectActivityService.RevisionAsync(conn, tx, projectId, ct);
        var canConfirm = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct));
        var latestSubmissionId = project.Status == ProjectStatuses.PendingConfirmation
            ? (await LatestSubmissionAsync(conn, tx, projectId, ct)).Id
            : (ulong?)null;
        var result = new
        {
            unreadMessages = unread,
            activityRevision,
            pendingConfirmation = canConfirm
                && project.Status == ProjectStatuses.PendingConfirmation
                && project.ConfirmSide == ProjectWorkflowRules.InternalAcceptanceSide,
            latestSubmissionId,
        };
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<object> SupplierOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        var canListAll = await ProjectAccessService.HasPermissionAsync(
            conn, tx, current.Id, "project:create", ct)
            || await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            canListAll
                ? "SELECT id, name FROM suppliers WHERE status='ACTIVE' ORDER BY id"
                : """
                  SELECT DISTINCT s.id,s.name
                  FROM suppliers s
                  INNER JOIN projects p ON p.supplier_id=s.id
                  WHERE s.status='ACTIVE'
                    AND p.responsible_user_id=@UserId
                  ORDER BY s.id
                  """,
            new { UserId = current.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows.AsList();
    }

    internal async Task<object> ProjectOwnerOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectDictionaryService.RequireOptionReadAsync(conn, tx, current, ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT u.id AS id,u.employee_no AS employeeNo,u.real_name AS realName,
                   CASE WHEN d.kind='SECTION' THEN d.id ELSE NULL END AS sectionId,
                   CASE WHEN d.kind='SECTION' THEN d.name ELSE NULL END AS sectionName
            FROM users u
            LEFT JOIN departments d ON d.id=u.department_id
            WHERE u.user_type='INTERNAL' AND u.status='ACTIVE'
              AND EXISTS(
                SELECT 1 FROM user_roles ur
                JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
                JOIN role_permissions rp ON rp.role_id=r.id
                JOIN permissions permission ON permission.id=rp.permission_id AND permission.code='project:list'
                WHERE ur.user_id=u.id)
            ORDER BY u.real_name,u.id
            """, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows.AsList();
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:delete", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM project_copies WHERE source_project_id=@ProjectId OR target_project_id=@ProjectId)",
                new { ProjectId = projectId }, tx, cancellationToken: ct)))
            throw ApiException.Conflict("项目存在复制引用履历，不能删除");
        var counts = await conn.QuerySingleAsync<ContentCount>(new CommandDefinition(
            """
            SELECT (SELECT COUNT(*) FROM files WHERE project_id=@ProjectId) AS FileCount,
                   (SELECT COUNT(*) FROM messages WHERE project_id=@ProjectId) AS MessageCount,
                   (SELECT COUNT(*) FROM upload_sessions WHERE project_id=@ProjectId) AS UploadCount
            """,
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        ProjectWorkflowRules.EnsureDeletable(
            project.Status,
            counts.FileCount + counts.MessageCount > 0,
            counts.UploadCount > 0);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM project_members WHERE project_id=@ProjectId;
            DELETE FROM email_outbox WHERE project_id=@ProjectId;
            DELETE FROM project_status_logs WHERE project_id=@ProjectId
            """,
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DELETE", "project", projectId, new { name = project.Name }, ip, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_activities WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM projects WHERE id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        if (deleted != 1)
        {
            throw ApiException.Conflict("项目已被删除，请刷新后重试");
        }
        await tx.CommitAsync(ct);
    }

    private async Task<object> DecideAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string action,
        ulong expectedSubmissionId,
        string? reason,
        string? ip,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        ProjectWorkflowRules.RequireInternalDecisionActor(current);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:confirm", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        if (project.Status != ProjectStatuses.PendingConfirmation)
        {
            throw ApiException.Conflict("项目当前不在待确认状态");
        }
        if (project.ConfirmSide != ProjectWorkflowRules.InternalAcceptanceSide)
        {
            throw new InvalidOperationException("待确认项目的确认方必须为公司内部");
        }
        var latestSubmit = await LatestSubmissionAsync(conn, tx, projectId, ct);
        EnsureExpectedSubmission(latestSubmit, expectedSubmissionId);
        var to = action == "CONFIRM" ? ProjectStatuses.Completed : ProjectStatuses.InProgress;
        await ApplyTransitionAsync(conn, tx, current, project, to, action, project.ConfirmSide, reason, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, action, project.ConfirmSide, reason, latestSubmit.OperatorId, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<(ProjectRow Project, CurrentUser Current)> LockWorkflowProjectAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CurrentUser actor,
        ulong projectId,
        string permission,
        CancellationToken ct)
    {
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, permission, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        return (project, current);
    }

    private async Task<ulong> ApplyTransitionAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CurrentUser actor,
        ProjectRow project,
        string to,
        string action,
        string? historyConfirmSide,
        string? reason,
        string? ip,
        CancellationToken ct)
    {
        var nextConfirmSide = action == "SUBMIT" ? historyConfirmSide : null;
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET status=@To,confirm_side=@NextConfirmSide,updated_at=UTC_TIMESTAMP(3) WHERE id=@ProjectId AND status=@From",
            new { To = to, NextConfirmSide = nextConfirmSide, ProjectId = project.Id, From = project.Status },
            tx,
            cancellationToken: ct));
        if (changed == 0)
        {
            throw ApiException.Conflict("项目状态已被他人变更，请刷新后重试");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at)
            VALUES(@ProjectId,@From,@To,@Action,@OperatorId,@ConfirmSide,@Reason,UTC_TIMESTAMP(3))
            """,
            new
            {
                ProjectId = project.Id,
                From = project.Status,
                To = to,
                Action = action,
                OperatorId = actor.Id,
                ConfirmSide = historyConfirmSide,
                Reason = reason,
            },
            tx,
            cancellationToken: ct));
        var statusLogId = await LastInsertIdAsync(conn, tx, ct);
        var auditAction = action switch
        {
            "START" => "PROJECT_START",
            "SUBMIT" => "PROJECT_SUBMIT",
            "CONFIRM" => "PROJECT_CONFIRM",
            "REJECT" => "PROJECT_REJECT",
            "WITHDRAW" => "PROJECT_WITHDRAW",
            "TERMINATE" => "PROJECT_TERMINATE",
            "RESTART" => "PROJECT_RESTART",
            _ => throw new InvalidOperationException($"未知项目流程动作: {action}"),
        };
        await audit.WriteAsync(conn, tx, actor.Id, auditAction, "project", project.Id, new
        {
            from = project.Status,
            to,
            action,
            confirmSide = historyConfirmSide,
            reason,
            statusLogId,
        }, ip, ct);
        return statusLogId;
    }

    private static async Task<ProjectStatusLogRow> LatestSubmissionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<ProjectStatusLogRow>(new CommandDefinition(
            """
            SELECT id AS Id,project_id AS ProjectId,operator_id AS OperatorId,action AS Action
            FROM project_status_logs WHERE project_id=@ProjectId AND action='SUBMIT'
            ORDER BY id DESC LIMIT 1
            """,
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        return row ?? throw new InvalidOperationException("待确认项目缺少提交历史");
    }

    internal static ulong RequireExpectedSubmissionId(ulong? expectedSubmissionId)
    {
        if (expectedSubmissionId is null or 0)
        {
            throw ApiException.BadRequest("expectedSubmissionId 必须为当前待验收提交版本");
        }
        return expectedSubmissionId.Value;
    }

    internal static void EnsureExpectedSubmission(ProjectStatusLogRow latestSubmit, ulong expectedSubmissionId)
    {
        if (latestSubmit.Id != expectedSubmissionId)
        {
            throw ApiException.Conflict("验收申请已更新，请刷新项目后重新操作");
        }
    }

    private static async Task<ProjectRow> LoadProjectAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        if (forUpdate)
        {
            var lockedId = await conn.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition(
                "SELECT id FROM projects WHERE id=@ProjectId FOR UPDATE",
                new { ProjectId = projectId },
                tx,
                cancellationToken: ct));
            if (lockedId is null)
            {
                throw ApiException.NotFound();
            }
        }

        const string sql = """
            SELECT p.id AS Id,p.name AS Name,p.description AS Description,p.supplier_id AS SupplierId,
                   p.machine_model AS MachineModel,p.robot_vendor_id AS RobotVendorId,
                   rv.code AS RobotVendorCode,rv.name AS RobotVendorName,p.robot_model_id AS RobotModelId,
                   rm.code AS RobotModelCode,rm.name AS RobotModelName,p.responsible_user_id AS ResponsibleUserId,
                   owner.employee_no AS ResponsibleUserEmployeeNo,owner.real_name AS ResponsibleUserName,
                   p.section_id AS SectionId,section.name AS SectionName,p.priority_id AS PriorityId,
                   priority.code AS PriorityCode,priority.name AS PriorityName,
                   p.expected_completion_date AS ExpectedCompletionDate,
                   EXISTS(SELECT 1 FROM project_copies pc
                          WHERE pc.source_project_id=p.id OR pc.target_project_id=p.id) AS HasCopyHistory,
                   p.status AS Status,p.confirm_side AS ConfirmSide,p.created_by AS CreatedBy,
                   CASE WHEN p.status='PENDING_CONFIRMATION' THEN (
                       SELECT MAX(psl.id) FROM project_status_logs psl
                       WHERE psl.project_id=p.id AND psl.action='SUBMIT'
                   ) ELSE NULL END AS LatestSubmissionId,
                   p.created_at AS CreatedAt,p.updated_at AS UpdatedAt,
                   s.name AS SupplierName,u.real_name AS CreatedByName
            FROM projects p
            LEFT JOIN suppliers s ON s.id=p.supplier_id
            LEFT JOIN users u ON u.id=p.created_by
            LEFT JOIN project_dictionaries rv ON rv.id=p.robot_vendor_id
            LEFT JOIN project_dictionaries rm ON rm.id=p.robot_model_id
            LEFT JOIN users owner ON owner.id=p.responsible_user_id
            LEFT JOIN departments section ON section.id=p.section_id AND section.kind='SECTION'
            LEFT JOIN project_dictionaries priority ON priority.id=p.priority_id
            WHERE p.id=@ProjectId
            """;
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            sql, new { ProjectId = projectId }, tx, cancellationToken: ct));
        if (row is null) throw ApiException.NotFound();
        row.WorkOrderNos = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT work_order_no FROM project_work_orders WHERE project_id=@ProjectId ORDER BY sort_no,id",
            new { ProjectId = projectId }, tx, cancellationToken: ct))).ToArray();
        return row;
    }

    private static async Task EnsureNameUniqueAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string name,
        ulong? excludeId,
        CancellationToken ct)
    {
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM projects WHERE name=@Name AND (@ExcludeId IS NULL OR id<>@ExcludeId))",
            new { Name = name, ExcludeId = excludeId }, tx, cancellationToken: ct));
        if (exists)
        {
            throw ApiException.Conflict("项目名称已存在");
        }
    }

    private static async Task LoadCopyLineageAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        IReadOnlyList<ProjectRow> projects,
        CurrentUser actor,
        bool canViewAll,
        CancellationToken ct)
    {
        if (projects.Count == 0) return;
        var ids = projects.Select(project => project.Id).ToArray();
        var relations = (await conn.QueryAsync<CopyLineageRow>(new CommandDefinition(
            """
            SELECT pc.source_project_id AS SourceProjectId,pc.target_project_id AS TargetProjectId,
                   source.name AS SourceName,source.supplier_id AS SourceSupplierId,
                   source.responsible_user_id AS SourceResponsibleUserId
            FROM project_copies pc JOIN projects source ON source.id=pc.source_project_id
            WHERE pc.source_project_id IN @Ids OR pc.target_project_id IN @Ids
            """, new { Ids = ids }, tx, cancellationToken: ct))).AsList();
        foreach (var project in projects)
        {
            var related = relations.Where(row => row.SourceProjectId == project.Id || row.TargetProjectId == project.Id).ToArray();
            project.HasCopyHistory = related.Length > 0;
            var source = related.SingleOrDefault(row => row.TargetProjectId == project.Id);
            if (source is null) continue;
            var canViewSource = actor.IsInternal
                ? canViewAll || source.SourceResponsibleUserId == actor.Id
                : actor.SupplierId is not null && actor.SupplierId == source.SourceSupplierId;
            if (!canViewSource) continue;
            project.CopySourceProjectId = source.SourceProjectId;
            project.CopySourceProjectName = source.SourceName;
        }
    }

    internal static string[] NormalizeWorkOrderNos(string?[]? values)
    {
        if (values is null) return [];
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var item = (value ?? string.Empty).Trim();
            if (item.Length == 0) continue;
            if (RuneCount(item) > 128) throw ApiException.BadRequest("单个工令号不能超过 128 个字符");
            if (seen.Add(item)) normalized.Add(item);
        }
        if (normalized.Count > 50) throw ApiException.BadRequest("工令号不能超过 50 个");
        return normalized.ToArray();
    }

    private static ProjectMetadataInput NormalizeMetadata(ProjectUpsertRequest request)
    {
        if (NormalizeWorkOrderNos(request.WorkOrderNos).Length == 0)
            throw ApiException.BadRequest("请至少填写一个工令号");
        if (string.IsNullOrWhiteSpace(request.MachineModel)) throw ApiException.BadRequest("请填写机型");
        if (request.RobotVendorId is null or 0) throw ApiException.BadRequest("请选择 Robot 厂商");
        if (request.RobotModelId is null or 0) throw ApiException.BadRequest("请选择 Robot 型号");
        if (request.ResponsibleUserId is null or 0) throw ApiException.BadRequest("请选择负责人");
        if (request.PriorityId is null or 0) throw ApiException.BadRequest("请选择优先级");
        if (string.IsNullOrWhiteSpace(request.ExpectedCompletionDate)) throw ApiException.BadRequest("请选择预计完成日期");
        var machineModel = string.IsNullOrWhiteSpace(request.MachineModel) ? null : request.MachineModel.Trim();
        if (machineModel is not null && RuneCount(machineModel) > 128)
            throw ApiException.BadRequest("机台机型不能超过 128 个字符");
        DateTime? expectedCompletionDate = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedCompletionDate))
        {
            var raw = request.ExpectedCompletionDate.Trim();
            if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)
                || parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != raw)
                throw ApiException.BadRequest("expectedCompletionDate 必须为 yyyy-MM-dd 格式");
            expectedCompletionDate = parsed.ToDateTime(TimeOnly.MinValue);
        }
        return new(NormalizeWorkOrderNos(request.WorkOrderNos), machineModel, request.RobotVendorId,
            request.RobotModelId, request.ResponsibleUserId, null, request.PriorityId, expectedCompletionDate);
    }

    private static async Task<ProjectMetadataInput> ValidateMetadataAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectMetadataInput input,
        ProjectRow? existing,
        CancellationToken ct)
    {
        var vendor = await ValidateDictionaryAsync(conn, tx, input.RobotVendorId,
            ProjectDictionaryTypes.RobotVendor, existing?.RobotVendorId, "机器人厂商", ct);
        var model = await ValidateDictionaryAsync(conn, tx, input.RobotModelId,
            ProjectDictionaryTypes.RobotModel, existing?.RobotModelId, "机器人型号", ct);
        await ValidateDictionaryAsync(conn, tx, input.PriorityId,
            ProjectDictionaryTypes.Priority, existing?.PriorityId, "优先级", ct);
        if (model is not null && vendor is null)
            throw ApiException.BadRequest("选择机器人型号时必须同时选择机器人厂商");
        if (model is not null && model.ParentId != vendor!.Id)
            throw ApiException.BadRequest("机器人型号不属于所选厂商");

        ulong? sectionId = null;
        if (input.ResponsibleUserId is { } responsibleUserId)
        {
            var owner = await conn.QuerySingleOrDefaultAsync<OwnerSelection>(new CommandDefinition(
                """
                SELECT u.id AS Id,
                       CASE WHEN d.kind='SECTION' THEN d.id ELSE NULL END AS SectionId
                FROM users u LEFT JOIN departments d ON d.id=u.department_id
                WHERE u.id=@Id AND u.user_type='INTERNAL' AND u.status='ACTIVE'
                  AND EXISTS(
                    SELECT 1 FROM user_roles ur
                    JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
                    JOIN role_permissions rp ON rp.role_id=r.id
                    JOIN permissions permission ON permission.id=rp.permission_id AND permission.code='project:list'
                    WHERE ur.user_id=u.id)
                """, new { Id = responsibleUserId }, tx, cancellationToken: ct));
            if (owner is null) throw ApiException.BadRequest("负责人必须是拥有项目列表权限的启用内部用户");
            sectionId = owner.SectionId;
            if (sectionId is null) throw ApiException.BadRequest("负责人未关联课别，请先在用户管理中设置其所属课别");
        }
        return input with { SectionId = sectionId };
    }

    private static async Task<MetadataDictionaryRow?> ValidateDictionaryAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong? id,
        string expectedType,
        ulong? existingId,
        string label,
        CancellationToken ct)
    {
        if (id is null) return null;
        var row = await conn.QuerySingleOrDefaultAsync<MetadataDictionaryRow>(new CommandDefinition(
            "SELECT id AS Id,type AS Type,parent_id AS ParentId,status AS Status FROM project_dictionaries WHERE id=@Id",
            new { Id = id.Value }, tx, cancellationToken: ct));
        if (row is null || row.Type != expectedType) throw ApiException.BadRequest(label + "不存在或类型不匹配");
        if (row.Status != "ACTIVE" && id != existingId) throw ApiException.BadRequest(label + "已停用");
        return row;
    }

    private static async Task ReplaceWorkOrdersAsync(
        MySqlConnection conn, MySqlTransaction tx, ulong projectId, string[] values, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM project_work_orders WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        for (var index = 0; index < values.Length; index++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_work_orders(project_id,work_order_no,sort_no,created_at) VALUES(@ProjectId,@Value,@SortNo,UTC_TIMESTAMP(3))",
                new { ProjectId = projectId, Value = values[index], SortNo = index }, tx, cancellationToken: ct));
        }
    }

    private static async Task LoadWorkOrdersAsync(
        MySqlConnection conn, MySqlTransaction tx, IReadOnlyCollection<ProjectRow> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return;
        var byProject = (await conn.QueryAsync<ProjectWorkOrderRow>(new CommandDefinition(
            "SELECT project_id AS ProjectId,work_order_no AS WorkOrderNo FROM project_work_orders WHERE project_id IN @Ids ORDER BY project_id,sort_no,id",
            new { Ids = projects.Select(project => project.Id).ToArray() }, tx, cancellationToken: ct)))
            .GroupBy(row => row.ProjectId).ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        foreach (var project in projects)
            project.WorkOrderNos = byProject.GetValueOrDefault(project.Id) ?? [];
    }

    private static Task<ulong> CountActiveUploadsAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM upload_sessions WHERE project_id=@ProjectId AND status IN ('UPLOADING','MERGING')",
            new { ProjectId = projectId }, tx, cancellationToken: ct));

    private static Task<ulong> LastInsertIdAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));

    private static string ValidateNameForCreate(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("项目名称不能为空");
        }
        if (RuneCount(name) > 128)
        {
            throw ApiException.BadRequest("项目名称过长");
        }
        return name;
    }

    private static string ValidateNameForUpdate(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || RuneCount(name) > 128)
        {
            throw ApiException.BadRequest("项目名称需为 1~128 个字符");
        }
        return name;
    }

    private static void ValidateDescription(string? description)
    {
        if (description is not null && RuneCount(description) > 500)
        {
            throw ApiException.BadRequest("项目说明过长（最多 500 字）");
        }
    }

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private sealed class ProjectDetailHistory
    {
        public string? RejectReason { get; init; }
        public ulong? LatestSubmitterId { get; init; }
    }

    private sealed class ProjectCopySourceRow
    {
        public ulong ProjectId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class CopyLineageRow
    {
        public ulong SourceProjectId { get; init; }
        public ulong TargetProjectId { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public ulong SourceSupplierId { get; init; }
        public ulong? SourceResponsibleUserId { get; init; }
    }

    private sealed class MemberRow
    {
        public ulong UserId { get; init; }
        public string EmployeeNo { get; init; } = string.Empty;
        public string RealName { get; init; } = string.Empty;
        public ulong? DepartmentId { get; init; }
        public string? DeptName { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ContentCount
    {
        public ulong FileCount { get; init; }
        public ulong MessageCount { get; init; }
        public ulong UploadCount { get; init; }
    }

    private sealed record ProjectMetadataInput(
        string[] WorkOrderNos,
        string? MachineModel,
        ulong? RobotVendorId,
        ulong? RobotModelId,
        ulong? ResponsibleUserId,
        ulong? SectionId,
        ulong? PriorityId,
        DateTime? ExpectedCompletionDate);

    private sealed class MetadataDictionaryRow
    {
        public ulong Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public ulong? ParentId { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    private sealed class OwnerSelection
    {
        public ulong Id { get; init; }
        public ulong? SectionId { get; init; }
    }

    private sealed class ProjectWorkOrderRow
    {
        public ulong ProjectId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
    }
}
