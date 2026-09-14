using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectWorkflowRules
{
    internal const string InternalAcceptanceSide = "COMPANY";

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

    internal static void EnsureDeletable(string status, bool hasContent, bool hasUploads)
    {
        if (status is ProjectStatuses.InProgress or ProjectStatuses.PendingConfirmation or ProjectStatuses.Completed)
        {
            throw ApiException.BadRequest("进行中、待确认或已完成的项目不能删除");
        }
        if (hasContent)
        {
            throw ApiException.BadRequest("项目内仍有文件或留言，不能直接删除");
        }
        if (hasUploads)
        {
            throw ApiException.BadRequest("项目仍有上传记录，不能删除");
        }
    }

    internal static ulong[] NormalizeMemberIds(IReadOnlyCollection<ulong> requestedIds, ulong actorId)
    {
        if (requestedIds.Count > 200)
        {
            throw ApiException.BadRequest("成员数量超过上限");
        }

        var ids = requestedIds.Append(actorId).Distinct().Order().ToArray();
        if (ids.Length > 200)
        {
            throw ApiException.BadRequest("成员数量超过上限");
        }
        return ids;
    }

    internal static string NormalizeConfirmSide(string? value)
    {
        if (value is null)
        {
            return InternalAcceptanceSide;
        }

        return value.Trim() switch
        {
            InternalAcceptanceSide => InternalAcceptanceSide,
            _ => throw ApiException.BadRequest("项目验收仅支持公司内部确认，confirmSide 必须为 COMPANY"),
        };
    }

    internal static void RequireInternalDecisionActor(CurrentUser actor)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden("项目验收确认和驳回仅限公司内部用户");
        }
    }

    internal static bool CanReceivePendingAcceptance(CurrentUser actor, bool hasConfirmPermission) =>
        actor.IsInternal && hasConfirmPermission;
}
