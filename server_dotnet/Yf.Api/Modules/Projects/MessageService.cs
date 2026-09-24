using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Projects;

internal sealed class MessageService(
    AuditService audit,
    AppOptions options,
    IProjectRealtimePublisher? realtime = null,
    ILogger<MessageService>? logger = null)
{
    internal const int MaximumImageCount = 9;
    internal const ulong MaximumImageBytes = 50UL * 1024 * 1024;
    internal const long MultipartRequestLimitBytes = 52L * 1024 * 1024;

    internal async Task<PageResponse<MessageResponse>> ListAsync(
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
        await using var db = EfDb.Use(conn);
        var query =
            from message in db.Messages
            join sender in db.Users on message.SenderId equals sender.Id
            where message.ProjectId == projectId && message.Status == "NORMAL"
            select new MessageRow
            {
                Id = message.Id,
                ProjectId = message.ProjectId,
                SenderId = message.SenderId,
                Content = message.Content,
                Status = message.Status,
                CreatedAt = message.CreatedAt,
                SenderName = sender.RealName,
                SenderType = sender.UserType,
            };
        if (targetId is not null) query = query.Where(message => message.Id == targetId.Value);
        var total = checked((ulong)await query.LongCountAsync(ct));
        if (beforeId is not null)
        {
            query = query.Where(message => message.Id < beforeId.Value);
        }
        var offset = beforeId is null ? (actualPage - 1) * size : 0;
        var rows = await query.OrderByDescending(message => message.Id)
            .Page(offset, size)
            .ToArrayAsync(ct);
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        var visibleIds = participants.Select(user => user.Id).ToHashSet();
        var messageIds = rows.Select(message => message.Id).ToArray();
        var reads = rows.Length == 0
            ? []
            : await db.MessageReads
                .Where(read => Enumerable.Contains(messageIds, read.MessageId))
                .Select(read => new MessageReadRow
                {
                    MessageId = read.MessageId,
                    UserId = read.UserId,
                    ReadAt = read.ReadAt,
                })
                .ToArrayAsync(ct);
        var readsByMessage = reads.ToLookup(read => read.MessageId);
        var images = await LoadImagesAsync(db, messageIds, ct);
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
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "message:create", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        EnsureWritable(project.Status);
        await tx.CommitAsync(ct);
    }

    internal async Task<MessageResponse> CreateAsync(
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
                await using var db = EfDb.Use(conn, tx);
                var databaseNow = await DatabaseUtcNowAsync(db, ct);
                var entity = new Message
                {
                    ProjectId = projectId,
                    SenderId = current.Id,
                    Content = content,
                    Status = "NORMAL",
                    CreatedAt = databaseNow,
                };
                db.Messages.Add(entity);
                await db.SaveChangesAsync(ct);
                var messageId = entity.Id;
                db.MessageImages.AddRange(stored.Select(image => new MessageImage
                {
                    MessageId = messageId,
                    OriginalName = image.OriginalName,
                    StoredName = image.StoredName,
                    Ext = image.Ext,
                    SizeBytes = image.SizeBytes,
                    MimeType = image.MimeType,
                    StoragePath = image.StoragePath,
                    CreatedAt = databaseNow,
                }));
                await db.SaveChangesAsync(ct);
                var notificationContent = content.Length == 0 ? "[图片]" : content;
                await ProjectNotificationService.EnqueueMessageAsync(conn, tx, project, messageId, notificationContent, current, options.WebBaseUrl, audit, ct);
                await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_CREATE", "message", messageId,
                    new { projectId, imageCount = stored.Count }, ip, ct);
                var message = await LoadMessageAsync(db, messageId, false, ct);
                var persistedImages = await LoadImagesAsync(db, [messageId], ct);
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

    internal Task<MessageResponse> CreateAsync(
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
        await using var db = EfDb.Use(conn, tx);
        var distinctIds = ids.Distinct().ToArray();
        var rows = await db.Messages
            .Where(message => Enumerable.Contains(distinctIds, message.Id)
                && message.Status == "NORMAL"
                && message.SenderId != current.Id)
            .Select(message => new ReadTargetRow
            {
                Id = message.Id,
                ProjectId = message.ProjectId,
            })
            .ToArrayAsync(ct);
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
            // Skip receipts that already exist, then insert the rest in one statement. The primary key is
            // the receipt contract; INSERT IGNORE keeps a concurrent acknowledgement of the same message idempotent.
            var groupIds = group.Select(message => message.Id).ToArray();
            var alreadyRead = (await db.MessageReads
                .Where(read => read.UserId == current.Id && Enumerable.Contains(groupIds, read.MessageId))
                .Select(read => read.MessageId).ToArrayAsync(ct)).ToHashSet();
            var newlyRead = groupIds.Where(id => !alreadyRead.Contains(id)).Order().ToArray();
            if (newlyRead.Length == 0) continue;
            var values = string.Join(",", newlyRead.Select((_, index) => $"(@m{index},@u,UTC_TIMESTAMP(3))"));
            var parameters = newlyRead.Select((id, index) => (object)new MySqlParameter($"@m{index}", id))
                .Append(new MySqlParameter("@u", current.Id)).ToArray();
            // Only generated parameter placeholders are concatenated; every value is a MySqlParameter.
            var sql = "INSERT IGNORE INTO message_reads(message_id,user_id,read_at) VALUES " + values;
            var inserted = await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
            if (inserted == 0) continue;
            // One audit entry per project and request (not per message): read receipts are high volume and
            // are deliberately not turned into project activity.
            await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_READ", "project", group.Key,
                new { projectId = group.Key, messageIds = newlyRead, count = newlyRead.Length }, null, ct);
            changedProjects.Add(group.Key);
        }
        await tx.CommitAsync(ct);
        foreach (var projectId in changedProjects)
            await PublishSafelyAsync(projectId, RealtimeChangeKinds.Receipts);
    }

    internal async Task<MessageReadsResponse> ReadsAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong messageId,
        CancellationToken ct)
    {
        var message = await LoadMessageAsync(conn, null, messageId, false, true, ct);
        await ProjectAccessService.RequireViewAsync(conn, null, actor, message.ProjectId, ct);
        var project = await LoadProjectAsync(conn, null, message.ProjectId, false, ct);
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        await using var db = EfDb.Use(conn);
        var reads = await db.MessageReads
            .Where(read => read.MessageId == messageId)
            .Select(read => new MessageReadRow
            {
                MessageId = read.MessageId,
                UserId = read.UserId,
                ReadAt = read.ReadAt,
            })
            .ToDictionaryAsync(read => read.UserId, ct);
        var readers = new List<MessageReader>();
        var unread = new List<MessageReader>();
        foreach (var user in participants.Where(user => user.Id != message.SenderId))
        {
            reads.TryGetValue(user.Id, out var read);
            var item = new MessageReader(user.Id, user.RealName, user.UserType,
                read is null ? null : ProjectJson.Utc(read.ReadAt));
            (read is null ? unread : readers).Add(item);
        }
        return new MessageReadsResponse(readers, unread);
    }

    internal async Task<MessageReceiptResponse[]> ReceiptsAsync(
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
        await using var db = EfDb.Use(conn);
        var targetIds = messageIds.ToArray();
        var targets = await db.Messages
            .Where(message => message.ProjectId == projectId
                && message.Status == "NORMAL"
                && Enumerable.Contains(targetIds, message.Id))
            .Select(message => new MessageReceiptTargetRow
            {
                Id = message.Id,
                SenderId = message.SenderId,
            })
            .ToDictionaryAsync(message => message.Id, ct);
        if (targets.Count == 0)
        {
            return [];
        }

        var foundIds = targets.Keys.ToArray();
        var reads = (await db.MessageReads
            .Where(read => Enumerable.Contains(foundIds, read.MessageId))
            .Select(read => new MessageReadRow
            {
                MessageId = read.MessageId,
                UserId = read.UserId,
                ReadAt = read.ReadAt,
            })
            .ToArrayAsync(ct)).ToLookup(read => read.MessageId);
        return messageIds
            .Where(targets.ContainsKey)
            .Select(messageId =>
            {
                var target = targets[messageId];
                var counts = ReceiptCounts(target.SenderId, reads[messageId], visibleIds, actor.Id);
                return new MessageReceiptResponse(messageId, counts.ReadCount, counts.TotalCount, counts.ReadByMe);
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
        await using var db = EfDb.Use(conn);
        var image = await db.MessageImages
            .Where(candidate => candidate.Id == imageId && candidate.MessageId == messageId)
            .Select(candidate => new MessageImageRow
            {
                Id = candidate.Id,
                MessageId = candidate.MessageId,
                OriginalName = candidate.OriginalName,
                StoredName = candidate.StoredName,
                Ext = candidate.Ext,
                SizeBytes = candidate.SizeBytes,
                MimeType = candidate.MimeType,
                StoragePath = candidate.StoragePath,
                CreatedAt = candidate.CreatedAt,
            })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        try
        {
            var path = FileStorage.ResolveExistingFile(
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
        await using (var db = EfDb.Use(conn, tx))
        {
            var databaseNow = await DatabaseUtcNowAsync(db, ct);
            await db.Messages
                .Where(candidate => candidate.Id == messageId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.Status, "DELETED")
                    .SetProperty(candidate => candidate.DeletedBy, current.Id)
                    .SetProperty(candidate => candidate.DeletedAt, databaseNow), ct);
        }
        await audit.WriteAsync(conn, tx, current.Id, "MESSAGE_DELETE", "message", messageId, null, ip, ct);
        await tx.CommitAsync(ct);
        await PublishSafelyAsync(message.ProjectId, RealtimeChangeKinds.Messages);
    }

    internal static async Task<ulong> UnreadCountAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong userId,
        ulong projectId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var cutoff = await UnreadWindow.CutoffAsync(db, ct);
        var count = await db.Messages.LongCountAsync(message =>
            message.ProjectId == projectId
            && message.Status == "NORMAL"
            && message.SenderId != userId
            && message.CreatedAt >= cutoff
            && !db.MessageReads.Any(read => read.MessageId == message.Id && read.UserId == userId), ct);
        return checked((ulong)count);
    }

    private static MessageResponse MessageJson(
        MessageRow message,
        IEnumerable<MessageReadRow> reads,
        IEnumerable<MessageImageRow> images,
        IReadOnlySet<ulong> visibleUserIds,
        ulong viewerId)
    {
        var counts = ReceiptCounts(message.SenderId, reads, visibleUserIds, viewerId);
        return new MessageResponse(
            message.Id,
            message.ProjectId,
            message.Content,
            message.Status,
            message.SenderId,
            message.SenderName,
            message.SenderType,
            ProjectJson.Utc(message.CreatedAt),
            counts.ReadCount,
            counts.TotalCount,
            counts.ReadByMe,
            images.Select(image => new MessageImageResponse(image.Id, image.OriginalName, image.SizeBytes, image.MimeType)).ToArray());
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
        directory = FileStorage.ResolveExisting(root, directory, requireFile: false, ct);
        var path = FileStorage.EnsureLexicallyWithin(root, Path.Combine(directory, storedName), false);
        ulong total = 0;
        var header = new byte[12];
        var headerLength = 0;
        try
        {
            await using var input = file.OpenReadStream();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
            FileStorage.ResolveExistingFile(root, path, ct);
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
                await using var db = EfDb.Use(conn);
                var referenced = await db.MessageImages.AnyAsync(
                    candidate => candidate.StoredName == image.StoredName, ct);
                if (!referenced)
                {
                    var candidate = FileStorage.EnsureLexicallyWithin(options.StorageRoot,
                        Path.Combine(options.StorageRoot, image.StoragePath), false);
                    var path = FileStorage.ResolveExistingFile(options.StorageRoot, candidate, ct);
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
        YfDbContext db,
        IEnumerable<ulong> messageIds,
        CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await db.MessageImages
            .Where(image => Enumerable.Contains(ids, image.MessageId))
            .OrderBy(image => image.Id)
            .Select(image => new MessageImageRow
            {
                Id = image.Id,
                MessageId = image.MessageId,
                OriginalName = image.OriginalName,
                StoredName = image.StoredName,
                Ext = image.Ext,
                SizeBytes = image.SizeBytes,
                MimeType = image.MimeType,
                StoragePath = image.StoragePath,
                CreatedAt = image.CreatedAt,
            })
            .ToArrayAsync(ct);
    }

    private static async Task<ulong> ConfigUInt64Async(
        MySqlConnection conn,
        string key,
        ulong fallback,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn);
        var value = await db.SystemConfigs
            .Where(config => config.CfgKey == key)
            .Select(config => config.CfgValue)
            .SingleOrDefaultAsync(ct);
        return ulong.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static Task<DateTime> DatabaseUtcNowAsync(YfDbContext db, CancellationToken ct) =>
        DbClock.UtcNowAsync(db, ct, 3);

    private async Task PublishSafelyAsync(ulong projectId, string kind)
    {
        if (realtime is null) return;
        try { await realtime.PublishAsync(projectId, kind, CancellationToken.None); }
        catch (OperationCanceledException error)
        {
            logger?.LogDebug(error,
                "Realtime message notification was canceled after commit for project {ProjectId}, change {ChangeKind}",
                projectId, kind);
        }
        catch (Exception error)
        {
            logger?.LogWarning(error,
                "Realtime message notification failed after commit for project {ProjectId}, change {ChangeKind}",
                projectId, kind);
        }
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
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate)
        {
            var locked = await db.Projects
                .FromSqlInterpolated($"SELECT * FROM projects WHERE id={projectId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
            if (locked is null) throw ApiException.NotFound();
        }
        return await ProjectQueries.Rows(db)
            .SingleOrDefaultAsync(project => project.Id == projectId, ct)
            ?? throw ApiException.NotFound();
    }

    private static async Task<MessageRow> LoadMessageAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong messageId,
        bool forUpdate,
        bool requireNormal,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate)
        {
            var locked = requireNormal
                ? await db.Messages
                    .FromSqlInterpolated($"SELECT * FROM messages WHERE id={messageId} AND status='NORMAL' FOR UPDATE")
                    .AsNoTracking()
                    .SingleOrDefaultAsync(ct)
                : await db.Messages
                    .FromSqlInterpolated($"SELECT * FROM messages WHERE id={messageId} FOR UPDATE")
                    .AsNoTracking()
                    .SingleOrDefaultAsync(ct);
            if (locked is null) throw ApiException.NotFound();
        }
        return await LoadMessageAsync(db, messageId, requireNormal, ct);
    }

    private static async Task<MessageRow> LoadMessageAsync(
        YfDbContext db,
        ulong messageId,
        bool requireNormal,
        CancellationToken ct)
    {
        var query =
            from message in db.Messages
            join sender in db.Users on message.SenderId equals sender.Id
            where message.Id == messageId
            select new MessageRow
            {
                Id = message.Id,
                ProjectId = message.ProjectId,
                SenderId = message.SenderId,
                Content = message.Content,
                Status = message.Status,
                CreatedAt = message.CreatedAt,
                SenderName = sender.RealName,
                SenderType = sender.UserType,
            };
        if (requireNormal) query = query.Where(message => message.Status == "NORMAL");
        return await query.SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
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
