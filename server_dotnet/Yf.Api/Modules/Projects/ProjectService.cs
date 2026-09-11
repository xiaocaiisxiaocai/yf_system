using System.Text;
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
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
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
        else if (!await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct))
        {
            clauses.Add("(p.created_by = @ActorId OR EXISTS(SELECT 1 FROM project_members pm WHERE pm.project_id = p.id AND pm.user_id = @ActorId))");
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
            SELECT p.id AS Id, p.name AS Name, p.description AS Description,
                   p.supplier_id AS SupplierId, p.status AS Status, p.confirm_side AS ConfirmSide,
                   p.created_by AS CreatedBy, p.created_at AS CreatedAt, p.updated_at AS UpdatedAt,
                   s.name AS SupplierName, u.real_name AS CreatedByName
            FROM projects p
            LEFT JOIN suppliers s ON s.id = p.supplier_id
            LEFT JOIN users u ON u.id = p.created_by
            """ + where + " ORDER BY p.id DESC LIMIT @Size OFFSET @Offset",
            args,
            tx,
            cancellationToken: ct))).AsList();
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
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
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
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO projects(name,description,supplier_id,status,confirm_side,created_by,created_at,updated_at)
                VALUES(@Name,@Description,@SupplierId,'DRAFT',NULL,@CreatedBy,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
                """,
                new { Name = name, request.Description, request.SupplierId, CreatedBy = current.Id },
                tx,
                cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("项目名称已存在");
        }
        var projectId = await LastInsertIdAsync(conn, tx, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_members(project_id,user_id,created_by,created_at)
            VALUES(@ProjectId,@UserId,@UserId,UTC_TIMESTAMP(3));
            INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at)
            VALUES(@ProjectId,NULL,'DRAFT','CREATE',@UserId,NULL,NULL,UTC_TIMESTAMP(3))
            """,
            new { ProjectId = projectId, UserId = current.Id },
            tx,
            cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", projectId, new { name }, ip, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
    }

    internal async Task<object> DetailAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var project = await LoadProjectAsync(conn, null, projectId, false, ct);
        var members = await ListMembersCoreAsync(conn, null, projectId, ct);
        var latest = await conn.QuerySingleOrDefaultAsync<ProjectDetailHistory>(new CommandDefinition(
            """
            SELECT
              (SELECT reason FROM project_status_logs WHERE project_id=@ProjectId AND action='REJECT' ORDER BY id DESC LIMIT 1) AS RejectReason,
              (SELECT operator_id FROM project_status_logs WHERE project_id=@ProjectId AND action='SUBMIT' ORDER BY id DESC LIMIT 1) AS LatestSubmitterId
            """,
            new { ProjectId = projectId },
            cancellationToken: ct));
        return new
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
            members,
            rejectReason = latest?.RejectReason,
            latestSubmitterId = latest?.LatestSubmitterId,
        };
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
        await using var tx = await conn.BeginTransactionAsync(ct);
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
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET name=@Name,description=@Description,updated_at=UTC_TIMESTAMP(3) WHERE id=@ProjectId",
                new { Name = name, request.Description, ProjectId = projectId },
                tx,
                cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("项目名称已存在");
        }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", projectId, null, ip, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
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
        await using var tx = await conn.BeginTransactionAsync(ct);
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
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
    }

    internal async Task<object> SubmitAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectSubmitRequest request,
        string? ip,
        CancellationToken ct)
    {
        var side = ParseConfirmSide(request.ConfirmSide);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:submit", ct);
        if (side == UserSide(current))
        {
            throw ApiException.BadRequest("确认方必须选择提交人的另一方");
        }
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
        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.PendingConfirmation, "SUBMIT", side, null, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, "SUBMIT", side, null, null, current, options.WebBaseUrl, audit, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
    }

    internal Task<object> ConfirmAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, string? ip, CancellationToken ct) =>
        DecideAsync(conn, actor, projectId, "CONFIRM", null, ip, ct);

    internal async Task<object> RejectAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectRejectRequest request,
        string? ip,
        CancellationToken ct)
    {
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw ApiException.BadRequest("驳回原因不能为空");
        }
        if (RuneCount(reason) > 500)
        {
            throw ApiException.BadRequest("驳回原因过长（最多 500 字）");
        }
        return await DecideAsync(conn, actor, projectId, "REJECT", reason, ip, ct);
    }

    internal async Task<object> WithdrawAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string? ip,
        CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:withdraw", ct);
        if (project.Status != ProjectStatuses.PendingConfirmation)
        {
            throw ApiException.Conflict("项目当前不在待确认状态");
        }
        var latestSubmit = await LatestSubmissionAsync(conn, tx, projectId, ct);
        var privileged = current.IsInternal
            && await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct);
        if (latestSubmit.OperatorId != current.Id && !privileged)
        {
            throw ApiException.Forbidden();
        }
        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.InProgress, "WITHDRAW", project.ConfirmSide, null, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, "WITHDRAW", project.ConfirmSide, null, latestSubmit.OperatorId, current, options.WebBaseUrl, audit, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
    }

    internal async Task<object> ListMembersAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        return await ListMembersCoreAsync(conn, null, projectId, ct);
    }

    internal async Task<object> ListSupplierMembersAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        var accessProject = await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var supplierActive = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM suppliers WHERE id=@SupplierId AND status='ACTIVE')",
            new { accessProject.SupplierId }, cancellationToken: ct));
        if (!supplierActive)
        {
            return Array.Empty<object>();
        }
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            """
            SELECT id AS Id, employee_no AS EmployeeNo, real_name AS RealName, status AS Status
            FROM users
            WHERE user_type='SUPPLIER' AND supplier_id=@SupplierId AND status='ACTIVE'
            ORDER BY id
            """,
            new { accessProject.SupplierId }, cancellationToken: ct));
        return rows.Select(user => new
        {
            userId = user.Id,
            employeeNo = user.EmployeeNo,
            realName = user.RealName,
            status = "ACTIVE",
        }).ToArray();
    }

    internal async Task SetMembersAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectMembersRequest request,
        string? ip,
        CancellationToken ct)
    {
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var requestedIds = request.UserIds ?? [];
        if (requestedIds.Length > 200)
        {
            throw ApiException.BadRequest("成员数量超过上限");
        }
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:member", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        if (project.Status is not (ProjectStatuses.Draft or ProjectStatuses.InProgress))
        {
            throw ApiException.Conflict("项目当前状态不可调整成员");
        }
        var ids = requestedIds.Append(current.Id).Distinct().Order().ToArray();
        foreach (var id in ids)
        {
            var user = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
                "SELECT id AS Id,employee_no AS EmployeeNo,user_type AS UserType,status AS Status FROM users WHERE id=@Id",
                new { Id = id }, tx, cancellationToken: ct));
            if (user is null)
            {
                throw ApiException.BadRequest($"用户不存在: {id}");
            }
            if (user.UserType != "INTERNAL" || user.Status != "ACTIVE")
            {
                throw ApiException.BadRequest($"用户 {user.EmployeeNo} 不是启用的内部账号");
            }
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_members WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        foreach (var id in ids)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_members(project_id,user_id,created_by,created_at) VALUES(@ProjectId,@UserId,@CreatedBy,UTC_TIMESTAMP(3))",
                new { ProjectId = projectId, UserId = id, CreatedBy = current.Id }, tx, cancellationToken: ct));
        }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_MEMBERS", "project", projectId, null, ip, ct);
        await tx.CommitAsync(ct);
    }

    internal async Task<object> SummaryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        var project = await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var unread = await MessageService.UnreadCountAsync(conn, null, actor.Id, projectId, ct);
        var canConfirm = await ProjectAccessService.HasPermissionAsync(conn, null, actor.Id, "project:confirm", ct);
        return new
        {
            unreadMessages = unread,
            pendingConfirmation = canConfirm
                && project.Status == ProjectStatuses.PendingConfirmation
                && project.ConfirmSide == UserSide(actor),
        };
    }

    internal async Task<object> SupplierOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, name FROM suppliers WHERE status='ACTIVE'",
            cancellationToken: ct));
        return rows.AsList();
    }

    internal async Task<object> InternalUserOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT u.id AS id,u.employee_no AS employeeNo,u.real_name AS realName,d.name AS deptName
            FROM users u LEFT JOIN departments d ON d.id=u.department_id
            WHERE u.user_type='INTERNAL' AND u.status='ACTIVE'
            """,
            cancellationToken: ct));
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
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:delete", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
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
        string? reason,
        string? ip,
        CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:confirm", ct);
        if (project.Status != ProjectStatuses.PendingConfirmation)
        {
            throw ApiException.Conflict("项目当前不在待确认状态");
        }
        if (project.ConfirmSide is null)
        {
            throw new InvalidOperationException("待确认项目缺少确认方");
        }
        if (project.ConfirmSide != UserSide(current))
        {
            throw ApiException.Forbidden();
        }
        var latestSubmit = await LatestSubmissionAsync(conn, tx, projectId, ct);
        var to = action == "CONFIRM" ? ProjectStatuses.Completed : ProjectStatuses.InProgress;
        await ApplyTransitionAsync(conn, tx, current, project, to, action, project.ConfirmSide, reason, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, action, project.ConfirmSide, reason, latestSubmit.OperatorId, current, options.WebBaseUrl, audit, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Project(await LoadProjectAsync(conn, null, projectId, false, ct));
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

    private async Task ApplyTransitionAsync(
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
    }

    private static async Task<ProjectStatusLogRow> LatestSubmissionAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
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

    private static async Task<ProjectRow> LoadProjectAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        var sql = """
            SELECT p.id AS Id,p.name AS Name,p.description AS Description,p.supplier_id AS SupplierId,
                   p.status AS Status,p.confirm_side AS ConfirmSide,p.created_by AS CreatedBy,
                   p.created_at AS CreatedAt,p.updated_at AS UpdatedAt,
                   s.name AS SupplierName,u.real_name AS CreatedByName
            FROM projects p
            LEFT JOIN suppliers s ON s.id=p.supplier_id
            LEFT JOIN users u ON u.id=p.created_by
            WHERE p.id=@ProjectId
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            sql, new { ProjectId = projectId }, tx, cancellationToken: ct));
        return row ?? throw ApiException.NotFound();
    }

    private static async Task<object[]> ListMembersCoreAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        var rows = await conn.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT u.id AS UserId,u.employee_no AS EmployeeNo,u.real_name AS RealName,
                   u.department_id AS DepartmentId,d.name AS DeptName,u.status AS Status,
                   pm.created_at AS CreatedAt
            FROM project_members pm
            INNER JOIN users u ON u.id=pm.user_id
            LEFT JOIN departments d ON d.id=u.department_id
            WHERE pm.project_id=@ProjectId
            """,
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        return rows.Select(row => (object)new
        {
            userId = row.UserId,
            employeeNo = row.EmployeeNo,
            realName = row.RealName,
            departmentId = row.DepartmentId,
            deptName = row.DeptName,
            status = row.Status,
            createdAt = ProjectJson.Utc(row.CreatedAt),
        }).ToArray();
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

    private static string ParseConfirmSide(string? value) => (value ?? string.Empty).Trim() switch
    {
        "COMPANY" => "COMPANY",
        "SUPPLIER" => "SUPPLIER",
        _ => throw ApiException.BadRequest("确认方必须为 COMPANY 或 SUPPLIER"),
    };

    private static string UserSide(CurrentUser actor) => actor.IsInternal ? "COMPANY" : "SUPPLIER";

    private sealed class ProjectDetailHistory
    {
        public string? RejectReason { get; init; }
        public ulong? LatestSubmitterId { get; init; }
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
}
