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
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "dashboard", ct);
        await using var db = EfDb.Use(conn, tx);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var cutoff = await UnreadWindow.CutoffAsync(db, ct);

        var query = VisibleMessages(db, visibleProjects, cutoff);
        if (unreadOnly) query = Unread(db, query, current.Id, cutoff);
        var pageResult = await LoadMessagePageAsync(
            db, query, query, (actualPage - 1) * size, size, current.Id, cutoff, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(pageResult.List, pageResult.Count, actualPage, size);
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
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
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
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
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
        var messages = VisibleMessages(db, visibleProjects, cutoff);
        var messageResult = await LoadMessagePageAsync(
            db, messages, Unread(db, messages, current.Id, cutoff), 0, 5, current.Id, cutoff, ct);
        await tx.CommitAsync(ct);
        return new DashboardSummaryResponse(projectCount, activeProjectCount, pendingConfirmations,
            messageResult.Count, messageResult.List);
    }

    /// <summary>
    /// Normal messages in visible projects. Visibility is a semi-join (project_id IN visible projects) so
    /// MySQL can walk messages by primary key, newest first, and stop after one page.
    /// </summary>
    private static IQueryable<Message> VisibleMessages(
        YfDbContext db, IQueryable<Project> visibleProjects, DateTime cutoff)
    {
        var projectIds = visibleProjects.Select(project => project.Id);
        return db.Messages.Where(message => message.Status == "NORMAL" && message.CreatedAt >= cutoff
            && projectIds.Contains(message.ProjectId));
    }

    /// <summary>Messages from others, within the unread window, that the user has not read.</summary>
    private static IQueryable<Message> Unread(YfDbContext db, IQueryable<Message> messages, ulong userId, DateTime cutoff) =>
        messages.Where(message => message.SenderId != userId && message.CreatedAt >= cutoff
            && !db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == userId));

    /// <summary>
    /// Loads page details and the caller's one required count in one statement. This keeps the filtered
    /// count and returned details on the same READ COMMITTED statement snapshot without a redundant scan.
    /// </summary>
    private static async Task<MessagePageResult> LoadMessagePageAsync(
        YfDbContext db,
        IQueryable<Message> detailMessages,
        IQueryable<Message> countMessages,
        ulong offset,
        ulong size,
        ulong userId,
        DateTime cutoff,
        CancellationToken ct)
    {
        var details =
            from message in detailMessages
            join project in db.Projects on message.ProjectId equals project.Id
            join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id
            join sender in db.Users on message.SenderId equals sender.Id
            select new MessageDetailRow
            {
                Key = 1,
                Id = message.Id,
                ProjectId = message.ProjectId,
                ProjectName = project.Name,
                ProjectGroupName = projectGroup.Name,
                Content = message.Content,
                SenderId = message.SenderId,
                CreatedAt = message.CreatedAt,
                SenderName = sender.RealName,
                HasImages = db.MessageImages.Any(image => image.MessageId == message.Id),
                ReadByMe = db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == userId),
            };
        var page = details.OrderByDescending(row => row.Id).Page(offset, size);
        // Anchor the one aggregate row on the required management config so even an empty page returns
        // its count in the same statement. The count subquery is evaluated once, not once per detail row.
        var counts = countMessages.GroupBy(_ => 1)
            .Select(group => new MessageCountRow { Key = group.Key, Count = group.LongCount() });
        var anchor = db.SystemConfigs.OrderBy(config => config.CfgKey).Take(1).Select(_ => 1);
        var count = from key in anchor
                    join aggregate in counts on key equals aggregate.Key into matches
                    from aggregate in matches.DefaultIfEmpty()
                    select new MessageCountRow { Key = key, Count = aggregate.Count ?? 0 };
        var rows = await (
            from aggregate in count
            join detail in page on aggregate.Key equals detail.Key into pageDetails
            from detail in pageDetails.DefaultIfEmpty()
            orderby detail.Id descending
            select new MessagePageRow
            {
                Count = aggregate.Count ?? 0,

                Id = detail.Id,
                ProjectId = detail.ProjectId,
                ProjectName = detail == null ? null : detail.ProjectName,
                ProjectGroupName = detail == null ? null : detail.ProjectGroupName,
                Content = detail == null ? null : detail.Content,
                SenderId = detail.SenderId,
                CreatedAt = detail.CreatedAt,
                SenderName = detail == null ? null : detail.SenderName,
                HasImages = detail.HasImages,
                ReadByMe = detail.ReadByMe,
            }).ToArrayAsync(ct);
        var aggregateCount = (ulong)rows[0].Count;
        var list = rows.Where(row => row.Id.HasValue).Select(row => new DashboardMessage(
            row.Id!.Value, row.ProjectId!.Value, row.ProjectName!, row.ProjectGroupName!, MessagePreview(row.Content!, row.HasImages ?? false),
            row.SenderName!, ProjectJson.Utc(row.CreatedAt!.Value),
            row.SenderId != userId && row.ReadByMe != true && row.CreatedAt >= cutoff)).ToArray();
        return new(aggregateCount, list);
    }

    private static string MessagePreview(string content, bool hasImages) =>
        content.Length == 0 && hasImages ? "[图片]" : string.Concat(content.EnumerateRunes().Take(60));

    private sealed record MessagePageResult(ulong Count, DashboardMessage[] List);

    private sealed class MessageCountRow
    {
        public int Key { get; init; }
        public long? Count { get; init; }
    }

    private sealed class MessageDetailRow
    {
        public int Key { get; init; }
        public ulong? Id { get; init; }
        public ulong? ProjectId { get; init; }
        public string ProjectName { get; init; } = string.Empty;
        public string ProjectGroupName { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public ulong? SenderId { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string SenderName { get; init; } = string.Empty;
        public bool? HasImages { get; init; }
        public bool? ReadByMe { get; init; }
    }

    private sealed class MessagePageRow
    {
        public long Count { get; init; }

        public ulong? Id { get; init; }
        public ulong? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public string? ProjectGroupName { get; init; }
        public string? Content { get; init; }
        public ulong? SenderId { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string? SenderName { get; init; }
        public bool? HasImages { get; init; }
        public bool? ReadByMe { get; init; }
    }
}
