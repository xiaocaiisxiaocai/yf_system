using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

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
                await using var ef = EfDb.Use(conn);
                var dbNow = await DbNowAsync(ef, ct);
                var resumable = new[] { "UPLOADING", "MERGING" };
                var entity = await ef.UploadSessions
                    .Where(session => session.ProjectId == request.ProjectId && session.UploaderId == actor.Id
                        && session.FileName == request.FileName && session.FileSize == request.FileSize
                        && session.FileMd5 == fileMd5 && session.ExpiresAt > dbNow
                        && Enumerable.Contains(resumable, session.Status))
                    .OrderByDescending(session => session.CreatedAt).FirstOrDefaultAsync(ct);
                existing = entity is null ? null : ToRow(entity, dbNow);
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
                return InitResponse(existing, resumed: true, ct);
            }

            var sessionId = Guid.NewGuid().ToString("D");
            var root = FileStorage.Root(options.StorageRoot);
            var tempDir = FileStorage.SessionDirectory(root, sessionId);
            tempDir = FileStorage.CreateDirectoryWithin(root, tempDir, ct);
            SessionCommitRecoveryDecision? commitRecovery = null;
            try
            {
                await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
                await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, request.ProjectId, ct);
                await using var ef = EfDb.Use(conn, tx);
                var dbNow = await DbNowAsync(ef, ct);
                ef.UploadSessions.Add(new UploadSession
                {
                    Id = sessionId,
                    ProjectId = request.ProjectId,
                    UploaderId = current.Id,
                    FileName = request.FileName,
                    FileSize = request.FileSize,
                    FileMd5 = fileMd5,
                    ChunkSize = chunkSize,
                    TotalChunks = totalChunks,
                    TempDir = tempDir,
                    Status = "UPLOADING",
                    ExpiresAt = dbNow.AddHours(24),
                    CreatedAt = dbNow,
                    UpdatedAt = dbNow
                });
                await ef.SaveChangesAsync(ct);
                try { await tx.CommitAsync(ct); }
                catch
                {
                    using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    // Only a positive read can turn a lost COMMIT acknowledgement into success.
                    // A failed confirmation query is still an unknown outcome and must be reported
                    // to the caller, while preserving the directory for a later retry or cleanup.
                    commitRecovery = SessionCommitRecovery(
                        await SessionExistsSafelyAsync(sessionId, reconcile.Token));
                    if (!commitRecovery.Value.Acknowledge)
                        throw;
                }
            }
            catch
            {
                var recovery = commitRecovery
                    ?? SessionCommitRecovery(await SessionExistsSafelyAsync(sessionId, ct));
                if (recovery.DeleteDirectory)
                    TryDeleteDirectory(options.StorageRoot, tempDir, CancellationToken.None);
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
        if (session.Status == "UPLOADING" && session.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        var chunks = UploadedChunks(session, ct);
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
        if (initial.IsExpired) throw ApiException.Conflict("上传会话已过期，请重新发起");
        if ((uint)index >= initial.TotalChunks) throw ApiException.BadRequest("分片序号越界");
        var expected = (uint)index == initial.TotalChunks - 1
            ? initial.FileSize - (ulong)initial.ChunkSize * (initial.TotalChunks - 1)
            : initial.ChunkSize;
        if (context.Request.ContentLength is long contentLength && (ulong)contentLength != expected)
            throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {contentLength}");
        FileStorage.EnsureFreeSpace(options.StorageRoot, expected);

        var root = FileStorage.Root(options.StorageRoot);
        var directory = FileStorage.SessionDirectory(root, sessionId);
        directory = FileStorage.CreateDirectoryWithin(root, directory, ct);
        var path = FileStorage.EnsureLexicallyWithin(root, Path.Combine(directory, $"{index}.part"), false);
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
            if (session.IsExpired) throw ApiException.Conflict("上传会话已过期，请重新发起");
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
                await using var ef = EfDb.Use(conn, tx);
                var dbNow = await DbNowAsync(ef, ct);
                await ef.UploadSessions.Where(item => item.Id == sessionId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "ABORTED")
                        .SetProperty(item => item.UpdatedAt, dbNow), ct);
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
        if (session.Status == "UPLOADING" && session.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");

        await using var mergeLease = await MySqlNamedLock.TryAcquireAsync(
            conn, MergeLockName(conn, sessionId), 0, ct)
            ?? throw ApiException.Conflict("正在合并中，请稍候");
        session = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (session.Status == "COMPLETED") return await CompletedFileAsync(conn, session, actor.Id, ct);
        if (session.Status is not ("UPLOADING" or "MERGING")) throw ApiException.Conflict("会话已失效");
        if (session.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        await ProjectAccessService.RequireFileUploadAsync(conn, null, actor, session.ProjectId, ct);
        var uploaded = UploadedChunks(session, ct);
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
        await using var clockContext = EfDb.Use(conn);
        var now = await DbNowAsync(clockContext, ct);
        var root = FileStorage.Root(options.StorageRoot);
        var finalPath = FileStorage.FinalPath(root, now, storedName);
        var finalDirectory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("存储目录无效");
        FileStorage.CreateDirectoryWithin(root, finalDirectory, ct);
        var mergeTemp = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(FileStorage.SessionDirectory(root, session.Id), $"{storedName}.tmp"), false);
        TryDeleteFile(mergeTemp);
        var chunks = new List<string>(checked((int)session.TotalChunks));
        for (uint index = 0; index < session.TotalChunks; index++)
        {
            chunks.Add(FileStorage.ResolveExistingFile(root,
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
            await using var ef = EfDb.Use(conn, tx);
            var file = new FileRecord
            {
                ProjectId = session.ProjectId,
                UploaderId = current.Id,
                Direction = direction,
                OriginalName = session.FileName,
                StoredName = storedName,
                Ext = extension,
                SizeBytes = session.FileSize,
                MimeType = FileStorage.MimeType(session.FileName),
                Sha256 = hash.Sha256,
                StoragePath = relativePath,
                Status = "AVAILABLE",
                CreatedAt = now
            };
            ef.Files.Add(file);
            await ef.SaveChangesAsync(ct);
            var fileId = file.Id;
            await EnqueueFileNoticeAsync(conn, tx, project.Id, fileId, session.FileName, current, ct);
            await audit.WriteAsync(conn, tx, current.Id, "FILE_UPLOAD", "file", fileId,
                new { name = session.FileName, size = session.FileSize, projectId = session.ProjectId },
                ClientIp.Resolve(context, options), ct);
            var completedAt = await DbNowAsync(ef, ct);
            var completed = await ef.UploadSessions
                .Where(item => item.Id == session.Id && item.Status == "MERGING")
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "COMPLETED")
                    .SetProperty(item => item.ResultFileId, fileId)
                    .SetProperty(item => item.UpdatedAt, completedAt), ct);
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
        if (locked.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        await using var ef = EfDb.Use(conn, tx);
        var dbNow = await DbNowAsync(ef, ct);
        var lease = locked.UpdatedAt >= dbNow ? locked.UpdatedAt.AddSeconds(1) : dbNow;
        var mergeable = new[] { "UPLOADING", "MERGING" };
        var changed = await ef.UploadSessions
            .Where(item => item.Id == session.Id && Enumerable.Contains(mergeable, item.Status) && item.ExpiresAt > dbNow)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "MERGING")
                .SetProperty(item => item.UpdatedAt, lease), ct);
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
        if (locked.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        await using var ef = EfDb.Use(conn, tx);
        var dbNow = await DbNowAsync(ef, ct);
        var lease = locked.UpdatedAt >= dbNow ? locked.UpdatedAt.AddSeconds(1) : dbNow;
        var changed = await ef.UploadSessions
            .Where(item => item.Id == session.Id && item.Status == "MERGING" && item.ExpiresAt > dbNow)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "UPLOADING")
                .SetProperty(item => item.UpdatedAt, lease), ct);
        if (changed != 1) throw ApiException.Conflict("会话已变更，请重试");
        await tx.CommitAsync(ct);
        return await LoadSessionAsync(conn, null, session.Id, false, ct);
    }

    private object InitResponse(UploadSessionRow session, bool resumed, CancellationToken ct) => new
    {
        sessionId = session.Id, chunkSize = session.ChunkSize, totalChunks = session.TotalChunks,
        uploadedChunks = UploadedChunks(session, ct), resumed
    };

    private List<uint> UploadedChunks(UploadSessionRow session, CancellationToken ct)
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
                var resolved = FileStorage.ResolveExistingFile(root, path, ct);
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
        await using var context = EfDb.Use(conn);
        var configured = await context.SystemConfigs.Where(config => config.CfgKey == "upload.allowed_exts")
            .Select(config => config.CfgValue).SingleOrDefaultAsync(ct);
        var allowed = (configured ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0 && !allowed.Contains(extension, StringComparer.Ordinal))
            throw ApiException.BadRequest($"不支持的文件类型 .{extension}");
        return extension;
    }

    internal static string ExtensionOf(string name) =>
        name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : name.ToLowerInvariant();

    private static async Task<ulong> ConfigUInt64Async(MySqlConnection conn, string key, ulong fallback, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn);
        var value = await context.SystemConfigs.Where(config => config.CfgKey == key)
            .Select(config => config.CfgValue).SingleOrDefaultAsync(ct);
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static async Task<UploadSessionRow> LoadSessionAsync(MySqlConnection conn, MySqlTransaction? tx,
        string id, bool forUpdate, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw ApiException.NotFound();
        await using var context = EfDb.Use(conn, tx);
        UploadSession? session;
        if (forUpdate)
            session = await context.UploadSessions.FromSqlInterpolated($"SELECT * FROM upload_sessions WHERE id={id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        else
            session = await context.UploadSessions.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (session is null) throw ApiException.NotFound();
        var dbNow = await DbNowAsync(context, ct);
        return ToRow(session, dbNow);
    }

    private static async Task<FileRow> LoadFileAsync(MySqlConnection conn, ulong id, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn);
        var file = await context.Files.SingleAsync(item => item.Id == id, ct);
        return ToRow(file);
    }

    private async Task<object> CompletedFileAsync(MySqlConnection conn, UploadSessionRow session, ulong uploaderId, CancellationToken ct)
    {
        FileRow? file = null;
        await using var context = EfDb.Use(conn);
        if (session.ResultFileId is ulong resultId)
        {
            var byResult = await context.Files.SingleOrDefaultAsync(item => item.Id == resultId, ct);
            if (byResult is not null) file = ToRow(byResult);
        }
        if (file is null)
        {
            var fallback = await context.Files.Where(item => item.ProjectId == session.ProjectId
                    && item.OriginalName == session.FileName && item.UploaderId == uploaderId)
                .OrderByDescending(item => item.Id).FirstOrDefaultAsync(ct);
            if (fallback is not null) file = ToRow(fallback);
        }
        return file is null ? throw ApiException.Conflict("会话已完成") : FileJson(file);
    }

    private async Task<bool?> SessionExistsSafelyAsync(string id, CancellationToken ct) =>
        await ProbeSessionExistenceAsync(async cancellationToken =>
        {
            await using var conn = await db.OpenAsync(cancellationToken);
            await using var context = EfDb.Use(conn);
            return await context.UploadSessions.AnyAsync(session => session.Id == id, cancellationToken);
        }, ct);

    internal static async Task<bool?> ProbeSessionExistenceAsync(
        Func<CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        try { return await probe(ct); }
        catch { return null; }
    }

    internal static SessionCommitRecoveryDecision SessionCommitRecovery(bool? sessionExists) =>
        sessionExists switch
        {
            true => new(Acknowledge: true, DeleteDirectory: false),
            false => new(Acknowledge: false, DeleteDirectory: true),
            null => new(Acknowledge: false, DeleteDirectory: false)
        };

    private async Task ResetMergeLeaseSafelyAsync(string id, DateTime lease, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var context = EfDb.Use(conn, tx);
            var dbNow = await DbNowAsync(context, ct);
            await context.UploadSessions.Where(session => session.Id == id && session.Status == "MERGING" && session.UpdatedAt == lease)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.Status, "UPLOADING")
                    .SetProperty(session => session.UpdatedAt, dbNow), ct);
            await tx.CommitAsync(ct);
        }
        catch { }
    }

    private async Task<FileRow?> ConfirmCompletedFileSafelyAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var context = EfDb.Use(conn);
            var file = await context.UploadSessions.Where(session => session.Id == sessionId && session.Status == "COMPLETED")
                .Join(context.Files, session => session.ResultFileId, file => (ulong?)file.Id, (_, file) => file)
                .SingleOrDefaultAsync(ct);
            return file is null ? null : ToRow(file);
        }
        catch { return null; }
    }

    private async Task EnqueueFileNoticeAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId,
        ulong fileId, string fileName, CurrentUser uploader, CancellationToken ct)
    {
        var policy = await EmailNotificationPolicy.LoadAsync(conn, tx, ct);
        if (!policy.Allows("FILE_UPLOADED", null)) return;
        await using var context = EfDb.Use(conn, tx);
        var project = await context.Projects.SingleAsync(item => item.Id == projectId, ct);
        var supplierActive = await context.Suppliers.AnyAsync(item => item.Id == project.SupplierId && item.Status == "ACTIVE", ct);
        var recipientsQuery = context.Users.Where(user => user.Status == "ACTIVE" && user.Id != uploader.Id
            && context.UserRoles.Where(userRole => userRole.UserId == user.Id)
                .Join(context.Roles.Where(role => role.Status == "ACTIVE"), userRole => userRole.RoleId, role => role.Id, (userRole, _) => userRole)
                .Join(context.RolePermissions, userRole => userRole.RoleId, rolePermission => rolePermission.RoleId, (_, rolePermission) => rolePermission)
                .Join(context.Permissions.Where(permission => permission.Code == "project:list"),
                    rolePermission => rolePermission.PermissionId, permission => permission.Id, (_, _) => true).Any());
        recipientsQuery = uploader.UserType == "SUPPLIER"
            ? recipientsQuery.Where(user => user.UserType == "INTERNAL" && user.Id == project.ResponsibleUserId)
            : recipientsQuery.Where(user => user.UserType == "SUPPLIER" && user.SupplierId == project.SupplierId && supplierActive);
        var recipients = await recipientsQuery.OrderBy(user => user.Id)
            .Select(user => new NoticeRecipient
            {
                Id = user.Id, Email = user.Email, EmployeeNo = user.EmployeeNo, RealName = user.RealName, UserType = user.UserType
            }).ToArrayAsync(ct);
        var projectName = project.Name;
        var subject = $"[协作平台] 项目「{projectName}」有新文件上传";
        var targetUrl = $"{options.WebBaseUrl.TrimEnd('/')}/projects/{projectId}?tab=files&target={fileId}";
        var body = $"项目：{projectName}\n文件：{fileName}\n上传人：工号 {uploader.EmployeeNo}\n\n请登录平台查看并下载：{targetUrl}\n\n（本邮件由系统自动发送，附件请登录平台获取）";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdAt = await DbNowAsync(context, ct);
        foreach (var recipient in recipients)
        {
            if (!policy.Allows("FILE_UPLOADED", recipient.UserType)) continue;
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                await audit.WriteAsync(conn, tx, null, "EMAIL_SKIPPED_MISSING_EMAIL", "user", recipient.Id,
                    new { eventType = "FILE_UPLOADED", reason = "RECIPIENT_EMAIL_MISSING", employeeNo = recipient.EmployeeNo, realName = recipient.RealName },
                    null, ct);
                continue;
            }
            if (!seen.Add(recipient.Email)) continue;
            context.EmailOutbox.Add(new EmailOutbox
            {
                EventType = "FILE_UPLOADED",
                ProjectId = projectId,
                RecipientUserId = recipient.Id,
                RecipientEmail = recipient.Email,
                Subject = subject,
                Body = body,
                Status = "PENDING",
                RetryCount = 0,
                CreatedAt = createdAt
            });
        }
        await context.SaveChangesAsync(ct);
    }

    private static async Task WriteUploadAbortAuditAsync(MySqlConnection conn, MySqlTransaction tx,
        CurrentUser actor, string sessionId, string ip, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn, tx);
        context.AuditLogs.Add(new AuditLog
        {
            UserId = actor.Id,
            EmployeeNo = actor.EmployeeNo,
            Action = "UPLOAD_ABORT",
            TargetType = "upload_session",
            TargetId = sessionId,
            Detail = null,
            Ip = ip,
            CreatedAt = await DbNowAsync(context, ct)
        });
        await context.SaveChangesAsync(ct);
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
        directory = FileStorage.CreateDirectoryWithin(root, directory, ct);
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
        directory = FileStorage.ResolveExisting(root, directory, requireFile: false, ct);
        foreach (var candidate in Directory.EnumerateFiles(
                     directory, PendingFinalMarkerPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var marker = FileStorage.ResolveExistingFile(root, candidate, ct);
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
            await using var context = EfDb.Use(conn);
            var referenced = await context.Files.AnyAsync(file => file.StoragePath == relativePath, ct);
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
            try { finalPath = FileStorage.ResolveForCleanup(root, relativePath, ct); }
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
                finalPath = FileStorage.ResolveExistingFile(root, finalPath, ct);
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
            FileStorage.DeleteDirectoryTree(options.StorageRoot,
                FileStorage.SessionDirectory(FileStorage.Root(options.StorageRoot), sessionId), ct);
        }
        catch
        {
            // The maintenance worker retries durable markers and session directories.
        }
    }

    internal static Task<DateTime> DbNowAsync(YfDbContext context, CancellationToken ct) =>
        context.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);

    internal static UploadSessionRow ToRow(UploadSession session, DateTime dbNow) => new()
    {
        Id = session.Id,
        ProjectId = session.ProjectId,
        UploaderId = session.UploaderId,
        FileName = session.FileName,
        FileSize = session.FileSize,
        FileMd5 = session.FileMd5,
        ChunkSize = session.ChunkSize,
        TotalChunks = session.TotalChunks,
        TempDir = session.TempDir,
        Status = session.Status,
        ResultFileId = session.ResultFileId,
        ExpiresAt = session.ExpiresAt,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        IsExpired = session.ExpiresAt <= dbNow
    };

    internal static FileRow ToRow(FileRecord file) => new()
    {
        Id = file.Id,
        ProjectId = file.ProjectId,
        UploaderId = file.UploaderId,
        Direction = file.Direction,
        OriginalName = file.OriginalName,
        StoredName = file.StoredName,
        Ext = file.Ext,
        SizeBytes = file.SizeBytes,
        MimeType = file.MimeType,
        Sha256 = file.Sha256,
        StoragePath = file.StoragePath,
        Status = file.Status,
        DeletedAt = file.DeletedAt,
        CreatedAt = file.CreatedAt
    };

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string root, string path, CancellationToken ct)
    {
        try { FileStorage.DeleteDirectoryTree(root, path, ct); }
        catch { }
    }

    private sealed class NoticeRecipient
    {
        public ulong Id { get; set; }
        public string Email { get; set; } = "";
        public string EmployeeNo { get; set; } = "";
        public string RealName { get; set; } = "";
        public string UserType { get; set; } = "";
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Md5Pattern();
}
