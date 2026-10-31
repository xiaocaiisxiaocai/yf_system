using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

// Responsible-user review of the supplier's returned material: approval completes the subproject, a return sends
// it back to the supplier. See Robot仿真协作流程调整方案.
internal sealed partial class ProjectService
{
    internal async Task<ProjectResponse> ApproveMaterialsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectMaterialReviewRequest request,
        string? ip,
        CancellationToken ct)
    {
        var expectedSupplierFileId = RequireExpectedSupplierFileId(request.ExpectedSupplierFileId);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current, latestSupplierFileId) =
            await LockMaterialReviewAsync(conn, tx, actor, projectId, expectedSupplierFileId, ct);
        await ApplyTransitionAsync(conn, tx, current, project, ProjectStatuses.Completed, "MATERIAL_APPROVE", null, ip, ct,
            supplierFileId: latestSupplierFileId);
        await ProjectNotificationService.EnqueueMaterialReviewAsync(
            conn, tx, project, approved: true, null, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProjectResponse> ReturnMaterialsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectMaterialReviewRequest request,
        string? ip,
        CancellationToken ct)
    {
        var expectedSupplierFileId = RequireExpectedSupplierFileId(request.ExpectedSupplierFileId);
        var reason = ValidateReviewReason(request.Reason, "退回原因");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var (project, current, latestSupplierFileId) =
            await LockMaterialReviewAsync(conn, tx, actor, projectId, expectedSupplierFileId, ct);
        if (ProjectWorkflowRules.EffectiveMaterialReview(project) == MaterialReviewStatuses.Returned)
        {
            throw ApiException.Conflict("资料已退回");
        }

        await using var db = EfDb.Use(conn, tx);
        var now = await DatabaseUtcNowAsync(db, ct);
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.MaterialReviewStatus, MaterialReviewStatuses.Returned)
                .SetProperty(item => item.MaterialReviewFileId, latestSupplierFileId)
                .SetProperty(item => item.MaterialReviewReason, reason)
                .SetProperty(item => item.UpdatedAt, now), ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_MATERIAL_RETURN",
            "project", projectId, new { supplierFileId = latestSupplierFileId, reason }, ip, ct);
        await ProjectNotificationService.EnqueueMaterialReviewAsync(
            conn, tx, project, approved: false, reason, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<(ProjectRow Project, CurrentUser Current, ulong LatestSupplierFileId)> LockMaterialReviewAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        CurrentUser actor,
        ulong projectId,
        ulong expectedSupplierFileId,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden("资料确认仅限项目负责人");
        }
        // The project row lock serializes with upload merges, so the latest supplier file read below is stable.
        var (project, current) = await LockWorkflowProjectAsync(conn, tx, actor, projectId, "project:list", ct);
        if (project.ResponsibleUserId != current.Id)
        {
            throw ApiException.Forbidden("资料确认仅限项目负责人");
        }
        if (project.Status != ProjectStatuses.InProgress)
        {
            throw ApiException.Conflict("只有进行中的项目可以确认资料");
        }
        if (project.LatestSupplierFileId is not { } latestSupplierFileId)
        {
            throw ApiException.Conflict("厂商尚未回传资料，不能确认");
        }
        if (latestSupplierFileId != expectedSupplierFileId)
        {
            throw ApiException.VersionConflict("厂商回传资料已更新，请刷新后重新确认");
        }
        // Only the supplier's own uploads matter: reviewing while it still sends material would cover unseen files.
        if (await CountActiveUploadsAsync(conn, tx, projectId, true, ct) > 0)
        {
            throw ApiException.Conflict("厂商仍有上传中的资料，请等待上传完成后再确认");
        }
        return (project, current, latestSupplierFileId);
    }

    internal static ulong RequireExpectedSupplierFileId(ulong? expectedSupplierFileId)
    {
        if (expectedSupplierFileId is null or 0)
        {
            throw ApiException.BadRequest("expectedSupplierFileId 必须为当前最新的厂商回传文件");
        }
        return expectedSupplierFileId.Value;
    }
}
