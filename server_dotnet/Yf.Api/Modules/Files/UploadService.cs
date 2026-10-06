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

    public async Task<UploadInitResponse> InitAsync(HttpContext context, InitUploadRequest request, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await ProjectAccessService.RequireFileUploadAsync(conn, null, actor, request.ProjectId, ct);
        await ValidateFileAsync(conn, request, ct);
        if (request.FileLastModified < 0)
            throw ApiException.BadRequest("文件修改时间无效");
        if (string.IsNullOrWhiteSpace(request.FileFingerprint)
            || !Sha256Pattern().IsMatch(request.FileFingerprint))
            throw ApiException.BadRequest("文件快速指纹格式无效");
        var fileFingerprint = request.FileFingerprint.ToLowerInvariant();

        var chunkSize = (uint)Math.Clamp(await ConfigUInt64Async(conn, "upload.chunk_size", (ulong)options.UploadChunkSize, ct),
            MinimumChunkSize, MaximumChunkSize);
        var totalChunks64 = request.FileSize / chunkSize + (request.FileSize % chunkSize == 0 ? 0UL : 1UL);
        if (totalChunks64 > uint.MaxValue) throw ApiException.BadRequest("文件分片数量超出限制");
        var totalChunks = (uint)totalChunks64;
        FileStorage.EnsureFreeSpace(options.StorageRoot, request.FileSize);

        var identityLockName = MySqlNamedLock.Name("upload-init", conn.Database,
            request.ProjectId, actor.Id, request.FileName, request.FileSize,
            request.FileLastModified, fileFingerprint);
        var identityLease = await MySqlNamedLock.TryAcquireAsync(conn, identityLockName, 10, ct)
            ?? throw ApiException.Conflict("相同文件正在初始化，请稍后重试");
        await using (identityLease)
        {
            UploadSessionRow? existing = null;
            await using (var ef = EfDb.Use(conn))
            {
                var dbNow = await DbNowAsync(ef, ct);
                var resumable = new[] { "UPLOADING", "MERGING" };
                var entity = await ef.UploadSessions
                    .Where(session => session.ProjectId == request.ProjectId && session.UploaderId == actor.Id
                        && session.FileName == request.FileName && session.FileSize == request.FileSize
                        && session.FileLastModified == request.FileLastModified
                        && session.FileFingerprint == fileFingerprint && session.ExpiresAt > dbNow
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
                return await InitResponseAsync(existing, resumed: true, ct);
            }

            // Material rules apply to new sessions only; resuming an existing session is never re-rejected.
            if (actor.IsInternal && UploadMaterialRules.IsSpreadsheet(request.FileName)
                && !UploadMaterialRules.IsMotionFlowWorkbookName(request.FileName))
                throw ApiException.BadRequest(UploadMaterialRules.MotionFlowNamingMessage);

            // Serializes this account's session creation so concurrent inits cannot overshoot the quota.
            await using var quotaLease = await MySqlNamedLock.TryAcquireAsync(conn,
                MySqlNamedLock.Name("upload-quota", conn.Database, actor.Id), 10, ct)
                ?? throw ApiException.Conflict("上传会话正在创建，请稍后重试");
            var sessionId = Guid.NewGuid().ToString("D");
            var root = FileStorage.Root(options.StorageRoot);
            var tempDir = FileStorage.SessionDirectory(root, sessionId);
            tempDir = FileStorage.CreateDirectoryWithin(root, tempDir, ct);
            SessionCommitRecoveryDecision? commitRecovery = null;
            var commitAttempted = false;
            try
            {
                await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
                var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
                await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, request.ProjectId, ct);
                await using var ef = EfDb.Use(conn, tx);
                var dbNow = await DbNowAsync(ef, ct);
                if (current.IsInternal && !UploadMaterialRules.IsStepFile(request.FileName)
                    && !await HasCompanyStepMaterialAsync(ef, request.ProjectId, dbNow, includeActiveSessions: true, ct))
                    throw ApiException.BadRequest(UploadMaterialRules.StepRequiredMessage);
                await EnsureUploadQuotaAsync(ef, current.Id, request.FileSize, dbNow, ct);
                ef.UploadSessions.Add(new UploadSession
                {
                    Id = sessionId,
                    ProjectId = request.ProjectId,
                    UploaderId = current.Id,
                    FileName = request.FileName,
                    FileSize = request.FileSize,
                    FileLastModified = request.FileLastModified,
                    FileFingerprint = fileFingerprint,
                    FileMd5 = null,
                    ChunkSize = chunkSize,
                    TotalChunks = totalChunks,
                    TempDir = tempDir,
                    Status = "UPLOADING",
                    ExpiresAt = dbNow.AddHours(24),
                    CreatedAt = dbNow,
                    UpdatedAt = dbNow
                });
                await ef.SaveChangesAsync(ct);
                commitAttempted = true;
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
                // Before COMMIT starts, transaction disposal makes cleanup safe. Once COMMIT starts,
                // only a positive probe proves success; a negative read can race a server-side commit.
                var recovery = commitRecovery
                    ?? (commitAttempted
                        ? new SessionCommitRecoveryDecision(Acknowledge: false, DeleteDirectory: false)
                        : new SessionCommitRecoveryDecision(Acknowledge: false, DeleteDirectory: true));
                if (recovery.DeleteDirectory)
                    TryDeleteDirectory(options.StorageRoot, tempDir, sessionId, CancellationToken.None);
                throw;
            }
            return new UploadInitResponse(sessionId, chunkSize, totalChunks,
                Array.Empty<UploadedChunkDigestResponse>());
        }
    }

    public async Task<UploadSessionResponse> GetAsync(HttpContext context, string sessionId, CancellationToken ct)
    {
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        var session = await LoadSessionAsync(conn, null, sessionId, false, ct);
        if (session.UploaderId != actor.Id) throw ApiException.Forbidden();
        await ProjectAccessService.RequireViewAsync(conn, null, actor, session.ProjectId, ct);
        await AccessService.RequirePermissionAsync(conn, null, actor, "file:upload", ct);
        if (session.Status == "UPLOADING" && session.IsExpired)
            throw ApiException.Conflict("上传会话已过期，请重新发起");
        var chunks = await UploadedChunksAsync(session, repairMetadata: false, ct);
        return new UploadSessionResponse(session.Id, session.Status, session.ChunkSize, session.TotalChunks, chunks,
            session.FileName, session.FileSize, session.ResultFileId);
    }

    public async Task PutChunkAsync(HttpContext context, string sessionId, int index, Stream body, CancellationToken ct)
    {
        if (index < 0) throw ApiException.BadRequest("分片序号越界");
        var declaredDigest = context.Request.Headers["X-Chunk-SHA256"].ToString().Trim().ToLowerInvariant();
        if (!Sha256Pattern().IsMatch(declaredDigest))
            throw ApiException.BadRequest("分片 SHA-256 摘要格式无效");
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
            var actualDigest = await WriteExactAndHashAsync(body, temporary, expected, ct);
            if (!actualDigest.Equals(declaredDigest, StringComparison.Ordinal))
                throw ApiException.BadRequest("分片 SHA-256 校验失败");
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
            if (await ValidChunkDigestAsync(root, path, expected, repairMetadata: false, ct) is { } existingDigest
                && existingDigest.Equals(declaredDigest, StringComparison.Ordinal))
            {
                await tx.CommitAsync(ct);
                return;
            }
            TryDeleteFile(ChunkDigestPath(path), "stale-chunk-digest");
            File.Move(temporary, path, overwrite: true);
            await WriteChunkDigestAsync(root, path, actualDigest, ct);
            await tx.CommitAsync(ct);
        }
        finally { TryDeleteFile(temporary, "chunk-upload-staging"); }
    }

    public async Task SubmitMd5Async(
        HttpContext context, string sessionId, SubmitUploadMd5Request request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileMd5) || !Md5Pattern().IsMatch(request.FileMd5))
            throw ApiException.BadRequest("文件 MD5 摘要格式无效");
        var actor = AccessService.GetCurrent(context);
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var session = await LoadSessionAsync(conn, tx, sessionId, true, ct);
        if (session.UploaderId != current.Id) throw ApiException.Forbidden();
        await ProjectAccessService.RequireFileUploadAsync(conn, tx, current, session.ProjectId, ct);
        if (session.Status != "UPLOADING") throw ApiException.Conflict("会话不可提交完整性摘要");
        if (session.IsExpired) throw ApiException.Conflict("上传会话已过期，请重新发起");
        await using var ef = EfDb.Use(conn, tx);
        var dbNow = await DbNowAsync(ef, ct);
        await ef.UploadSessions.Where(item => item.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FileMd5, request.FileMd5.ToLowerInvariant())
                .SetProperty(item => item.UpdatedAt, dbNow), ct);
        await tx.CommitAsync(ct);
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
        await TryCleanupSessionArtifactsAsync(sessionId, CancellationToken.None);
    }

    private async Task<UploadInitResponse> InitResponseAsync(
        UploadSessionRow session, bool resumed, CancellationToken ct) =>
        new(session.Id, session.ChunkSize, session.TotalChunks,
            await UploadedChunksAsync(session, repairMetadata: false, ct), resumed);

    private async Task<List<UploadedChunkDigestResponse>> UploadedChunksAsync(
        UploadSessionRow session, bool repairMetadata, CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var result = new List<UploadedChunkDigestResponse>();
        for (uint index = 0; index < session.TotalChunks; index++)
        {
            var path = FileStorage.ChunkPath(root, session.Id, index);
            if (!File.Exists(path)) continue;
            var expected = index == session.TotalChunks - 1
                ? session.FileSize - (ulong)session.ChunkSize * (session.TotalChunks - 1)
                : session.ChunkSize;
            var digest = await ValidChunkDigestAsync(root, path, expected, repairMetadata, ct);
            if (digest is not null) result.Add(new UploadedChunkDigestResponse(index, digest));
        }
        return result;
    }

    private async Task<string> ValidateFileAsync(MySqlConnection conn, InitUploadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.EnumerateRunes().Count() > 255
            || HasUnsafeFileNameCharacter(request.FileName))
            throw ApiException.BadRequest("文件名需为 1~255 个字符且不能包含路径、控制字符或格式字符");
        if (request.FileSize == 0) throw ApiException.BadRequest("空文件不可上传");
        var maximum = await ConfigUInt64Async(conn, "upload.max_file_size", (ulong)options.UploadMaxFileSize, ct);
        if (request.FileSize > maximum)
            throw ApiException.BadRequest($"文件超过大小上限 {maximum / 1024 / 1024} MB");
        var extension = ExtensionOf(request.FileName);
        await using var context = EfDb.Use(conn);
        var configured = await context.SystemConfigs.Where(config => config.CfgKey == "upload.allowed_exts")
            .Select(config => config.CfgValue).SingleOrDefaultAsync(ct);
        var allowed = (configured ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extension.Length == 0) throw ApiException.BadRequest("文件缺少扩展名，无法识别文件类型");
        if (allowed.Length > 0 && !allowed.Contains(extension, StringComparer.Ordinal))
            throw ApiException.BadRequest($"不支持的文件类型 .{extension}");
        return extension;
    }

    /// <summary>
    /// Per-account upload quota: unexpired UPLOADING/MERGING sessions and their declared, not yet merged
    /// bytes. Only new sessions are checked; resuming or merging an existing session is never rejected.
    /// </summary>
    private async Task EnsureUploadQuotaAsync(
        YfDbContext ef, ulong uploaderId, ulong requestedBytes, DateTime dbNow, CancellationToken ct)
    {
        var active = new[] { "UPLOADING", "MERGING" };
        var sizes = await ef.UploadSessions
            .Where(session => session.UploaderId == uploaderId && session.ExpiresAt > dbNow
                && Enumerable.Contains(active, session.Status))
            .Select(session => session.FileSize).ToListAsync(ct);
        if (sizes.Count >= options.UploadMaxActiveSessionsPerUser)
            throw ApiException.Conflict(
                $"当前账号未完成的上传任务已达上限（{options.UploadMaxActiveSessionsPerUser} 个），请等待现有上传完成或取消后再上传");
        var pending = sizes.Aggregate(0UL, (total, size) => total + size);
        if (pending + requestedBytes > (ulong)options.UploadMaxPendingBytesPerUser)
            throw ApiException.Conflict(
                $"当前账号未完成上传的文件总大小超过上限（{FormatQuotaBytes(options.UploadMaxPendingBytesPerUser)}），请等待现有上传完成或取消后再上传");
    }

    private static string FormatQuotaBytes(long bytes) => bytes % (1024L * 1024 * 1024) == 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024L * 1024 * 1024)} GiB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB");

    // 公司发给供应商的非 STEP 文件，需要本子项目已有可用的 C2S STEP 文件。新建会话时（includeActiveSessions）
    // 内部账号正在上传/合并的 STEP 会话也计入，支持同一批次并发上传（STEP 会话先创建即可）；合并写入时只认
    // 已合并、未删除的 STEP 文件，因为进行中的 STEP 会话随后可能被取消或过期。恢复已有会话不重复校验。
    internal static async Task<bool> HasCompanyStepMaterialAsync(
        YfDbContext ef, ulong projectId, DateTime dbNow, bool includeActiveSessions, CancellationToken ct)
    {
        var extensions = UploadMaterialRules.StepFileExtensions.ToArray();
        if (await ef.Files.AnyAsync(file => file.ProjectId == projectId && file.Direction == "C2S"
                && file.Status == FileStatuses.Available && Enumerable.Contains(extensions, file.Ext), ct))
            return true;
        if (!includeActiveSessions) return false;
        var active = new[] { "UPLOADING", "MERGING" };
        var sessionNames = await (
            from session in ef.UploadSessions
            join user in ef.Users on session.UploaderId equals user.Id
            where session.ProjectId == projectId && user.UserType == UserTypes.Internal
                && session.ExpiresAt > dbNow && Enumerable.Contains(active, session.Status)
            select session.FileName).ToListAsync(ct);
        return sessionNames.Any(UploadMaterialRules.IsStepFile);
    }

    // A name without a dot has no extension; it must not be mistaken for one (a file named "pdf").
    internal static string ExtensionOf(string name) =>
        name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : string.Empty;

    internal static bool HasUnsafeFileNameCharacter(string name) =>
        name.Any(character => char.IsControl(character) || character is '/' or '\\')
        || name.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format);

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

    private async Task<bool?> SessionExistsSafelyAsync(string id, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var context = EfDb.Use(conn);
            return await context.UploadSessions.AnyAsync(session => session.Id == id, ct);
        }
        catch (Exception error)
        {
            logger.LogWarning("确认上传会话提交结果失败 {SessionId} {Failure}", id, SafeFailureCode(error));
            return null;
        }
    }

    internal static SessionCommitRecoveryDecision SessionCommitRecovery(bool? sessionExists) =>
        sessionExists switch
        {
            true => new(Acknowledge: true, DeleteDirectory: false),
            false => new(Acknowledge: false, DeleteDirectory: false),
            null => new(Acknowledge: false, DeleteDirectory: false)
        };

    internal static string SafeFailureCode(Exception error) => error is MySqlException mysql
        ? $"{error.GetType().Name}:mysql-{mysql.Number}:0x{error.HResult:x8}"
        : $"{error.GetType().Name}:0x{error.HResult:x8}";

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

    internal static Task<DateTime> DbNowAsync(YfDbContext context, CancellationToken ct) =>
        DbClock.UtcNowAsync(context, ct);

    internal static UploadSessionRow ToRow(UploadSession session, DateTime dbNow) => new()
    {
        Id = session.Id,
        ProjectId = session.ProjectId,
        UploaderId = session.UploaderId,
        FileName = session.FileName,
        FileSize = session.FileSize,
        FileLastModified = session.FileLastModified,
        FileFingerprint = session.FileFingerprint,
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

    internal static FileRow ToRow(FileRecord file) => FileRowMapping.Map(file);

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

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
