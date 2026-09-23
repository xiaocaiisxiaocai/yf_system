using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectGroupStatusService(AuditService audit)
{
    internal async Task<string> RecalculateAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong groupId,
        ulong actorId,
        ulong? triggerProjectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var group = await db.ProjectGroups
            .FromSqlInterpolated($"SELECT * FROM project_groups WHERE id={groupId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("主项目不存在");

        var counts = await db.Projects
            .Where(project => project.ProjectGroupId == groupId)
            .GroupBy(project => project.Status)
            .Select(statuses => new { Status = statuses.Key, Count = statuses.LongCount() })
            .ToListAsync(ct);
        var total = checked((ulong)counts.Sum(item => item.Count));
        var draft = Count(ProjectStatuses.Draft);
        var completed = Count(ProjectStatuses.Completed);
        var terminated = Count(ProjectStatuses.Terminated);
        var next = DeriveStatus(total, draft, completed, terminated);
        if (next == group.Status) return next;

        var action = next switch
        {
            ProjectStatuses.Completed => "AUTO_COMPLETE",
            ProjectStatuses.Terminated => "AUTO_TERMINATE",
            ProjectStatuses.InProgress when group.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated => "AUTO_REOPEN",
            _ => "AUTO_SYNC",
        };
        var now = await DbClock.UtcNowAsync(db, ct, 3);
        var changed = await db.ProjectGroups
            .Where(item => item.Id == groupId && item.Status == group.Status)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, next)
                .SetProperty(item => item.CompletedAt, next == ProjectStatuses.Completed ? now : null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed != 1) throw ApiException.Conflict("主项目状态已被他人变更，请刷新后重试");

        db.ProjectGroupStatusLogs.Add(new ProjectGroupStatusLog
        {
            ProjectGroupId = groupId,
            FromStatus = group.Status,
            ToStatus = next,
            Action = action,
            TriggerProjectId = triggerProjectId,
            OperatorId = actorId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, actorId, "PROJECT_GROUP_STATUS_AUTO", "project_group", groupId, new
        {
            fromStatus = group.Status,
            toStatus = next,
            action,
            triggerProjectId,
            childCount = total,
            completedCount = completed,
            terminatedCount = terminated,
            changes = AuditChange.OnlyChanged(new AuditChange("status", "主项目状态", group.Status, next)),
        }, null, ct);
        return next;

        ulong Count(string status) => checked((ulong)(counts.SingleOrDefault(item => item.Status == status)?.Count ?? 0));
    }

    internal static string DeriveStatus(ulong total, ulong draft, ulong completed, ulong terminated) =>
        total == 0 || draft == total
            ? ProjectStatuses.Draft
            : completed == total
                ? ProjectStatuses.Completed
                : completed + terminated == total
                    ? ProjectStatuses.Terminated
                    : ProjectStatuses.InProgress;
}
