using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class DashboardService
{
    internal async Task<object> MessagesAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        bool unreadOnly,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        await using var db = EfDb.Use(conn, tx);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);

        var query =
            from message in db.Messages
            join project in visibleProjects on message.ProjectId equals project.Id
            join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id
            join sender in db.Users on message.SenderId equals sender.Id
            let readByMe = db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == current.Id)
            let hasImages = db.MessageImages.Any(image => image.MessageId == message.Id)
            where message.Status == "NORMAL"
                && (!unreadOnly || (message.SenderId != current.Id && !readByMe))
            select new
            {
                message.Id,
                message.ProjectId,
                ProjectName = project.Name,
                ProjectGroupName = projectGroup.Name,
                message.Content,
                message.SenderId,
                message.CreatedAt,
                SenderName = sender.RealName,
                HasImages = hasImages,
                ReadByMe = readByMe,
            };

        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(row => row.Id)
            .Page((actualPage - 1) * size, size)
            .ToArrayAsync(ct);
        var list = rows.Select(row => new
        {
            id = row.Id,
            projectId = row.ProjectId,
            projectName = row.ProjectName,
            projectGroupName = row.ProjectGroupName,
            content = MessagePreview(row.Content, row.HasImages),
            senderName = row.SenderName,
            createdAt = ProjectJson.Utc(row.CreatedAt),
            unread = row.SenderId != current.Id && !row.ReadByMe,
        }).ToArray();
        await tx.CommitAsync(ct);
        return ProjectJson.Page(list, total, actualPage, size);
    }

    internal async Task<object> PendingProjectsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        var canReceivePendingAcceptance = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct))
            && await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:list", ct);
        if (current.IsInternal && !canReceivePendingAcceptance)
        {
            await tx.CommitAsync(ct);
            return ProjectJson.Page(Array.Empty<object>(), 0, actualPage, size);
        }

        await using var db = EfDb.Use(conn, tx);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var query =
            from project in visibleProjects
            join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id
            where project.Status == ProjectStatuses.PendingConfirmation
                && project.ConfirmSide == ProjectWorkflowRules.InternalAcceptanceSide
            select new
            {
                project.Id,
                project.Name,
                project.ProjectGroupId,
                ProjectGroupName = projectGroup.Name,
                project.ConfirmSide,
                project.UpdatedAt,
            };
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(row => row.UpdatedAt)
            .ThenByDescending(row => row.Id)
            .Page((actualPage - 1) * size, size)
            .ToArrayAsync(ct);
        var list = rows.Select(row => new
        {
            id = row.Id,
            name = row.Name,
            projectGroupId = row.ProjectGroupId,
            projectGroupName = row.ProjectGroupName,
            status = ProjectStatuses.PendingConfirmation,
            confirmSide = row.ConfirmSide == "COMPANY" ? "COMPANY" : "SUPPLIER",
            updatedAt = ProjectJson.Utc(row.UpdatedAt),
        }).ToArray();
        await tx.CommitAsync(ct);
        return ProjectJson.Page(list, total, actualPage, size);
    }

    internal async Task<object> SummaryAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        var canConfirm = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:confirm", ct))
            && await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:list", ct);

        await using var db = EfDb.Use(conn, tx);
        var visibleGroups = await ProjectGroupAccessService.VisibleQueryAsync(db, current, ct);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var projectCount = (ulong)await visibleGroups.LongCountAsync(ct);
        var activeProjectCount = (ulong)await visibleGroups.LongCountAsync(
            group => group.Status == ProjectStatuses.InProgress, ct);
        var pendingConfirmations = current.IsInternal && !canConfirm
            ? 0UL
            : (ulong)await visibleProjects.LongCountAsync(project =>
                project.Status == ProjectStatuses.PendingConfirmation
                && project.ConfirmSide == ProjectWorkflowRules.InternalAcceptanceSide, ct);

        var messages =
            from message in db.Messages
            join project in visibleProjects on message.ProjectId equals project.Id
            where message.Status == "NORMAL"
            select new { Message = message, Project = project };
        var unreadMessages = (ulong)await messages.LongCountAsync(row =>
            row.Message.SenderId != current.Id
            && !db.MessageReads.Any(read => read.MessageId == row.Message.Id && read.UserId == current.Id), ct);
        var recentRows = await (
            from row in messages
            join projectGroup in db.ProjectGroups on row.Project.ProjectGroupId equals projectGroup.Id
            join sender in db.Users on row.Message.SenderId equals sender.Id
            let readByMe = db.MessageReads.Any(read => read.MessageId == row.Message.Id && read.UserId == current.Id)
            let hasImages = db.MessageImages.Any(image => image.MessageId == row.Message.Id)
            orderby row.Message.Id descending
            select new
            {
                row.Message.Id,
                row.Message.ProjectId,
                ProjectName = row.Project.Name,
                ProjectGroupName = projectGroup.Name,
                row.Message.Content,
                row.Message.SenderId,
                row.Message.CreatedAt,
                SenderName = sender.RealName,
                HasImages = hasImages,
                ReadByMe = readByMe,
            }).Take(5).ToArrayAsync(ct);
        var recentMessages = recentRows.Select(row => (object)new
        {
            id = row.Id,
            projectId = row.ProjectId,
            projectName = row.ProjectName,
            projectGroupName = row.ProjectGroupName,
            content = MessagePreview(row.Content, row.HasImages),
            senderName = row.SenderName,
            createdAt = ProjectJson.Utc(row.CreatedAt),
            unread = row.SenderId != current.Id && !row.ReadByMe,
        }).ToArray();
        await tx.CommitAsync(ct);
        return new
        {
            projectCount,
            activeProjectCount,
            pendingConfirmations,
            unreadMessages,
            recentMessages,
        };
    }

    private static string MessagePreview(string content, bool hasImages) =>
        content.Length == 0 && hasImages ? "[图片]" : string.Concat(content.EnumerateRunes().Take(60));
}
