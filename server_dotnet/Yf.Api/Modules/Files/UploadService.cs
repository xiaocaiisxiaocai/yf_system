using Dapper;
using MySqlConnector;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Files;

public sealed partial class UploadService(
    AppDb db,
    AppOptions options,
    AuditService audit,
    ILogger<UploadService> logger)
{
    private const uint MinimumChunkSize = 256 * 1024;
    private const uint MaximumChunkSize = 64 * 1024 * 1024;

    public async Task<object> InitAsync(HttpContext context, InitUploadRequest request, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await ProjectAccessService.RequireFileUploadAsync(conn, null, actor, request.ProjectId, ct);
        var extension = await ValidateFileAsync(conn, request, ct);
        if (request.FileMd5 is not null && !Md5Pattern().IsMatch(request.FileMd5))
            throw ApiException.BadRequest("文件 MD5 摘要格式无效");
        var fileMd5 = request.FileMd5?.ToLowerInvariant();

        var chunkSize = (uint)Math.Clamp(await ConfigUInt64Async(conn, "upload.chunk_size", (ulong)options.UploadChunkSize, ct),
            MinimumChunkSize, MaximumChunkSize);
        var totalChunks64 = request.FileSize / chunkSize + (request.FileSize % chunkSize == 0 ? 0UL : 1UL);
        if (totalChunks64 > uint.MaxValue) throw ApiException.BadRequest("文件分片数量超出限制");
        var totalChunks = (uint)totalChunks64;
        FileStorage.EnsureFreeSpace(options.StorageRoot, request.FileSize);

        MySqlNamedLock? identityLease = null;
        if (fileMd5 is not null)
        {
            var identityLockName = MySqlNamedLock.Name("upload-init", conn.Database,
                request.ProjectId, actor.Id, request.FileName, request.FileSize, fileMd5);
            identityLease = await MySqlNamedLock.TryAcquireAsync(conn, identityLockName, 10, ct)
                ?? throw ApiException.Conflict("相同文件正在初始化，请稍后重试");
        }
        await using (identityLease)
        {
            UploadSessionRow? existing = null;
            if (fileMd5 is not null)
            {
                existing = await conn.QuerySingleOrDefaultAsync<UploadSessionRow>(new CommandDefinition("""
                SELECT id AS Id,project_id AS ProjectId,uploader_id AS UploaderId,file_name AS FileName,
                       file_size AS FileSize,file_md5 AS FileMd5,chunk_size AS ChunkSize,total_chunks AS TotalChunks,
                       temp_dir AS TempDir,status AS Status,result_file_id AS ResultFileId,
                       expires_at AS ExpiresAt,created_at AS CreatedAt,updated_at AS UpdatedAt
                FROM upload_sessions
                WHERE project_id=@ProjectId AND uploader_id=@UploaderId AND file_name=@FileName
                  AND file_size=@FileSize AND file_md5=@FileMd5 AND expires_at>UTC_TIMESTAMP(6)
                  AND status IN ('UPLOADING','MERGING')
                ORDER BY created_at DESC LIMIT 1
                """, new { request.ProjectId, UploaderId = actor.Id, request.FileName, request.FileSize, FileMd5 = fileMd5 }, cancellationToken: ct));
            }
            if (existing is not null)
            {
                if (existing.Status == "MERGING")
                {
                    var mergeLockName = MergeLockName(conn, existing.Id);
                    await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(conn, mergeLockName, 0, ct)
                        ?? throw ApiException.Conflict("该文件正在合并，请稍候");
                    existing = await ResetOrphanedMergeAsync(conn, actor, existing, ct);
                }
                return await InitResponseAsync(existing, resumed: true, ct);
            }

            var sessionId = Guid.NewGuid().ToString("D");
            var root = FileStorage.Root(options.StorageRoot);
            var tempDir = FileStorage.SessionDirectory(root, sessionId);
            Directory.CreateDirectory(tempDir);
            await FileStorage.ResolveExistingAsync(root, tempDir, requireFile: false, ct);
            var now = TruncateMilliseconds(DateTime.UtcNow);
            try
            {
                await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
                await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, request.ProjectId, ct);
                await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO upload_sessions
                    (id,project_id,uploader_id,file_name,file_size,file_md5,chunk_size,total_chunks,temp_dir,status,result_file_id,expires_at,created_at,updated_at)
                VALUES
                    (@Id,@ProjectId,@UploaderId,@FileName,@FileSize,@FileMd5,@ChunkSize,@TotalChunks,@TempDir,'UPLOADING',NULL,@ExpiresAt,@Now,@Now)
                """, new
                {
                    Id = sessionId, request.ProjectId, UploaderId = current.Id, request.FileName,
                    request.FileSize, FileMd5 = fileMd5, ChunkSize = chunkSize, TotalChunks = totalChunks,
                    TempDir = tempDir, ExpiresAt = now.AddHours(24), Now = now
                }, tx, cancellationToken: ct));
                try { await tx.CommitAsync(ct); }
                catch
                {
                    using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (!await SessionExistsSafelyAsync(sessionId, reconcile.Token))
                        throw;
                }
            }
            catch
            {
                if (!await SessionExistsSafelyAsync(sessionId, ct))
                    await TryDeleteDirectoryAsync(options.StorageRoot, tempDir, CancellationToken.None);
                throw;
            }
            _ = extension;
            return new { sessionId, chunkSize, totalChunks, uploadedChunks = Array.Empty<uint>() };
        }
    }

    public async Task<object> GetAsync(HttpContext context, string sessionId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var session = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (session.UploaderId != actor.Id) throw ApiException.Forbidden();
        await ProjectAccessService.RequireViewAsync(conn, null, actor, session.ProjectId, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:upload", ct);
        if (session.Status == "UPLOADING" && session.ExpiresAt < DateTime.UtcNow)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        var chunks = await UploadedChunksAsync(session, ct);
        return new
        {
            sessionId = session.Id, status = session.Status, chunkSize = session.ChunkSize,
            totalChunks = session.TotalChunks, uploadedChunks = chunks, fileName = session.FileName,
            fileSize = session.FileSize, resultFileId = session.ResultFileId
        };
    }

    public async Task PutChunkAsync(HttpContext context, string sessionId, int index, Stream body, CancellationToken ct)
    {
        if (index < 0) throw ApiException.BadRequest("分片序号越界");
        var actor = AccessService.GetCurrent(context);
        UploadSessionRow initial;
        await using (var initialConnection = await db.OpenAsync(ct))
        {
            initial = await LoadSessionAsync(initialConnection, null, sessionId, false, ct);
            if (initial.UploaderId != actor.Id) throw ApiException.Forbidden();
            await ProjectAccessService.RequireFileUploadAsync(
                initialConnection, null, actor, initial.ProjectId, ct);
        }
        if (initial.Status != "UPLOADING") throw ApiException.Conflict("会话不可上传（可能已合并或放弃）");
        if (initial.ExpiresAt < DateTime.UtcNow) throw ApiException.Conflict("上传会话已过期，请重新发起");
        if ((uint)index >= initial.TotalChunks) throw ApiException.BadRequest("分片序号越界");
        var expected = (uint)index == initial.TotalChunks - 1
            ? initial.FileSize - (ulong)initial.ChunkSize * (initial.TotalChunks - 1)
            : initial.ChunkSize;
        if (context.Request.ContentLength is long contentLength && (ulong)contentLength != expected)
            throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {contentLength}");
        FileStorage.EnsureFreeSpace(options.StorageRoot, expected);

        var root = FileStorage.Root(options.StorageRoot);
        var directory = FileStorage.SessionDirectory(root, sessionId);
        Directory.CreateDirectory(directory);
        await FileStorage.ResolveExistingAsync(root, directory, requireFile: false, ct);
        var path = FileStorage.ChunkPath(root, sessionId, (uint)index);
        var temporary = FileStorage.EnsureLexicallyWithin(root,
            path + $".{Guid.NewGuid():D}.uploading", false);
        try
        {
            await WriteExactAsync(body, temporary, expected, ct);
            // Network receive is complete before any business or project row is locked.
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
            await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, initial.ProjectId, ct);
            var session = await LoadSessionAsync(conn, tx, sessionId, true, ct);
            if (session.UploaderId != current.Id) throw ApiException.Forbidden();
            if (session.ProjectId != initial.ProjectId)
                throw ApiException.Conflict("上传会话所属项目已变化，请重新查询");
            if (session.Status != "UPLOADING") throw ApiException.Conflict("会话不可上传（可能已合并或放弃）");
            if (session.ExpiresAt < DateTime.UtcNow) throw ApiException.Conflict("上传会话已过期，请重新发起");
            if ((uint)index >= session.TotalChunks) throw ApiException.BadRequest("分片序号越界");
            var lockedExpected = (uint)index == session.TotalChunks - 1
                ? session.FileSize - (ulong)session.ChunkSize * (session.TotalChunks - 1)
                : session.ChunkSize;
            if (lockedExpected != expected) throw ApiException.Conflict("上传会话参数已变化，请重新查询");
            if (File.Exists(path) && (ulong)new FileInfo(path).Length == expected)
            {
                await tx.CommitAsync(ct);
                return;
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path, overwrite: false);
            await tx.CommitAsync(ct);
        }
        finally { TryDeleteFile(temporary); }
    }

    public async Task AbortAsync(HttpContext context, string sessionId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var initial = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (initial.UploaderId != actor.Id) throw ApiException.Forbidden();
        await ProjectAccessService.RequireViewAsync(conn, null, actor, initial.ProjectId, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:upload", ct);

        // Always take the merge lease, including when the initial snapshot still says
        // UPLOADING. This closes the race where another request changes it to MERGING
        // between the snapshot and the row lock below.
        await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(
            conn, MergeLockName(conn, sessionId), 0, ct)
            ?? throw ApiException.Conflict("正在合并中，请稍候");
        await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
        {
            var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
            var session = await LoadSessionAsync(conn, tx, sessionId, true, ct);
            if (session.UploaderId != current.Id) throw ApiException.Forbidden();
            await ProjectAccessService.RequireViewAsync(conn, tx, current, session.ProjectId, ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "file:upload", ct);
            if (session.Status == "COMPLETED") throw ApiException.Conflict("会话已完成，不可放弃");
            if (session.Status is not ("UPLOADING" or "MERGING" or "EXPIRED" or "ABORTED"))
                throw ApiException.Conflict("会话已失效");
            if (session.Status != "ABORTED")
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE upload_sessions SET status='ABORTED',updated_at=UTC_TIMESTAMP(6) WHERE id=@Id",
                    new { Id = sessionId }, tx, cancellationToken: ct));
                await WriteUploadAbortAuditAsync(conn, tx, current, sessionId,
                    ClientIp.Resolve(context, options), ct);
            }
            await tx.CommitAsync(ct);
        }
        await TryCleanupSessionArtifactsAsync(conn, sessionId, CancellationToken.None);
    }

    public async Task<object> MergeAsync(HttpContext context, string sessionId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var session = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (session.UploaderId != actor.Id) throw ApiException.Forbidden();
        await ProjectAccessService.RequireViewAsync(conn, null, actor, session.ProjectId, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:upload", ct);
        if (session.Status == "COMPLETED") return await CompletedFileAsync(conn, session, actor.Id, ct);
        if (session.Status is not ("UPLOADING" or "MERGING")) throw ApiException.Conflict("会话已失效");
        if (session.Status == "UPLOADING" && session.ExpiresAt < DateTime.UtcNow)
            throw ApiException.Conflict("上传会话已过期，请重新发起");

        await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(
            conn, MergeLockName(conn, sessionId), 0, ct)
            ?? throw ApiException.Conflict("正在合并中，请稍候");
        session = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (session.Status == "COMPLETED") return await CompletedFileAsync(conn, session, actor.Id, ct);
        if (session.Status is not ("UPLOADING" or "MERGING")) throw ApiException.Conflict("会话已失效");
        if (session.Status == "UPLOADING" && session.ExpiresAt < DateTime.UtcNow)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        await ProjectAccessService.RequireFileUploadAsync(conn, null, actor, session.ProjectId, ct);
        var uploaded = await UploadedChunksAsync(session, ct);
        if ((uint)uploaded.Count != session.TotalChunks)
            throw ApiException.BadRequest($"分片不完整：已传 {uploaded.Count}/{session.TotalChunks}");
        FileStorage.EnsureFreeSpace(options.StorageRoot, session.FileSize);
        session = await ClaimMergeAsync(conn, actor, session, ct);
        var lease = session.UpdatedAt;
        try
        {
            var result = await DoMergeAsync(conn, context, actor, session, ct);
            await TryCleanupSessionArtifactsAsync(conn, session.Id, CancellationToken.None);
            return result;
        }
        catch
        {
            await ResetMergeLeaseSafelyAsync(session.Id, lease, CancellationToken.None);
            throw;
        }
    }

    private async Task<object> DoMergeAsync(MySqlConnection conn, HttpContext context, CurrentUser actor,
        UploadSessionRow session, CancellationToken ct)
    {
        var extension = ExtensionOf(session.FileName);
        var storedName = $"{Guid.NewGuid():D}.{extension}";
        var now = TruncateMilliseconds(DateTime.UtcNow);
        var root = FileStorage.Root(options.StorageRoot);
        var finalPath = FileStorage.FinalPath(root, now, storedName);
        var finalDirectory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("存储目录无效");
        Directory.CreateDirectory(finalDirectory);
        await FileStorage.ResolveExistingAsync(root, finalDirectory, requireFile: false, ct);
        var mergeTemp = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(FileStorage.SessionDirectory(root, session.Id), $"{storedName}.tmp"), false);
        TryDeleteFile(mergeTemp);
        var chunks = new List<string>(checked((int)session.TotalChunks));
        for (uint index = 0; index < session.TotalChunks; index++)
        {
            chunks.Add(await FileStorage.ResolveExistingFileAsync(root,
                FileStorage.ChunkPath(root, session.Id, index), ct));
        }
        (string Sha256, string Md5, ulong Bytes) hash;
        try { hash = await FileStorage.HashAndCopyAsync(chunks, mergeTemp, session.FileSize, ct); }
        catch { TryDeleteFile(mergeTemp); throw; }
        if (hash.Bytes != session.FileSize)
        {
            TryDeleteFile(mergeTemp);
            throw ApiException.BadRequest($"合并文件大小不符：期望 {session.FileSize}，实际 {hash.Bytes}");
        }
        if (!string.IsNullOrWhiteSpace(session.FileMd5)
            && !hash.Md5.Equals(session.FileMd5.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(mergeTemp);
            throw ApiException.BadRequest("文件 MD5 校验失败，请重新上传");
        }
        var relativePath = Path.GetRelativePath(root, finalPath).Replace(Path.DirectorySeparatorChar, '/');
        var keepFinal = false;
        try
        {
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
            var project = await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, session.ProjectId, ct);
            var locked = await LoadSessionAsync(conn, tx, session.Id, true, ct);
            if (locked.Status != "MERGING" || locked.UpdatedAt != session.UpdatedAt)
                throw ApiException.Conflict("上传会话状态已变化，请重新查询");

            // Final publication happens only after the database connection that owns
            // the named lease has passed the persisted fencing check. A disconnected
            // former owner therefore cannot publish after another worker takes over.
            await WritePendingFinalMarkerAsync(root, session.Id, relativePath, ct);
            File.Move(mergeTemp, finalPath, overwrite: false);
            var direction = current.IsInternal ? "C2S" : "S2C";
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,deleted_at,created_at)
                VALUES(@ProjectId,@UploaderId,@Direction,@OriginalName,@StoredName,@Ext,@SizeBytes,@MimeType,@Sha256,@StoragePath,'AVAILABLE',NULL,@CreatedAt)
                """, new
                {
                    ProjectId = session.ProjectId, UploaderId = current.Id, Direction = direction,
                    OriginalName = session.FileName, StoredName = storedName, Ext = extension,
                    SizeBytes = session.FileSize, MimeType = FileStorage.MimeType(session.FileName),
                    Sha256 = hash.Sha256, StoragePath = relativePath, CreatedAt = now
                }, tx, cancellationToken: ct));
            var fileId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                "SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
            await EnqueueFileNoticeAsync(conn, tx, project.Id, session.FileName, current, ct);
            await audit.WriteAsync(conn, tx, current.Id, "FILE_UPLOAD", "file", fileId,
                new { name = session.FileName, size = session.FileSize, projectId = session.ProjectId },
                ClientIp.Resolve(context, options), ct);
            var completed = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE upload_sessions SET status='COMPLETED',result_file_id=@FileId,updated_at=UTC_TIMESTAMP(6)
                WHERE id=@Id AND status='MERGING'
                """, new { FileId = fileId, Id = session.Id }, tx, cancellationToken: ct));
            if (completed != 1) throw ApiException.Conflict("上传会话已被其他请求变更");
            try { await tx.CommitAsync(ct); }
            catch
            {
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var confirmed = await ConfirmCompletedFileSafelyAsync(session.Id, reconcile.Token);
                if (confirmed is not null) { keepFinal = true; return FileJson(confirmed); }
                keepFinal = true;
                throw;
            }
            keepFinal = true;
            return FileJson(await LoadFileAsync(conn, fileId, ct));
        }
        finally
        {
            TryDeleteFile(mergeTemp);
            if (!keepFinal) TryDeleteFile(finalPath);
        }
    }

    private async Task<UploadSessionRow> ClaimMergeAsync(
        MySqlConnection conn, CurrentUser actor, UploadSessionRow session, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, session.ProjectId, ct);
        var locked = await LoadSessionAsync(conn, tx, session.Id, true, ct);
        if (locked.UploaderId != current.Id) throw ApiException.Forbidden();
        if (locked.Status is not ("UPLOADING" or "MERGING"))
            throw ApiException.Conflict("上传会话状态已变化，请重新查询");
        if (locked.Status == "UPLOADING" && locked.ExpiresAt < DateTime.UtcNow)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE upload_sessions
            SET status='MERGING',
                updated_at=CASE WHEN updated_at>=UTC_TIMESTAMP() THEN DATE_ADD(updated_at,INTERVAL 1 SECOND) ELSE UTC_TIMESTAMP() END
            WHERE id=@Id AND status IN ('UPLOADING','MERGING')
            """, new { Id = session.Id }, tx, cancellationToken: ct));
        if (changed != 1) throw ApiException.Conflict("上传会话状态已变化，请重新查询");
        await tx.CommitAsync(ct);
        return await LoadSessionAsync(conn, null, session.Id, false, ct);
    }

    private async Task<UploadSessionRow> ResetOrphanedMergeAsync(MySqlConnection conn, CurrentUser actor,
        UploadSessionRow session, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, session.ProjectId, ct);
        var locked = await LoadSessionAsync(conn, tx, session.Id, true, ct);
        if (locked.Status != "MERGING")
            throw ApiException.Conflict("会话已变更，请重试");
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE upload_sessions
            SET status='UPLOADING',
                updated_at=CASE WHEN updated_at>=UTC_TIMESTAMP() THEN DATE_ADD(updated_at,INTERVAL 1 SECOND) ELSE UTC_TIMESTAMP() END
            WHERE id=@Id AND status='MERGING'
            """, new { session.Id }, tx, cancellationToken: ct));
        if (changed != 1) throw ApiException.Conflict("会话已变更，请重试");
        await tx.CommitAsync(ct);
        return await LoadSessionAsync(conn, null, session.Id, false, ct);
    }

    private async Task<object> InitResponseAsync(UploadSessionRow session, bool resumed, CancellationToken ct) => new
    {
        sessionId = session.Id, chunkSize = session.ChunkSize, totalChunks = session.TotalChunks,
        uploadedChunks = await UploadedChunksAsync(session, ct), resumed
    };

    private async Task<List<uint>> UploadedChunksAsync(UploadSessionRow session, CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var result = new List<uint>();
        for (uint index = 0; index < session.TotalChunks; index++)
        {
            var path = FileStorage.ChunkPath(root, session.Id, index);
            if (!File.Exists(path)) continue;
            var expected = index == session.TotalChunks - 1
                ? session.FileSize - (ulong)session.ChunkSize * (session.TotalChunks - 1)
                : session.ChunkSize;
            try
            {
                var resolved = await FileStorage.ResolveExistingFileAsync(root, path, ct);
                if ((ulong)new FileInfo(resolved).Length == expected) result.Add(index);
            }
            catch (FileNotFoundException) { }
        }
        return result;
    }

    private async Task<string> ValidateFileAsync(MySqlConnection conn, InitUploadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.EnumerateRunes().Count() > 255
            || request.FileName.Any(character => char.IsControl(character) || character is '/' or '\\'))
            throw ApiException.BadRequest("文件名需为 1~255 个字符且不能包含路径或控制字符");
        if (request.FileSize == 0) throw ApiException.BadRequest("空文件不可上传");
        var maximum = await ConfigUInt64Async(conn, "upload.max_file_size", (ulong)options.UploadMaxFileSize, ct);
        if (request.FileSize > maximum)
            throw ApiException.BadRequest($"文件超过大小上限 {maximum / 1024 / 1024} MB");
        var extension = ExtensionOf(request.FileName);
        var configured = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='upload.allowed_exts'", cancellationToken: ct));
        var allowed = (configured ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0 && !allowed.Contains(extension, StringComparer.Ordinal))
            throw ApiException.BadRequest($"不支持的文件类型 .{extension}");
        return extension;
    }

    internal static string ExtensionOf(string name) =>
        name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : name.ToLowerInvariant();

    private static async Task<ulong> ConfigUInt64Async(MySqlConnection conn, string key, ulong fallback, CancellationToken ct)
    {
        var value = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key=@Key", new { Key = key }, cancellationToken: ct));
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static async Task<UploadSessionRow> LoadSessionAsync(MySqlConnection conn, MySqlTransaction? tx,
        string id, bool forUpdate, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw ApiException.NotFound();
        var session = await conn.QuerySingleOrDefaultAsync<UploadSessionRow>(new CommandDefinition("""
            SELECT id AS Id,project_id AS ProjectId,uploader_id AS UploaderId,file_name AS FileName,
                   file_size AS FileSize,file_md5 AS FileMd5,chunk_size AS ChunkSize,total_chunks AS TotalChunks,
                   temp_dir AS TempDir,status AS Status,result_file_id AS ResultFileId,
                   expires_at AS ExpiresAt,created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM upload_sessions WHERE id=@Id
            """ + (forUpdate ? " FOR UPDATE" : string.Empty), new { Id = id }, tx, cancellationToken: ct));
        if (session is null) throw ApiException.NotFound();
        return session;
    }

    private static async Task<FileRow> LoadFileAsync(MySqlConnection conn, ulong id, CancellationToken ct) =>
        await conn.QuerySingleAsync<FileRow>(new CommandDefinition(FileSelect + " WHERE f.id=@Id", new { Id = id }, cancellationToken: ct));

    private async Task<object> CompletedFileAsync(MySqlConnection conn, UploadSessionRow session, ulong uploaderId, CancellationToken ct)
    {
        FileRow? file = null;
        if (session.ResultFileId is ulong resultId)
            file = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(FileSelect + " WHERE f.id=@Id", new { Id = resultId }, cancellationToken: ct));
        file ??= await conn.QueryFirstOrDefaultAsync<FileRow>(new CommandDefinition(FileSelect + """
             WHERE f.project_id=@ProjectId AND f.original_name=@FileName AND f.uploader_id=@UploaderId ORDER BY f.id DESC LIMIT 1
             """, new { session.ProjectId, session.FileName, UploaderId = uploaderId }, cancellationToken: ct));
        return file is null ? throw ApiException.Conflict("会话已完成") : FileJson(file);
    }

    private async Task<bool> SessionExistsSafelyAsync(string id, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM upload_sessions WHERE id=@Id)", new { Id = id }, cancellationToken: ct));
        }
        catch { return true; }
    }

    private async Task ResetMergeLeaseSafelyAsync(string id, DateTime lease, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE upload_sessions SET status='UPLOADING',updated_at=UTC_TIMESTAMP(6)
                WHERE id=@Id AND status='MERGING' AND updated_at=@Lease
                """, new { Id = id, Lease = lease }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch { }
    }

    private async Task<FileRow?> ConfirmCompletedFileSafelyAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            return await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(FileSelect + """
                JOIN upload_sessions s ON s.result_file_id=f.id
                WHERE s.id=@Id AND s.status='COMPLETED'
                """, new { Id = sessionId }, cancellationToken: ct));
        }
        catch { return null; }
    }

    private async Task EnqueueFileNoticeAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId,
        string fileName, CurrentUser uploader, CancellationToken ct)
    {
        var enabled = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cfg_value FROM system_configs WHERE cfg_key='notify.enabled'", transaction: tx, cancellationToken: ct));
        if (enabled is not null && !(enabled.Equals("true", StringComparison.OrdinalIgnoreCase) || enabled == "1")) return;
        var projectName = await conn.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT name FROM projects WHERE id=@ProjectId", new { ProjectId = projectId }, tx, cancellationToken: ct));
        var recipients = await conn.QueryAsync<NoticeRecipient>(new CommandDefinition("""
            SELECT DISTINCT u.id AS Id,u.email AS Email,u.employee_no AS EmployeeNo,u.real_name AS RealName
            FROM users u
            LEFT JOIN project_members pm ON pm.user_id=u.id AND pm.project_id=@ProjectId
            LEFT JOIN projects p ON p.id=@ProjectId
            LEFT JOIN suppliers s ON s.id=p.supplier_id AND s.status='ACTIVE'
            WHERE u.status='ACTIVE' AND u.id<>@UploaderId AND (
                (@UploaderType='SUPPLIER' AND u.user_type='INTERNAL' AND (u.id=p.created_by OR pm.user_id IS NOT NULL)) OR
                (@UploaderType<>'SUPPLIER' AND u.user_type='SUPPLIER' AND u.supplier_id=p.supplier_id AND s.id IS NOT NULL)
            ) ORDER BY u.id
            """, new { ProjectId = projectId, UploaderId = uploader.Id, UploaderType = uploader.UserType }, tx, cancellationToken: ct));
        var subject = $"[协作平台] 项目「{projectName}」有新文件上传";
        var body = $"项目：{projectName}\n文件：{fileName}\n上传人：工号 {uploader.EmployeeNo}\n\n请登录平台查看并下载：{options.WebBaseUrl}\n\n（本邮件由系统自动发送，附件请登录平台获取）";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                await audit.WriteAsync(conn, tx, null, "EMAIL_SKIPPED_MISSING_EMAIL", "user", recipient.Id,
                    new { eventType = "FILE_UPLOADED", reason = "RECIPIENT_EMAIL_MISSING", employeeNo = recipient.EmployeeNo, realName = recipient.RealName },
                    null, ct);
                continue;
            }
            if (!seen.Add(recipient.Email)) continue;
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO email_outbox(event_type,project_id,dedupe_key,recipient_user_id,recipient_email,subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
                VALUES('FILE_UPLOADED',@ProjectId,NULL,@RecipientUserId,@RecipientEmail,@Subject,@Body,'PENDING',0,NULL,NULL,NULL,UTC_TIMESTAMP(6))
                """, new { ProjectId = projectId, RecipientUserId = recipient.Id, RecipientEmail = recipient.Email, Subject = subject, Body = body }, tx, cancellationToken: ct));
        }
    }

    private static async Task WriteUploadAbortAuditAsync(MySqlConnection conn, MySqlTransaction tx,
        CurrentUser actor, string sessionId, string ip, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_logs(user_id,employee_no,action,target_type,target_id,detail,ip,created_at)
            VALUES(@ActorId,@EmployeeNo,'UPLOAD_ABORT','upload_session',@SessionId,NULL,@Ip,UTC_TIMESTAMP(6))
            """, new { ActorId = actor.Id, actor.EmployeeNo, SessionId = sessionId, Ip = ip }, tx, cancellationToken: ct));
    }

    private static async Task WriteExactAsync(Stream source, string destination, ulong expected, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        ulong total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total = checked(total + (uint)read);
            if (total > expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际超过上限");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {total}");
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    internal static object FileJson(FileRow file) => new
    {
        id = file.Id, projectId = file.ProjectId, uploaderId = file.UploaderId, direction = file.Direction,
        originalName = file.OriginalName, ext = file.Ext, sizeBytes = file.SizeBytes,
        mimeType = file.MimeType, sha256 = file.Sha256, createdAt = file.CreatedAt
    };

    internal const string PendingFinalMarkerPrefix = ".pending-final-";
    internal const string PendingFinalStagingPrefix = ".writing-pending-final-";
    internal const string InvalidPendingFinalMarkerPrefix = ".invalid-pending-final-";

    internal static string MergeLockName(MySqlConnection conn, string sessionId) =>
        MySqlNamedLock.Name("upload-merge", conn.Database, sessionId);

    internal static async Task WritePendingFinalMarkerAsync(
        string root, string sessionId, string relativePath, CancellationToken ct)
    {
        var directory = FileStorage.SessionDirectory(root, sessionId);
        var markerId = Guid.NewGuid().ToString("D");
        var marker = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, PendingFinalMarkerPrefix + markerId), false);
        var staging = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, PendingFinalStagingPrefix + markerId), false);
        var payload = Encoding.UTF8.GetBytes(relativePath);
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(payload, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, marker, overwrite: false);
        }
        finally { TryDeleteFile(staging); }
    }

    internal static async Task CleanupPendingFinalsAsync(
        MySqlConnection conn,
        string configuredRoot,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        var root = FileStorage.Root(configuredRoot);
        var directory = FileStorage.SessionDirectory(root, sessionId);
        if (!Directory.Exists(directory)) return;
        directory = await FileStorage.ResolveExistingAsync(root, directory, requireFile: false, ct);
        foreach (var candidate in Directory.EnumerateFiles(
                     directory, PendingFinalMarkerPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var marker = await FileStorage.ResolveExistingFileAsync(root, candidate, ct);
            if (new FileInfo(marker).Length is <= 0 or > 2048)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记长度无效");
                continue;
            }

            string relativePath;
            try
            {
                relativePath = await File.ReadAllTextAsync(marker, new UTF8Encoding(false, true), ct);
            }
            catch (DecoderFallbackException)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记不是有效 UTF-8");
                continue;
            }

            // Check the durable database reference before classifying legacy marker text. A committed
            // file must always win, even if a historical marker does not match today's filename rules.
            var referenced = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM files WHERE storage_path=@StoragePath)",
                new { StoragePath = relativePath }, cancellationToken: ct));
            if (referenced)
            {
                File.Delete(marker);
                continue;
            }

            if (!IsPendingFinalPath(relativePath))
            {
                // A truncated marker cannot prove which file was published. Keep every possible
                // target untouched, move the marker out of the active pattern, and let the owned
                // session directory cleanup remove the quarantined evidence.
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记路径格式无效");
                continue;
            }

            string finalPath;
            try { finalPath = await FileStorage.ResolveForCleanupAsync(root, relativePath, ct); }
            catch (InvalidOperationException)
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记路径越出存储根目录");
                continue;
            }

            if (Directory.Exists(finalPath))
            {
                QuarantinePendingFinalMarker(root, directory, marker, sessionId, logger, "标记目标不是文件");
                continue;
            }
            if (File.Exists(finalPath))
            {
                finalPath = await FileStorage.ResolveExistingFileAsync(root, finalPath, ct);
                File.Delete(finalPath);
            }
            File.Delete(marker);
        }
    }

    private static bool IsPendingFinalPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath != relativePath.Trim()
            || relativePath.Contains('\\')
            || Path.IsPathFullyQualified(relativePath)) return false;
        var parts = relativePath.Split('/');
        if (parts.Length != 4 || parts[0] != "files"
            || parts[1].Length != 4 || !parts[1].All(char.IsAsciiDigit)
            || parts[2].Length != 2 || !int.TryParse(parts[2], out var month) || month is < 1 or > 12)
            return false;
        var extension = Path.GetExtension(parts[3]);
        return extension.Length is >= 2 and <= 17
               && extension.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit)
               && Guid.TryParseExact(Path.GetFileNameWithoutExtension(parts[3]), "D", out _);
    }

    private static void QuarantinePendingFinalMarker(
        string root,
        string directory,
        string marker,
        string sessionId,
        ILogger logger,
        string reason)
    {
        var quarantined = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(directory, InvalidPendingFinalMarkerPrefix + Guid.NewGuid().ToString("D")), false);
        try
        {
            File.Move(marker, quarantined, overwrite: false);
            logger.LogWarning("已隔离无效待提交文件标记 {SessionId} {MarkerName}: {Reason}",
                sessionId, Path.GetFileName(marker), reason);
        }
        catch (Exception error)
        {
            // Do not interpret or delete a target from invalid marker text. The caller can still
            // safely delete the owned session directory, including the marker itself.
            logger.LogWarning(error, "无法隔离无效待提交文件标记 {SessionId} {MarkerName}: {Reason}",
                sessionId, Path.GetFileName(marker), reason);
        }
    }

    private async Task TryCleanupSessionArtifactsAsync(
        MySqlConnection conn, string sessionId, CancellationToken ct)
    {
        try
        {
            await CleanupPendingFinalsAsync(conn, options.StorageRoot, sessionId, logger, ct);
            await FileStorage.DeleteDirectoryTreeAsync(options.StorageRoot,
                FileStorage.SessionDirectory(FileStorage.Root(options.StorageRoot), sessionId), ct);
        }
        catch
        {
            // The maintenance worker retries durable markers and session directories.
        }
    }

    private static DateTime TruncateMilliseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    internal const string FileSelect = """
        SELECT f.id AS Id,f.project_id AS ProjectId,f.uploader_id AS UploaderId,f.direction AS Direction,
               f.original_name AS OriginalName,f.stored_name AS StoredName,f.ext AS Ext,f.size_bytes AS SizeBytes,
               f.mime_type AS MimeType,f.sha256 AS Sha256,f.storage_path AS StoragePath,f.status AS Status,
               f.deleted_at AS DeletedAt,f.created_at AS CreatedAt FROM files f
        """;

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }
    private static async Task TryDeleteDirectoryAsync(string root, string path, CancellationToken ct)
    {
        try { await FileStorage.DeleteDirectoryTreeAsync(root, path, ct); }
        catch { }
    }

    private sealed class NoticeRecipient
    {
        public ulong Id { get; set; }
        public string Email { get; set; } = "";
        public string EmployeeNo { get; set; } = "";
        public string RealName { get; set; } = "";
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Md5Pattern();
}
