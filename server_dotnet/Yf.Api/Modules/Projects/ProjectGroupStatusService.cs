using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

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
        var current = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM project_groups WHERE id=@GroupId FOR UPDATE",
            new { GroupId = groupId }, tx, cancellationToken: ct)) ?? throw ApiException.NotFound("主项目不存在");
        var counts = await conn.QuerySingleAsync<StatusCounts>(new CommandDefinition(
            """
            SELECT COUNT(*) AS Total,
                   COALESCE(SUM(status='DRAFT'),0) AS DraftCount,
                   COALESCE(SUM(status='COMPLETED'),0) AS CompletedCount,
                   COALESCE(SUM(status='TERMINATED'),0) AS TerminatedCount
            FROM projects WHERE project_group_id=@GroupId
            """, new { GroupId = groupId }, tx, cancellationToken: ct));
        var next = DeriveStatus(counts.Total, counts.DraftCount, counts.CompletedCount, counts.TerminatedCount);
        if (next == current) return next;

        var action = next switch
        {
            ProjectStatuses.Completed => "AUTO_COMPLETE",
            ProjectStatuses.Terminated => "AUTO_TERMINATE",
            ProjectStatuses.InProgress when current is ProjectStatuses.Completed or ProjectStatuses.Terminated => "AUTO_REOPEN",
            _ => "AUTO_SYNC",
        };
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE project_groups
            SET status=@Next,completed_at=CASE WHEN @Next='COMPLETED' THEN UTC_TIMESTAMP(3) ELSE NULL END,
                updated_at=UTC_TIMESTAMP(3)
            WHERE id=@GroupId AND status=@Current
            """, new { Next = next, GroupId = groupId, Current = current }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_group_status_logs(project_group_id,from_status,to_status,action,trigger_project_id,operator_id,created_at)
            VALUES(@GroupId,@Current,@Next,@Action,@TriggerProjectId,@ActorId,UTC_TIMESTAMP(3))
            """, new { GroupId = groupId, Current = current, Next = next, Action = action, TriggerProjectId = triggerProjectId, ActorId = actorId },
            tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, actorId, "PROJECT_GROUP_STATUS_AUTO", "project_group", groupId, new
        {
            fromStatus = current,
            toStatus = next,
            action,
            triggerProjectId,
            childCount = counts.Total,
            completedCount = counts.CompletedCount,
            terminatedCount = counts.TerminatedCount,
            changes = AuditChange.OnlyChanged(new AuditChange("status", "主项目状态", current, next)),
        }, null, ct);
        return next;
    }

    internal static string DeriveStatus(ulong total, ulong draft, ulong completed, ulong terminated) =>
        total == 0 || draft == total
            ? ProjectStatuses.Draft
            : completed == total
                ? ProjectStatuses.Completed
                : completed + terminated == total
                    ? ProjectStatuses.Terminated
                    : ProjectStatuses.InProgress;

    private sealed class StatusCounts
    {
        public ulong Total { get; init; }
        public ulong DraftCount { get; init; }
        public ulong CompletedCount { get; init; }
        public ulong TerminatedCount { get; init; }
    }
}
