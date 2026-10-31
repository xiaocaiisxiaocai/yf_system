using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

// Subproject workflow transitions (start, terminate, restart, reopen); material approval completes a subproject
// in ProjectService.MaterialReview.cs. See Robot仿真协作流程调整方案.
internal sealed partial class ProjectService
{
    internal async Task<ProjectResponse> SetStatusAsync(
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
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:status", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        var requested = request.Status ?? string.Empty;
        var (to, action) = ProjectWorkflowRules.ManagementTransition(project.Status, requested);
        if (action is "START" or "RESTART")
        {
            await EnsureSupplierCanCollaborateAsync(conn, tx, project.SupplierId, ct);
        }
        if (action == "TERMINATE" && await CountActiveUploadsAsync(conn, tx, projectId, false, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，不能终止");
        }
        await ApplyTransitionAsync(conn, tx, current, project, to, action, null, ip, ct);
        await ProjectNotificationService.EnqueueStatusChangeAsync(
            conn, tx, project, action, null, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal const string SupplierDisabledStartMessage = "项目所属厂商已停用，请先启用厂商后再开始项目";
    internal const string SupplierWithoutActiveUserStartMessage = "项目所属厂商尚未添加启用的用户，请先添加或启用厂商用户后再开始项目";
    internal const string SupplierWithoutUploaderStartMessage =
        "项目所属厂商的启用用户都没有上传文件权限，请先为厂商用户分配包含“上传文件”权限的角色后再开始项目";

    /// <summary>
    /// START/RESTART hands the subproject to the supplier, so the supplier company must be active and at least one
    /// of its active supplier accounts must effectively hold <c>file:upload</c> (through an active role) to return material.
    /// </summary>
    private static async Task EnsureSupplierCanCollaborateAsync(MySqlConnection conn, MySqlTransaction tx, ulong supplierId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        if (!await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == AccountStatuses.Active, ct))
        {
            throw ApiException.Conflict(SupplierDisabledStartMessage);
        }
        var activeUsers = db.Users.Where(user => user.SupplierId == supplierId
            && user.UserType == UserTypes.Supplier && user.Status == AccountStatuses.Active);
        if (!await activeUsers.AnyAsync(ct))
        {
            throw ApiException.Conflict(SupplierWithoutActiveUserStartMessage);
        }
        var uploaders = AccessService.UsersWithPermission(db, "file:upload");
        if (!await activeUsers.AnyAsync(user => uploaders.Contains(user.Id), ct))
        {
            throw ApiException.Conflict(SupplierWithoutUploaderStartMessage);
        }
    }

    /// <summary>
    /// Reopens a completed subproject as an after-the-fact return: it goes back to IN_PROGRESS with the latest
    /// supplier material marked RETURNED, and follows the main project's current public data again.
    /// </summary>
    internal async Task<ProjectResponse> ReopenAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectReopenRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden("重开子项目仅限项目负责人");
        }
        if (request.ExpectedUpdatedAt is not { } expectedUpdatedAt)
        {
            throw ApiException.BadRequest("expectedUpdatedAt 必须为当前子项目的 updatedAt");
        }
        var reason = ValidateReviewReason(request.Reason, "重开原因");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:list", ct);
        if (project.ResponsibleUserId != current.Id)
        {
            throw ApiException.Forbidden("重开子项目仅限项目负责人");
        }
        if (ProjectJson.Utc(project.UpdatedAt) != ProjectJson.Utc(expectedUpdatedAt))
        {
            throw ApiException.VersionConflict(SubprojectChangedMessage);
        }
        if (project.Status != ProjectStatuses.Completed)
        {
            throw ApiException.Conflict("只有已完成的子项目可以重开");
        }

        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.InProgress, "REOPEN", reason, ip, ct);
        await using var db = EfDb.Use(conn, tx);
        await ProjectGroupService.SyncSubprojectFromGroupAsync(db, project.ProjectGroupId, projectId, ct);
        var latestSupplierFileId = await db.Files
            .Where(file => file.ProjectId == projectId && file.Status == FileStatuses.Available && file.Direction == "S2C")
            .MaxAsync(file => (ulong?)file.Id, ct);
        if (latestSupplierFileId is not null)
        {
            await db.Projects.Where(item => item.Id == projectId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.MaterialReviewStatus, MaterialReviewStatuses.Returned)
                    .SetProperty(item => item.MaterialReviewFileId, latestSupplierFileId)
                    .SetProperty(item => item.MaterialReviewReason, reason), ct);
        }
        await ProjectNotificationService.EnqueueStatusChangeAsync(
            conn, tx, project, "REOPEN", reason, current, options.WebBaseUrl, audit, ct);
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
        string? reason,
        string? ip,
        CancellationToken ct,
        ulong? supplierFileId = null)
    {
        await using var db = EfDb.Use(conn, tx);
        var now = await DatabaseUtcNowAsync(db, ct);
        var changed = await db.Projects
            .Where(item => item.Id == project.Id && item.Status == project.Status)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, to)
                // Every transition ends the current material review; restarted or reopened subprojects
                // need the responsible user to review the material again.
                .SetProperty(item => item.MaterialReviewStatus, (string?)null)
                .SetProperty(item => item.MaterialReviewFileId, (ulong?)null)
                .SetProperty(item => item.MaterialReviewReason, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            throw ApiException.VersionConflict("项目状态已被他人变更，请刷新后重试");

        var statusLog = new ProjectStatusLog
        {
            ProjectId = project.Id,
            FromStatus = project.Status,
            ToStatus = to,
            Action = action,
            OperatorId = actor.Id,
            Reason = reason,
            CreatedAt = now,
        };
        db.ProjectStatusLogs.Add(statusLog);
        await db.SaveChangesAsync(ct);
        var statusLogId = statusLog.Id;
        var auditAction = action switch
        {
            "START" => "PROJECT_START",
            "MATERIAL_APPROVE" => "PROJECT_MATERIAL_APPROVE",
            "REOPEN" => "PROJECT_REOPEN",
            "TERMINATE" => "PROJECT_TERMINATE",
            "RESTART" => "PROJECT_RESTART",
            _ => throw new InvalidOperationException($"未知项目流程动作: {action}"),
        };
        object details = supplierFileId is null
            ? new { from = project.Status, to, action, reason, statusLogId }
            : new { from = project.Status, to, action, reason, statusLogId, supplierFileId };
        await audit.WriteAsync(conn, tx, actor.Id, auditAction, "project", project.Id, details, ip, ct);
        await groupStatus.RecalculateAsync(
            conn, tx, project.ProjectGroupId, actor.Id, project.Id, ct, groupAlreadyLocked: true,
            allowCompletion: action == "MATERIAL_APPROVE");
        return statusLogId;
    }

    private static string ValidateReviewReason(string? value, string label)
    {
        var reason = (value ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw ApiException.BadRequest($"{label}不能为空");
        }
        if (RuneCount(reason) > 500)
        {
            throw ApiException.BadRequest($"{label}过长（最多 500 字）");
        }
        return reason;
    }
}
