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

// Merging uploaded chunks into a stored file, and recovering merges interrupted by a crash or lost commit.
public sealed partial class UploadService
{
    public async Task<FileResponse> MergeAsync(HttpContext context, string sessionId, CancellationToken ct)
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

    private async Task<FileResponse> DoMergeAsync(MySqlConnection conn, HttpContext context, CurrentUser actor,
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
                Status = FileStatuses.Available,
                CreatedAt = now
            };
            ef.Files.Add(file);
            await ef.SaveChangesAsync(ct);
            var fileId = file.Id;
            await EnqueueFileNoticeAsync(conn, tx, project.Id, fileId, session.FileName, direction, current, ct);
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

    private async Task<FileResponse> CompletedFileAsync(MySqlConnection conn, UploadSessionRow session, ulong uploaderId, CancellationToken ct)
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

    private const int FileSummaryDelayMinutes = 2;
    private const int FileSummaryMaximumBodyBytes = 60_000;

    private async Task EnqueueFileNoticeAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId,
        ulong fileId, string fileName, string direction, CurrentUser uploader, CancellationToken ct)
    {
        var policy = await EmailNotificationPolicy.LoadAsync(conn, tx, ct);
        if (!policy.Allows("FILE_UPLOADED", null)) return;
        await using var context = EfDb.Use(conn, tx);
        var project = await context.Projects.SingleAsync(item => item.Id == projectId, ct);
        var supplierActive = await context.Suppliers.AnyAsync(item => item.Id == project.SupplierId && item.Status == AccountStatuses.Active, ct);
        var permittedUserIds = AccessService.UsersWithPermission(context, "project:list");
        var recipientsQuery = context.Users.Where(user => user.Status == AccountStatuses.Active && user.Id != uploader.Id
            && permittedUserIds.Contains(user.Id));
        recipientsQuery = uploader.UserType == UserTypes.Supplier
            ? recipientsQuery.Where(user => user.UserType == UserTypes.Internal && user.Id == project.ResponsibleUserId)
            : recipientsQuery.Where(user => user.UserType == UserTypes.Supplier && user.SupplierId == project.SupplierId && supplierActive);
        var recipients = await recipientsQuery.OrderBy(user => user.Id)
            .Select(user => new NoticeRecipient
            {
                Id = user.Id, Email = user.Email, EmployeeNo = user.EmployeeNo, RealName = user.RealName, UserType = user.UserType
            }).ToArrayAsync(ct);
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
            await EnqueueFileSummaryForRecipientAsync(
                conn, tx, projectId, project.Name, fileId, fileName, direction, uploader.EmployeeNo,
                recipient.Id, recipient.Email, options.WebBaseUrl, createdAt, ct);
        }
    }

    internal static async Task EnqueueFileSummaryForRecipientAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong projectId,
        string projectName,
        ulong fileId,
        string fileName,
        string direction,
        string uploaderEmployeeNo,
        ulong recipientUserId,
        string recipientEmail,
        string webBaseUrl,
        DateTime createdAt,
        CancellationToken ct)
    {
        if (direction is not ("C2S" or "S2C"))
            throw new InvalidOperationException("文件通知方向无效");

        var safeProjectName = SafeMailLine(projectName, 100);
        var safeFileName = SafeMailLine(fileName, 240);
        var safeEmployeeNo = SafeMailLine(uploaderEmployeeNo, 64);
        var target = $"{webBaseUrl.Trim().TrimEnd('/')}/projects/{projectId}?tab=files&target={fileId}";
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUrl)
            || !targetUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && !targetUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("邮件文件链接配置无效");

        var directionText = direction == "C2S" ? "公司 → 供应商" : "供应商 → 公司";
        var entry = $"- {safeFileName}\n  {targetUrl.AbsoluteUri}\n  上传人：工号 {safeEmployeeNo}\n";
        var body = $"项目：{safeProjectName}\n方向：{directionText}\n以下文件请登录平台查看并下载（邮件不含附件）：\n\n{entry}";
        var subject = $"[协作平台] 项目「{safeProjectName}」文件上传摘要";
        var dedupeKey = FileSummaryDedupeKey(projectId, recipientUserId, direction);
        var nextAttemptAt = createdAt.AddMinutes(FileSummaryDelayMinutes);

        await using var context = EfDb.Use(conn, tx);
        // A stable unique key identifies only the currently mergeable window. Stale,
        // oversized, claimed and retried rows release it so a fresh window can start.
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE email_outbox
            SET dedupe_key=NULL
            WHERE dedupe_key={{dedupeKey}}
              AND (status<>'PENDING' OR retry_count<>0 OR sent_at IS NOT NULL OR next_attempt_at IS NULL
                   OR OCTET_LENGTH(body)+OCTET_LENGTH({{entry}})>{{FileSummaryMaximumBodyBytes}})
            """, ct);
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO email_outbox
                (event_type,project_id,dedupe_key,recipient_user_id,recipient_email,
                 subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
            VALUES
                ('FILE_UPLOADED',{{projectId}},{{dedupeKey}},{{recipientUserId}},{{recipientEmail}},
                 {{subject}},{{body}},'PENDING',0,{{nextAttemptAt}},NULL,NULL,{{createdAt}})
            ON DUPLICATE KEY UPDATE
                recipient_email=IF(status='PENDING' AND retry_count=0 AND sent_at IS NULL AND next_attempt_at IS NOT NULL,
                                   VALUES(recipient_email),recipient_email),
                body=IF(status='PENDING' AND retry_count=0 AND sent_at IS NULL AND next_attempt_at IS NOT NULL,
                        CONCAT(body,{{entry}}),body)
            """, ct);
    }

    internal static string FileSummaryDedupeKey(ulong projectId, ulong recipientUserId, string direction) =>
        $"file-summary:{projectId}:{recipientUserId}:{direction}";

    internal static string SafeMailLine(string value, int maximumRunes)
    {
        var normalized = Regex.Replace(value, @"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\s]+", " ").Trim();
        return string.Concat(normalized.EnumerateRunes().Take(maximumRunes));
    }

    internal static FileResponse FileJson(FileRow file) => new(
        file.Id, file.ProjectId, file.UploaderId, file.Direction, file.OriginalName, file.Ext, file.SizeBytes,
        file.MimeType, file.Sha256, file.CreatedAt);
}
