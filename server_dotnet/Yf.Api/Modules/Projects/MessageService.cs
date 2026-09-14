using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed class MessageService(
    AuditService audit,
    AppOptions options,
    IProjectRealtimePublisher? realtime = null)
{
    internal async Task<object> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ulong page,
        ulong pageSize,
        ulong? beforeId,
        ulong? targetId,
        CancellationToken ct)
    {
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        var project = await LoadProjectAsync(conn, null, projectId, false, ct);
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        var filter = "m.project_id=@ProjectId AND m.status='NORMAL'";
        if (targetId is not null)
        {
            filter += " AND m.id=@TargetId";
        }
        var parameters = new DynamicParameters(new { ProjectId = projectId, TargetId = targetId });
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            $"SELECT COUNT(*) FROM messages m WHERE {filter}", parameters, cancellationToken: ct));
        if (beforeId is not null)
        {
            filter += " AND m.id<@BeforeId";
            parameters.Add("BeforeId", beforeId.Value);
        }
        parameters.Add("Size", size);
        parameters.Add("Offset", beforeId is null ? (actualPage - 1) * size : 0);
        var rows = (await conn.QueryAsync<MessageRow>(new CommandDefinition(
            $"""
            SELECT m.id AS Id,m.project_id AS ProjectId,m.sender_id AS SenderId,m.content AS Content,
                   m.status AS Status,m.created_at AS CreatedAt,u.real_name AS SenderName,u.user_type AS SenderType
            FROM messages m INNER JOIN users u ON u.id=m.sender_id
            WHERE {filter}
            ORDER BY m.id DESC LIMIT @Size OFFSET @Offset
            """,
            parameters,
            cancellationToken: ct))).AsList();
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        var visibleIds = participants.Select(user => user.Id).ToHashSet();
        var reads = rows.Count == 0
            ? []
            : (await conn.QueryAsync<MessageReadRow>(new CommandDefinition(
                "SELECT message_id AS MessageId,user_id AS UserId,read_at AS ReadAt FROM message_reads WHERE message_id IN @Ids",
                new { Ids = rows.Select(message => message.Id).ToArray() }, cancellationToken: ct))).AsList();
        var readsByMessage = reads.ToLookup(read => read.MessageId);
        var result = rows.Select(message => MessageJson(message, readsByMessage[message.Id], visibleIds, actor.Id)).ToArray();
        return ProjectJson.Page(result, total, actualPage, size);
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        MessageCreateRequest request,
        string? ip,
        CancellationToken ct)
    {
        var content = (request.Content ?? string.Empty).Trim();
        if (content.Length == 0 || content.EnumerateRunes().Count() > 4000)
        {
            throw ApiException.BadRequest("留言内容为空或超长（≤4000 字）");
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "message:create", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        EnsureWritable(project.Status);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO messages(project_id,sender_id,content,status,deleted_by,deleted_at,created_at)
            VALUES(@ProjectId,@SenderId,@Content,'NORMAL',NULL,NULL,UTC_TIMESTAMP(3))
            """,
            new { ProjectId = projectId, SenderId = current.Id, Content = content },
            tx,
            cancellationToken: ct));
        var messageId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        await ProjectNotificationService.EnqueueMessageAsync(conn, tx, project, messageId, content, current, options.WebBaseUrl, audit, ct);
        await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_CREATE", "message", messageId, new { projectId }, ip, ct);
        var message = await LoadMessageAsync(conn, tx, messageId, false, false, ct);
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, tx, project, ct);
        var response = MessageJson(message, [], participants.Select(user => user.Id).ToHashSet(), current.Id);
        await tx.CommitAsync(ct);
        await PublishSafelyAsync(projectId, RealtimeChangeKinds.Messages);
        return response;
    }

    internal async Task MarkReadAsync(
        MySqlConnection conn,
        CurrentUser actor,
        MarkMessagesReadRequest request,
        CancellationToken ct)
    {
        var ids = request.Ids ?? [];
        if (ids.Length > 500)
        {
            throw ApiException.BadRequest("单次标记数量超过上限");
        }
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var changedProjects = new HashSet<ulong>();
        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }
        var rows = (await conn.QueryAsync<ReadTargetRow>(new CommandDefinition(
            "SELECT id AS Id,project_id AS ProjectId FROM messages WHERE id IN @Ids AND sender_id<>@UserId",
            new { Ids = ids.Distinct().ToArray(), UserId = current.Id }, tx, cancellationToken: ct))).AsList();
        foreach (var group in rows.GroupBy(message => message.ProjectId))
        {
            try
            {
                await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, group.Key, false, ct);
            }
            catch (ApiException)
            {
                continue;
            }
            foreach (var message in group)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO message_reads(message_id,user_id,read_at)
                    VALUES(@MessageId,@UserId,UTC_TIMESTAMP(3))
                    ON DUPLICATE KEY UPDATE read_at=VALUES(read_at)
                    """,
                    new { MessageId = message.Id, UserId = current.Id }, tx, cancellationToken: ct));
            }
            changedProjects.Add(group.Key);
        }
        await tx.CommitAsync(ct);
        foreach (var projectId in changedProjects)
            await PublishSafelyAsync(projectId, RealtimeChangeKinds.Receipts);
    }

    internal async Task<object> ReadsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong messageId,
        CancellationToken ct)
    {
        var message = await LoadMessageAsync(conn, null, messageId, false, true, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, message.ProjectId, ct);
        var project = await LoadProjectAsync(conn, null, message.ProjectId, false, ct);
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        var reads = (await conn.QueryAsync<MessageReadRow>(new CommandDefinition(
            "SELECT message_id AS MessageId,user_id AS UserId,read_at AS ReadAt FROM message_reads WHERE message_id=@MessageId",
            new { MessageId = messageId }, cancellationToken: ct))).ToDictionary(read => read.UserId);
        var readers = new List<object>();
        var unread = new List<object>();
        foreach (var user in participants.Where(user => user.Id != message.SenderId))
        {
            reads.TryGetValue(user.Id, out var read);
            var item = new
            {
                userId = user.Id,
                realName = user.RealName,
                userType = user.UserType,
                readAt = read is null ? (DateTime?)null : ProjectJson.Utc(read.ReadAt),
            };
            (read is null ? unread : readers).Add(item);
        }
        return new { readers, unread };
    }

    internal async Task<object[]> ReceiptsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        IReadOnlyCollection<ulong> messageIds,
        CancellationToken ct)
    {
        await ProjectAccessService.RequireViewAsync(conn, null, actor, projectId, ct);
        if (messageIds.Count == 0)
        {
            return [];
        }

        var project = await LoadProjectAsync(conn, null, projectId, false, ct);
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        var visibleIds = participants.Select(user => user.Id).ToHashSet();
        var targets = (await conn.QueryAsync<MessageReceiptTargetRow>(new CommandDefinition(
            """
            SELECT id AS Id,sender_id AS SenderId
            FROM messages
            WHERE project_id=@ProjectId AND status='NORMAL' AND id IN @Ids
            """,
            new { ProjectId = projectId, Ids = messageIds.ToArray() },
            cancellationToken: ct))).ToDictionary(message => message.Id);
        if (targets.Count == 0)
        {
            return [];
        }

        var reads = (await conn.QueryAsync<MessageReadRow>(new CommandDefinition(
            "SELECT message_id AS MessageId,user_id AS UserId,read_at AS ReadAt FROM message_reads WHERE message_id IN @Ids",
            new { Ids = targets.Keys.ToArray() },
            cancellationToken: ct))).ToLookup(read => read.MessageId);
        return messageIds
            .Where(targets.ContainsKey)
            .Select(messageId =>
            {
                var target = targets[messageId];
                var counts = ReceiptCounts(target.SenderId, reads[messageId], visibleIds, actor.Id);
                return (object)new
                {
                    id = messageId,
                    readCount = counts.ReadCount,
                    totalCount = counts.TotalCount,
                    readByMe = counts.ReadByMe,
                };
            })
            .ToArray();
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong messageId,
        string? ip,
        CancellationToken ct)
    {
        var initial = await LoadMessageAsync(conn, null, messageId, false, false, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, initial.ProjectId, true, ct);
        var message = await LoadMessageAsync(conn, tx, messageId, true, false, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "message:delete_any", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, message.ProjectId, false, ct);
        EnsureWritable(project.Status);
        if (message.Status != "NORMAL")
        {
            throw ApiException.NotFound();
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET status='DELETED',deleted_by=@DeletedBy,deleted_at=UTC_TIMESTAMP(3) WHERE id=@MessageId",
            new { DeletedBy = current.Id, MessageId = messageId }, tx, cancellationToken: ct));
        await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_DELETE", "message", messageId, null, ip, ct);
        await tx.CommitAsync(ct);
        await PublishSafelyAsync(message.ProjectId, RealtimeChangeKinds.Messages);
    }

    internal static async Task<ulong> UnreadCountAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong userId,
        ulong projectId,
        CancellationToken ct) =>
        await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM messages m
            WHERE m.project_id=@ProjectId AND m.status='NORMAL' AND m.sender_id<>@UserId
              AND NOT EXISTS(SELECT 1 FROM message_reads mr WHERE mr.message_id=m.id AND mr.user_id=@UserId)
            """,
            new { ProjectId = projectId, UserId = userId }, tx, cancellationToken: ct));

    private static object MessageJson(
        MessageRow message,
        IEnumerable<MessageReadRow> reads,
        IReadOnlySet<ulong> visibleUserIds,
        ulong viewerId)
    {
        var counts = ReceiptCounts(message.SenderId, reads, visibleUserIds, viewerId);
        return new
        {
            id = message.Id,
            projectId = message.ProjectId,
            content = message.Content,
            status = message.Status,
            senderId = message.SenderId,
            senderName = message.SenderName,
            senderType = message.SenderType,
            createdAt = ProjectJson.Utc(message.CreatedAt),
            readCount = counts.ReadCount,
            totalCount = counts.TotalCount,
            readByMe = counts.ReadByMe,
        };
    }

    private async Task PublishSafelyAsync(ulong projectId, string kind)
    {
        if (realtime is null) return;
        try { await realtime.PublishAsync(projectId, kind, CancellationToken.None); }
        catch { }
    }

    private static MessageReceiptCounts ReceiptCounts(
        ulong senderId,
        IEnumerable<MessageReadRow> reads,
        IReadOnlySet<ulong> visibleUserIds,
        ulong viewerId)
    {
        var readRows = reads.ToArray();
        return new(
            readRows.Count(read => read.UserId != senderId && visibleUserIds.Contains(read.UserId)),
            visibleUserIds.Count(id => id != senderId),
            readRows.Any(read => read.UserId == viewerId));
    }

    private static void EnsureWritable(string status)
    {
        if (status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
        {
            throw ApiException.Conflict("项目当前不可留言");
        }
    }

    private static async Task<ProjectRow> LoadProjectAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT id AS Id,name AS Name,description AS Description,supplier_id AS SupplierId,status AS Status,
                   confirm_side AS ConfirmSide,created_by AS CreatedBy,created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM projects WHERE id=@ProjectId
            """ + (forUpdate ? " FOR UPDATE" : string.Empty),
            new { ProjectId = projectId }, tx, cancellationToken: ct));
        return row ?? throw ApiException.NotFound();
    }

    private static async Task<MessageRow> LoadMessageAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong messageId,
        bool forUpdate,
        bool requireNormal,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<MessageRow>(new CommandDefinition(
            """
            SELECT m.id AS Id,m.project_id AS ProjectId,m.sender_id AS SenderId,m.content AS Content,
                   m.status AS Status,m.created_at AS CreatedAt,u.real_name AS SenderName,u.user_type AS SenderType
            FROM messages m INNER JOIN users u ON u.id=m.sender_id
            WHERE m.id=@MessageId
            """ + (requireNormal ? " AND m.status='NORMAL'" : string.Empty) + (forUpdate ? " FOR UPDATE" : string.Empty),
            new { MessageId = messageId }, tx, cancellationToken: ct));
        return row ?? throw ApiException.NotFound();
    }

    private sealed class ReadTargetRow
    {
        public ulong Id { get; init; }
        public ulong ProjectId { get; init; }
    }

    private sealed class MessageReceiptTargetRow
    {
        public ulong Id { get; init; }
        public ulong SenderId { get; init; }
    }

    private readonly record struct MessageReceiptCounts(int ReadCount, int TotalCount, bool ReadByMe);
}
