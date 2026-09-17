using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Projects;

internal sealed class MessageService(
    AuditService audit,
    AppOptions options,
    IProjectRealtimePublisher? realtime = null)
{
    internal const int MaximumImageCount = 9;
    internal const ulong MaximumImageBytes = 50UL * 1024 * 1024;
    internal const long MultipartRequestLimitBytes = 52L * 1024 * 1024;

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
        var images = await LoadImagesAsync(conn, null, rows.Select(message => message.Id), ct);
        var imagesByMessage = images.ToLookup(image => image.MessageId);
        var result = rows.Select(message => MessageJson(
            message, readsByMessage[message.Id], imagesByMessage[message.Id], visibleIds, actor.Id)).ToArray();
        return ProjectJson.Page(result, total, actualPage, size);
    }

    internal async Task EnsureCreateAllowedAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "message:create", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        EnsureWritable(project.Status);
        await tx.CommitAsync(ct);
    }

    internal async Task<object> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        MessageCreateRequest request,
        IReadOnlyList<IFormFile>? imageFiles,
        string? ip,
        CancellationToken ct)
    {
        var content = (request.Content ?? string.Empty).Trim();
        var files = imageFiles ?? [];
        if ((content.Length == 0 && files.Count == 0) || content.EnumerateRunes().Count() > 4000)
        {
            throw ApiException.BadRequest("留言需包含文字或图片，文字不超过 4000 字");
        }
        if (files.Count > MaximumImageCount)
            throw ApiException.BadRequest($"单条留言最多上传 {MaximumImageCount} 张图片");

        var systemMaximum = await ConfigUInt64Async(conn, "upload.max_file_size", (ulong)options.UploadMaxFileSize, ct);
        var perImageMaximum = Math.Min(MaximumImageBytes, systemMaximum);
        var declaredTotal = files.Aggregate(0UL, (total, file) => checked(total + (ulong)Math.Max(0, file.Length)));
        if (declaredTotal > MaximumImageBytes)
            throw ApiException.BadRequest("单次留言图片合计不能超过 50 MiB");
        if (files.Any(file => (ulong)Math.Max(0, file.Length) > perImageMaximum))
            throw ApiException.BadRequest($"单张图片超过大小上限 {perImageMaximum / 1024 / 1024} MiB");
        if (declaredTotal > 0) FileStorage.EnsureFreeSpace(options.StorageRoot, declaredTotal);

        var stored = new List<MessageImageRow>();
        var committed = false;
        try
        {
            foreach (var file in files)
            {
                stored.Add(await StoreImageAsync(file, perImageMaximum, ct));
                if (stored.Aggregate(0UL, (total, image) => checked(total + image.SizeBytes)) > MaximumImageBytes)
                    throw ApiException.BadRequest("单次留言图片合计不能超过 50 MiB");
            }

            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            {
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
                foreach (var image in stored)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO message_images
                            (message_id,original_name,stored_name,ext,size_bytes,mime_type,storage_path,created_at)
                        VALUES
                            (@MessageId,@OriginalName,@StoredName,@Ext,@SizeBytes,@MimeType,@StoragePath,UTC_TIMESTAMP(3))
                        """,
                        new
                        {
                            MessageId = messageId,
                            image.OriginalName,
                            image.StoredName,
                            image.Ext,
                            image.SizeBytes,
                            image.MimeType,
                            image.StoragePath,
                        },
                        tx,
                        cancellationToken: ct));
                }
                var notificationContent = content.Length == 0 ? "[图片]" : content;
                await ProjectNotificationService.EnqueueMessageAsync(conn, tx, project, messageId, notificationContent, current, options.WebBaseUrl, audit, ct);
                await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_CREATE", "message", messageId,
                    new { projectId, imageCount = stored.Count }, ip, ct);
                var message = await LoadMessageAsync(conn, tx, messageId, false, false, ct);
                var persistedImages = await LoadImagesAsync(conn, tx, [messageId], ct);
                var participants = await ProjectNotificationService.ParticipantsAsync(conn, tx, project, ct);
                var response = MessageJson(message, [], persistedImages,
                    participants.Select(user => user.Id).ToHashSet(), current.Id);
                await tx.CommitAsync(ct);
                committed = true;
                await PublishSafelyAsync(projectId, RealtimeChangeKinds.Messages);
                return response;
            }
        }
        catch
        {
            if (!committed)
                await CleanupUnreferencedAsync(conn, stored, CancellationToken.None);
            throw;
        }
    }

    internal Task<object> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        MessageCreateRequest request,
        string? ip,
        CancellationToken ct) =>
        CreateAsync(conn, actor, projectId, request, [], ip, ct);

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
            "SELECT id AS Id,project_id AS ProjectId FROM messages WHERE id IN @Ids AND status='NORMAL' AND sender_id<>@UserId",
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
                var changed = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT IGNORE INTO message_reads(message_id,user_id,read_at)
                    VALUES(@MessageId,@UserId,UTC_TIMESTAMP(3))
                    """,
                    new { MessageId = message.Id, UserId = current.Id }, tx, cancellationToken: ct));
                if (changed == 1)
                {
                    await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_READ", "message", message.Id,
                        new { projectId = group.Key }, null, ct);
                }
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

    internal async Task<MessageImageDownload> GetImageAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong messageId,
        ulong imageId,
        CancellationToken ct)
    {
        var message = await LoadMessageAsync(conn, null, messageId, false, true, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, message.ProjectId, ct);
        var image = await conn.QuerySingleOrDefaultAsync<MessageImageRow>(new CommandDefinition(
            """
            SELECT id AS Id,message_id AS MessageId,original_name AS OriginalName,stored_name AS StoredName,
                   ext AS Ext,size_bytes AS SizeBytes,mime_type AS MimeType,storage_path AS StoragePath,
                   created_at AS CreatedAt
            FROM message_images
            WHERE id=@ImageId AND message_id=@MessageId
            """,
            new { ImageId = imageId, MessageId = messageId },
            cancellationToken: ct)) ?? throw ApiException.NotFound();
        try
        {
            var path = await FileStorage.ResolveExistingFileAsync(
                options.StorageRoot, Path.Combine(options.StorageRoot, image.StoragePath), ct);
            return new(path, image.OriginalName, image.MimeType, image.SizeBytes);
        }
        catch (FileNotFoundException)
        {
            throw ApiException.NotFound();
        }
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong messageId,
        string? ip,
        CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var initial = await LoadMessageAsync(conn, null, messageId, false, false, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, initial.ProjectId, true, ct);
        var message = await LoadMessageAsync(conn, tx, messageId, true, false, ct);
        AccessService.RequireInternal(current);
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
        IEnumerable<MessageImageRow> images,
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
            images = images.Select(image => new
            {
                id = image.Id,
                name = image.OriginalName,
                sizeBytes = image.SizeBytes,
                mimeType = image.MimeType,
            }).ToArray(),
        };
    }

    private async Task<MessageImageRow> StoreImageAsync(
        IFormFile file,
        ulong maximumBytes,
        CancellationToken ct)
    {
        var originalName = file.FileName;
        if (string.IsNullOrWhiteSpace(originalName)
            || originalName.EnumerateRunes().Count() > 255
            || originalName.Any(character => char.IsControl(character) || character is '/' or '\\'))
            throw ApiException.BadRequest("图片文件名需为 1~255 个字符且不能包含路径或控制字符");
        if (file.Length <= 0) throw ApiException.BadRequest("空图片不可上传");

        var extension = Path.GetExtension(originalName).TrimStart('.').ToLowerInvariant();
        var expectedMime = extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "bmp" => "image/bmp",
            _ => throw ApiException.BadRequest("留言图片仅支持 PNG、JPEG、GIF、WebP、BMP"),
        };
        if ((ulong)file.Length > maximumBytes)
            throw ApiException.BadRequest($"单张图片超过大小上限 {maximumBytes / 1024 / 1024} MiB");

        var root = FileStorage.Root(options.StorageRoot);
        var now = DateTime.UtcNow;
        var storedName = $"{Guid.NewGuid():D}.{extension}";
        var directory = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(root, "message-images", now.ToString("yyyy"), now.ToString("MM")), false);
        Directory.CreateDirectory(directory);
        directory = await FileStorage.ResolveExistingAsync(root, directory, requireFile: false, ct);
        var path = FileStorage.EnsureLexicallyWithin(root, Path.Combine(directory, storedName), false);
        ulong total = 0;
        var header = new byte[12];
        var headerLength = 0;
        try
        {
            await using var input = file.OpenReadStream();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                total = checked(total + (uint)read);
                if (total > maximumBytes || total > MaximumImageBytes)
                    throw ApiException.BadRequest("单张图片超过大小上限");
                if (headerLength < header.Length)
                {
                    var copied = Math.Min(header.Length - headerLength, read);
                    buffer.AsSpan(0, copied).CopyTo(header.AsSpan(headerLength));
                    headerLength += copied;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (!HasExpectedSignature(extension, header.AsSpan(0, headerLength)))
                throw ApiException.BadRequest("图片内容与扩展名不匹配或文件已损坏");
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            await FileStorage.ResolveExistingFileAsync(root, path, ct);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        if (total == 0)
        {
            TryDelete(path);
            throw ApiException.BadRequest("空图片不可上传");
        }
        return new MessageImageRow
        {
            OriginalName = originalName,
            StoredName = storedName,
            Ext = extension,
            SizeBytes = total,
            MimeType = expectedMime,
            StoragePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            CreatedAt = now,
        };
    }

    internal static bool HasExpectedSignature(string extension, ReadOnlySpan<byte> header) => extension switch
    {
        "png" => header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4e && header[3] == 0x47
            && header[4] == 0x0d && header[5] == 0x0a && header[6] == 0x1a && header[7] == 0x0a,
        "jpg" or "jpeg" => header.Length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
        "gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
        "webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
        "bmp" => header.StartsWith("BM"u8),
        _ => false,
    };

    private async Task CleanupUnreferencedAsync(
        MySqlConnection conn,
        IReadOnlyCollection<MessageImageRow> images,
        CancellationToken ct)
    {
        foreach (var image in images)
        {
            try
            {
                var referenced = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM message_images WHERE stored_name=@StoredName)",
                    new { image.StoredName }, cancellationToken: ct));
                if (!referenced)
                {
                    var candidate = FileStorage.EnsureLexicallyWithin(options.StorageRoot,
                        Path.Combine(options.StorageRoot, image.StoragePath), false);
                    var path = await FileStorage.ResolveExistingFileAsync(options.StorageRoot, candidate, ct);
                    TryDelete(path);
                }
            }
            catch (FileNotFoundException) { }
            catch
            {
                // On an unknown database result, retaining a private orphan is safer than
                // deleting a file that may already be referenced by a committed message.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<MessageImageRow[]> LoadImagesAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        IEnumerable<ulong> messageIds,
        CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return (await conn.QueryAsync<MessageImageRow>(new CommandDefinition(
            """
            SELECT id AS Id,message_id AS MessageId,original_name AS OriginalName,stored_name AS StoredName,
                   ext AS Ext,size_bytes AS SizeBytes,mime_type AS MimeType,storage_path AS StoragePath,
                   created_at AS CreatedAt
            FROM message_images WHERE message_id IN @Ids ORDER BY id
            """,
            new { Ids = ids }, tx, cancellationToken: ct))).ToArray();
    }

    private static async Task<ulong> ConfigUInt64Async(
        MySqlConnection conn,
        string key,
        ulong fallback,
        CancellationToken ct)
    {
        var value = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key=@Key",
            new { Key = key }, cancellationToken: ct));
        return ulong.TryParse(value, out var parsed) ? parsed : fallback;
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
