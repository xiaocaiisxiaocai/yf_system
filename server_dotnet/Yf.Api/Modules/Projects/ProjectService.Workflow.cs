using System.Text;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

// Subproject workflow transitions (start, submit, confirm, reject, withdraw, terminate, restart). See 主项目与子项目协作契约.
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
        if (action == "TERMINATE" && await CountActiveUploadsAsync(conn, tx, projectId, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，不能终止");
        }
        await ApplyTransitionAsync(conn, tx, current, project, to, action, null, null, ip, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProjectResponse> SubmitAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectSubmitRequest request,
        string? ip,
        CancellationToken ct)
    {
        ProjectWorkflowRules.RequireSupplierSubmitter(actor);
        var side = ProjectWorkflowRules.NormalizeConfirmSide(request.ConfirmSide);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:submit", ct);
        ProjectWorkflowRules.RequireSupplierSubmitter(current);
        if (project.Status != ProjectStatuses.InProgress)
        {
            throw ApiException.Conflict("只有进行中的项目可以提交验收");
        }
        await using var db = EfDb.Use(conn, tx);
        if (!await db.Files.AnyAsync(
                file => file.ProjectId == projectId && file.Status == FileStatuses.Available, ct))
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

    internal Task<ProjectResponse> ConfirmAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectDecisionRequest request,
        string? ip,
        CancellationToken ct) =>
        DecideAsync(conn, actor, projectId, "CONFIRM", RequireExpectedSubmissionId(request.ExpectedSubmissionId), null, ip, ct);

    internal async Task<ProjectResponse> RejectAsync(
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

    internal async Task<ProjectResponse> WithdrawAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectDecisionRequest request,
        string? ip,
        CancellationToken ct)
    {
        ProjectWorkflowRules.RequireSupplierWithdrawer(actor);
        var expectedSubmissionId = RequireExpectedSubmissionId(request.ExpectedSubmissionId);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:withdraw", ct);
        ProjectWorkflowRules.RequireSupplierWithdrawer(current);
        if (project.Status != ProjectStatuses.PendingConfirmation)
        {
            throw ApiException.Conflict("项目当前不在待确认状态");
        }
        var latestSubmit = await LatestSubmissionAsync(conn, tx, projectId, ct);
        EnsureExpectedSubmission(latestSubmit, expectedSubmissionId);
        if (latestSubmit.OperatorId != current.Id)
        {
            throw ApiException.Forbidden();
        }
        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.InProgress, "WITHDRAW", project.ConfirmSide, null, ip, ct);
        await ProjectNotificationService.EnqueueWorkflowAsync(conn, tx, project, "WITHDRAW", project.ConfirmSide, null, latestSubmit.OperatorId, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<ProjectResponse> DecideAsync(
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
        await using var db = EfDb.Use(conn, tx);
        var now = await DatabaseUtcNowAsync(db, ct);
        var changed = await db.Projects
            .Where(item => item.Id == project.Id && item.Status == project.Status)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, to)
                .SetProperty(item => item.ConfirmSide, nextConfirmSide)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            throw ApiException.Conflict("项目状态已被他人变更，请刷新后重试");

        var statusLog = new ProjectStatusLog
        {
            ProjectId = project.Id,
            FromStatus = project.Status,
            ToStatus = to,
            Action = action,
            OperatorId = actor.Id,
            ConfirmSide = historyConfirmSide,
            Reason = reason,
            CreatedAt = now,
        };
        db.ProjectStatusLogs.Add(statusLog);
        await db.SaveChangesAsync(ct);
        var statusLogId = statusLog.Id;
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
        if (project.ProjectGroupId != 0)
            await groupStatus.RecalculateAsync(conn, tx, project.ProjectGroupId, actor.Id, project.Id, ct);
        return statusLogId;
    }

    private static async Task<ProjectStatusLogRow> LatestSubmissionAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var row = await db.ProjectStatusLogs
            .Where(log => log.ProjectId == projectId && log.Action == "SUBMIT")
            .OrderByDescending(log => log.Id)
            .Select(log => new ProjectStatusLogRow
            {
                Id = log.Id,
                ProjectId = log.ProjectId,
                OperatorId = log.OperatorId,
                Action = log.Action,
            })
            .FirstOrDefaultAsync(ct);
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
}
