using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

// Subproject workflow transitions (start, terminate, restart, reopen); material approval completes a subproject
// in ProjectService.MaterialReview.cs. See Robot仿真协作流程调整方案.
internal sealed partial class ProjectService
{
    internal const string StatusChangedMessage = "项目状态已被他人变更，请刷新后重试";

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
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:status", ct);
        // Scope is authorized before any row lock; the locked values are authorized again inside.
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        // Optional guard: a caller that decided from a stale status must refresh instead of acting on a newer one.
        if (request.ExpectedStatus is { } expectedStatus && expectedStatus != project.Status)
            throw ApiException.VersionConflict(StatusChangedMessage);
        var requested = request.Status ?? string.Empty;
        var (to, action) = ProjectWorkflowRules.ManagementTransition(project.Status, requested);
        if (action is "START" or "RESTART")
        {
            await EnsureResponsibleUserCanActAsync(conn, tx, project.ResponsibleUserId, ct);
            await EnsureSupplierCanCollaborateAsync(conn, tx, project.SupplierId, ct);
        }
        if (action == "TERMINATE" && await CountActiveUploadsAsync(conn, tx, projectId, false, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，不能终止");
        }
        await ApplyTransitionAsync(conn, tx, current, project, to, action, null, ip, ct);
        if (action is "START" or "RESTART")
        {
            await StartMaterialRoundAsync(conn, tx, projectId, ct);
        }
        await ProjectNotificationService.EnqueueStatusChangeAsync(
            conn, tx, project, action, null, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Starts a new material round: supplier files already on the subproject (copied from the source, or returned
    /// before it was terminated) stay downloadable but are not this round's material, so only a new upload can be
    /// approved. Reopen keeps the round, since it re-reviews the material the round already produced.
    /// </summary>
    private static async Task StartMaterialRoundAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var floor = await db.Files.Where(file => file.ProjectId == projectId && file.Direction == "S2C")
            .MaxAsync(file => (ulong?)file.Id, ct);
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.MaterialFileFloor, floor)
                // The transition already stamped updated_at; keep that value instead of ON UPDATE.
                .SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct);
    }

    internal const string ResponsibleUserUnavailableStartMessage = "主项目负责人已停用或没有项目列表权限，请先转交主项目后再开始项目";

    /// <summary>
    /// Only the responsible user reviews, returns and reopens material. The disable guard covers draft and in-progress
    /// main projects only, so a terminated one may be owned by a disabled account; restarting it would strand the subproject.
    /// </summary>
    private static async Task EnsureResponsibleUserCanActAsync(MySqlConnection conn, MySqlTransaction tx, ulong? responsibleUserId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        if (responsibleUserId is not ulong ownerId || !await EligibleOwners(db).AnyAsync(owner => owner.Id == ownerId, ct))
        {
            throw ApiException.Conflict(ResponsibleUserUnavailableStartMessage);
        }
    }

    internal const string SupplierDisabledStartMessage = "项目所属厂商已停用，请先启用厂商后再开始项目";
    internal const string SupplierWithoutActiveUserStartMessage = "项目所属厂商尚未添加启用的用户，请先添加或启用厂商用户后再开始项目";
    internal const string SupplierWithoutUploaderStartMessage =
        "项目所属厂商的启用用户都没有上传文件权限，请先为厂商用户分配包含“上传文件”权限的角色后再开始项目";

    /// <summary>
    /// START/RESTART/REOPEN hand the subproject to the supplier, so the supplier company must be active and at least one
    /// of its active supplier accounts must effectively hold <c>file:upload</c> (through an active role) to return material.
    /// The supplier row is share-locked so a concurrent disable (which refuses while subprojects are in progress) waits for this one.
    /// </summary>
    private static async Task EnsureSupplierCanCollaborateAsync(MySqlConnection conn, MySqlTransaction tx, ulong supplierId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var supplierStatus = await db.Database
            .SqlQuery<string>($"SELECT status AS Value FROM suppliers WHERE id={supplierId} LOCK IN SHARE MODE")
            .SingleOrDefaultAsync(ct);
        if (supplierStatus != AccountStatuses.Active)
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
        await EnsureSupplierCanCollaborateAsync(conn, tx, project.SupplierId, ct);

        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.InProgress, "REOPEN", reason, ip, ct);
        await using var db = EfDb.Use(conn, tx);
        // A completed main project was frozen while contacts were renamed; it is open again now, so rebuild its
        // Robot owner snapshot before the subproject copies the group's data.
        await RobotOwners.RebuildSnapshotAsync(db, project.ProjectGroupId, ct);
        await ProjectGroupService.SyncSubprojectFromGroupAsync(db, project.ProjectGroupId, projectId, ct);
        var floor = await db.Projects.Where(item => item.Id == projectId).Select(item => item.MaterialFileFloor).SingleAsync(ct);
        var latestSupplierFileId = await db.Files
            .Where(file => file.ProjectId == projectId && file.Status == FileStatuses.Available && file.Direction == "S2C"
                && (floor == null || file.Id > floor))
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
        await AccessService.RequirePermissionAsync(conn, tx, current, permission, ct);
        // Scope is authorized before any row lock; the locked values are authorized again inside.
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
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
            throw ApiException.VersionConflict(StatusChangedMessage);

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
