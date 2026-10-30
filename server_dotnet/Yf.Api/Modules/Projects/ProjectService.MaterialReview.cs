using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

// Responsible-user review of the supplier's returned material before the supplier may submit for acceptance.
// See 主项目与子项目协作契约 (资料确认).
internal sealed partial class ProjectService
{
    internal Task<ProjectResponse> ApproveMaterialsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectMaterialReviewRequest request,
        string? ip,
        CancellationToken ct) =>
        ReviewMaterialsAsync(conn, actor, projectId, MaterialReviewStatuses.Approved,
            RequireExpectedSupplierFileId(request.ExpectedSupplierFileId), null, ip, ct);

    internal Task<ProjectResponse> ReturnMaterialsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectMaterialReviewRequest request,
        string? ip,
        CancellationToken ct)
    {
        var expectedSupplierFileId = RequireExpectedSupplierFileId(request.ExpectedSupplierFileId);
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw ApiException.BadRequest("退回原因不能为空");
        }
        if (RuneCount(reason) > 500)
        {
            throw ApiException.BadRequest("退回原因过长（最多 500 字）");
        }
        return ReviewMaterialsAsync(conn, actor, projectId, MaterialReviewStatuses.Returned,
            expectedSupplierFileId, reason, ip, ct);
    }

    private async Task<ProjectResponse> ReviewMaterialsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string decision,
        ulong expectedSupplierFileId,
        string? reason,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden("资料确认仅限项目负责人");
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
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
        if (ProjectWorkflowRules.EffectiveMaterialReview(project) == decision)
        {
            throw ApiException.Conflict(decision == MaterialReviewStatuses.Approved ? "资料已确认 OK" : "资料已退回");
        }
        if (await CountActiveUploadsAsync(conn, tx, projectId, ct) > 0)
        {
            throw ApiException.Conflict("项目仍有活动上传会话，请等待上传完成后再确认资料");
        }

        await using var db = EfDb.Use(conn, tx);
        var now = await DatabaseUtcNowAsync(db, ct);
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.MaterialReviewStatus, decision)
                .SetProperty(item => item.MaterialReviewFileId, latestSupplierFileId)
                .SetProperty(item => item.MaterialReviewReason, reason)
                .SetProperty(item => item.UpdatedAt, now), ct);
        await audit.WriteAsync(conn, tx, current.Id,
            decision == MaterialReviewStatuses.Approved ? "PROJECT_MATERIAL_APPROVE" : "PROJECT_MATERIAL_RETURN",
            "project", projectId, new { supplierFileId = latestSupplierFileId, reason }, ip, ct);
        await ProjectNotificationService.EnqueueMaterialReviewAsync(
            conn, tx, project, decision, reason, current, options.WebBaseUrl, audit, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
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
