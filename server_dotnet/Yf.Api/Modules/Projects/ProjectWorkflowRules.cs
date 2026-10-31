using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectWorkflowRules
{
    internal static (string To, string Action) ManagementTransition(string from, string requested) =>
        (from, requested) switch
        {
            (ProjectStatuses.Draft, ProjectStatuses.InProgress) => (ProjectStatuses.InProgress, "START"),
            (ProjectStatuses.Terminated, ProjectStatuses.InProgress) => (ProjectStatuses.InProgress, "RESTART"),
            (ProjectStatuses.InProgress, ProjectStatuses.Terminated) => (ProjectStatuses.Terminated, "TERMINATE"),
            (_, ProjectStatuses.InProgress or ProjectStatuses.Terminated) =>
                throw ApiException.Conflict($"项目当前状态 {from} 不允许执行该管理动作"),
            _ => throw ApiException.BadRequest("仅允许开始、终止或重新开始项目"),
        };

    internal static void EnsureNoDeletionDependencies(bool hasContent, bool hasUploads)
    {
        if (hasContent)
        {
            throw ApiException.BadRequest("项目内仍有文件或留言，不能直接删除");
        }
        if (hasUploads)
        {
            throw ApiException.BadRequest("项目仍有上传记录，不能删除");
        }
    }

    internal static void EnsureDeletable(string status)
    {
        if (status is not (ProjectStatuses.Draft or ProjectStatuses.Terminated))
        {
            throw ApiException.BadRequest("仅草稿或已终止项目可以删除");
        }
    }

    /// <summary>
    /// A stored return only counts while it covers the latest available supplier file, so a new supplier
    /// upload (or removal of the returned file) sends the material back to PENDING without extra writes.
    /// Approval completes the subproject, so it never appears as an in-progress review state.
    /// </summary>
    internal static string? EffectiveMaterialReview(ProjectRow row) =>
        EffectiveMaterialReview(row.Status, row.MaterialReviewStatus, row.MaterialReviewFileId, row.LatestSupplierFileId);

    internal static string? EffectiveMaterialReview(
        string projectStatus, string? reviewStatus, ulong? reviewFileId, ulong? latestSupplierFileId)
    {
        if (projectStatus != ProjectStatuses.InProgress) return null;
        if (latestSupplierFileId is null) return MaterialReviewStatuses.NoMaterial;
        return reviewStatus == MaterialReviewStatuses.Returned && reviewFileId == latestSupplierFileId
            ? MaterialReviewStatuses.Returned
            : MaterialReviewStatuses.Pending;
    }
}

internal static class MaterialReviewStatuses
{
    internal const string NoMaterial = "NO_MATERIAL";
    internal const string Pending = "PENDING";
    internal const string Returned = "RETURNED";
}
