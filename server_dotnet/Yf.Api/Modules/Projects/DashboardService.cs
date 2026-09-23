using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class DashboardService
{
    internal async Task<PageResponse<DashboardMessage>> MessagesAsync(
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
        var cutoff = await UnreadWindow.CutoffAsync(db, ct);

        var query = VisibleMessages(db, visibleProjects);
        if (unreadOnly) query = Unread(db, query, current.Id, cutoff);
        var total = (ulong)await query.LongCountAsync(ct);
        // Page over message ids first (newest first, walking the primary key), then load the page's details.
        var ids = await query.OrderByDescending(message => message.Id)
            .Page((actualPage - 1) * size, size)
            .Select(message => message.Id)
            .ToArrayAsync(ct);
        var list = await LoadMessagesAsync(db, query, ids, current.Id, cutoff, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(list, total, actualPage, size);
    }

    internal async Task<PageResponse<DashboardPendingProject>> PendingProjectsAsync(
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
            return ProjectJson.Page(Array.Empty<DashboardPendingProject>(), 0, actualPage, size);
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
        var list = rows.Select(row => new DashboardPendingProject(
            row.Id, row.Name, row.ProjectGroupId, row.ProjectGroupName, ProjectStatuses.PendingConfirmation,
            row.ConfirmSide == "COMPANY" ? "COMPANY" : "SUPPLIER", ProjectJson.Utc(row.UpdatedAt))).ToArray();
        await tx.CommitAsync(ct);
        return ProjectJson.Page(list, total, actualPage, size);
    }

    internal async Task<DashboardSummaryResponse> SummaryAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
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

        var cutoff = await UnreadWindow.CutoffAsync(db, ct);
        var messages = VisibleMessages(db, visibleProjects);
        var unreadMessages = (ulong)await Unread(db, messages, current.Id, cutoff).LongCountAsync(ct);
        var recentIds = await messages.OrderByDescending(message => message.Id).Take(5)
            .Select(message => message.Id).ToArrayAsync(ct);
        var recentMessages = await LoadMessagesAsync(db, messages, recentIds, current.Id, cutoff, ct);
        await tx.CommitAsync(ct);
        return new DashboardSummaryResponse(projectCount, activeProjectCount, pendingConfirmations, unreadMessages, recentMessages);
    }

    /// <summary>
    /// Normal messages in visible projects. Visibility is a semi-join (project_id IN visible projects) so
    /// MySQL can walk messages by primary key, newest first, and stop after one page.
    /// </summary>
    private static IQueryable<Message> VisibleMessages(YfDbContext db, IQueryable<Project> visibleProjects)
    {
        var projectIds = visibleProjects.Select(project => project.Id);
        return db.Messages.Where(message => message.Status == "NORMAL" && projectIds.Contains(message.ProjectId));
    }

    /// <summary>Messages from others, within the unread window, that the user has not read.</summary>
    private static IQueryable<Message> Unread(YfDbContext db, IQueryable<Message> messages, ulong userId, DateTime cutoff) =>
        messages.Where(message => message.SenderId != userId && message.CreatedAt >= cutoff
            && !db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == userId));

    /// <summary>
    /// Loads page details in ID order, reapplying visibility, NORMAL status and any unread filter.
    /// READ COMMITTED allows deletion, reassignment or a receipt between the two SELECTs.
    /// </summary>
    private static async Task<DashboardMessage[]> LoadMessagesAsync(
        YfDbContext db, IQueryable<Message> eligibleMessages, ulong[] ids, ulong userId, DateTime cutoff, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await (
            from message in eligibleMessages
            join project in db.Projects on message.ProjectId equals project.Id
            join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id
            join sender in db.Users on message.SenderId equals sender.Id
            where Enumerable.Contains(ids, message.Id)
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
                HasImages = db.MessageImages.Any(image => image.MessageId == message.Id),
                ReadByMe = db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == userId),
            }).ToDictionaryAsync(row => row.Id, ct);
        return ids.Where(rows.ContainsKey).Select(id => rows[id]).Select(row => new DashboardMessage(
            row.Id, row.ProjectId, row.ProjectName, row.ProjectGroupName, MessagePreview(row.Content, row.HasImages),
            row.SenderName, ProjectJson.Utc(row.CreatedAt),
            row.SenderId != userId && !row.ReadByMe && row.CreatedAt >= cutoff)).ToArray();
    }

    private static string MessagePreview(string content, bool hasImages) =>
        content.Length == 0 && hasImages ? "[图片]" : string.Concat(content.EnumerateRunes().Take(60));
}
