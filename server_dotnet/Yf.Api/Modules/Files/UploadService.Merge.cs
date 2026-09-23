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

    internal static FileResponse FileJson(FileRow file) => new(
        file.Id, file.ProjectId, file.UploaderId, file.Direction, file.OriginalName, file.Ext, file.SizeBytes,
        file.MimeType, file.Sha256, file.CreatedAt);
}
