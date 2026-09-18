using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Uploads;

public sealed record OemUploadInit(string FileName, ulong FileSize, string? FileMd5);

/// <summary>
/// Chunked, resumable uploads into the OEM quarantine. Only the actual sender of a draft
/// may upload. Quotas are decided under the vendor row lock: per-file and per-transfer
/// size, concurrent sessions per vendor, and the vendor's online storage (which counts
/// files in both directions plus bytes reserved by unfinished uploads). A merged file is
/// always QUARANTINED + PENDING and gets a scan job; it is never directly available.
/// </summary>
public sealed partial class OemUploadService(
    IDbContextFactory<YfDbContext> dbFactory,
    AppDb appDb,
    OemStorage storage,
    OemAuditWriter audit,
    ILogger<OemUploadService> logger)
{
    public async Task<object> InitAsync(OemActor actor, ulong transferId, OemUploadInit request, CancellationToken ct)
    {
        var name = ValidateFileName(request.FileName);
        var extension = ExtensionOf(name);
        if (request.FileSize == 0) throw ApiException.BadRequest("空文件不可上传");
        if (request.FileMd5 is not null && !Md5Pattern().IsMatch(request.FileMd5)) throw ApiException.BadRequest("文件 MD5 摘要格式无效");
        var md5 = request.FileMd5?.ToLowerInvariant();
        storage.Root();

        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var transfer = await OemTransferProgression.LockTransferAsync(uow, transferId, ct);
        OemTransferService.EnsureOwnDraft(current, transfer);
        if (current is InternalOemActor) await OemAuthorizer.RequireAsync(uow, current, OemPermissions.TransferCreate, ct);
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        if (settings.ReconcileRequired) throw new ApiException(503, 50302, "系统正在进行存储核对，暂时不能上传");
        if (!settings.AllowedExtensions(transfer.Direction).Contains(extension)) throw ApiException.BadRequest($"不允许上传 .{extension} 类型的文件");
        if (request.FileSize > settings.MaxFileSize) throw ApiException.BadRequest($"文件超过大小上限 {settings.MaxFileSize / 1024 / 1024} MB");

        if (md5 is not null)
        {
            var resumable = await uow.Db.OemUploadSessions.AsNoTracking().Where(session => session.TransferId == transferId
                    && session.UploaderRealm == current.Realm && session.UploaderId == current.Id && session.FileName == name
                    && session.FileSize == request.FileSize && session.FileMd5 == md5 && session.Status == UploadStatuses.Uploading
                    && session.ExpiresAt > uow.Now)
                .OrderByDescending(session => session.CreatedAt).FirstOrDefaultAsync(ct);
            if (resumable is not null)
                return new { sessionId = resumable.Id, resumable.ChunkSize, resumable.TotalChunks, uploadedChunks = UploadedChunks(resumable), resumed = true };
        }

        // Quota decisions for one vendor are serialised on the vendor row.
        await uow.Db.OemCompanies.FromSqlInterpolated($"SELECT * FROM oem_companies WHERE id = {transfer.OemCompanyId} FOR UPDATE").SingleAsync(ct);
        var onDisk = PayloadStatuses.OnDisk;
        var companyTransfers = uow.Db.OemTransfers.Where(item => item.OemCompanyId == transfer.OemCompanyId).Select(item => item.Id);
        var activeSessions = uow.Db.OemUploadSessions.Where(session => companyTransfers.Contains(session.TransferId)
            && (session.Status == UploadStatuses.Uploading || session.Status == UploadStatuses.Merging) && session.ExpiresAt > uow.Now);
        if (await activeSessions.CountAsync(ct) >= settings.MaxConcurrentPerCompany)
            throw ApiException.Conflict("该厂商同时进行的上传过多，请稍后重试");
        var storedBytes = (ulong)(await uow.Db.OemTransferFiles
            .Where(file => companyTransfers.Contains(file.TransferId) && Enumerable.Contains(onDisk, file.PayloadStatus))
            .SumAsync(file => (decimal?)file.SizeBytes, ct) ?? 0m);
        var reservedBytes = (ulong)(await activeSessions.SumAsync(session => (decimal?)session.ReservedBytes, ct) ?? 0m);
        if (storedBytes + reservedBytes + request.FileSize > settings.MaxStoragePerCompany)
            throw ApiException.Conflict("该厂商的在线存储配额不足，请等待已到期文件清理或联系管理员调整配额");
        var transferBytes = (ulong)(await OemTransferFiles.Members(uow.Db, transferId).Where(file => Enumerable.Contains(onDisk, file.PayloadStatus))
            .SumAsync(file => (decimal?)file.SizeBytes, ct) ?? 0m);
        var transferReserved = (ulong)(await activeSessions.Where(session => session.TransferId == transferId)
            .SumAsync(session => (decimal?)session.ReservedBytes, ct) ?? 0m);
        if (transferBytes + transferReserved + request.FileSize > settings.MaxTransferSize)
            throw ApiException.BadRequest($"单个传递单的附件总大小不能超过 {settings.MaxTransferSize / 1024 / 1024} MB");
        // Merging needs room for the chunks and the merged copy at the same time.
        storage.EnsureFreeSpace(checked(request.FileSize * 2));

        var chunkSize = settings.ChunkSize;
        var totalChunks = request.FileSize / chunkSize + (request.FileSize % chunkSize == 0 ? 0UL : 1UL);
        if (totalChunks > uint.MaxValue) throw ApiException.BadRequest("文件分片数量超出限制");
        var sessionId = Guid.NewGuid().ToString("D");
        var directory = storage.SessionDirectory(sessionId, create: true, ct);
        try
        {
            uow.Db.OemUploadSessions.Add(new OemUploadSession
            {
                Id = sessionId, TransferId = transferId, UploaderRealm = current.Realm, UploaderId = current.Id, FileName = name,
                FileSize = request.FileSize, FileMd5 = md5, ChunkSize = chunkSize, TotalChunks = (uint)totalChunks,
                TempDir = Path.GetRelativePath(storage.Root(), directory).Replace(Path.DirectorySeparatorChar, '/'),
                ReservedBytes = request.FileSize, Status = UploadStatuses.Uploading, ExpiresAt = uow.Now.Add(settings.UploadSessionTtl),
                CreatedAt = uow.Now, UpdatedAt = uow.Now,
            });
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
        }
        catch
        {
            await TryDeleteDirectoryAsync(directory);
            throw;
        }
        return new { sessionId, chunkSize, totalChunks = (uint)totalChunks, uploadedChunks = Array.Empty<uint>(), resumed = false };
    }

    public async Task<object> GetAsync(OemActor actor, string sessionId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var session = await LoadOwnSessionAsync(uow, current, sessionId, forUpdate: false, ct);
        return new
        {
            sessionId = session.Id, session.Status, session.ChunkSize, session.TotalChunks, session.FileName, session.FileSize,
            uploadedChunks = session.Status == UploadStatuses.Uploading ? UploadedChunks(session) : [], resultFileId = session.ResultFileId,
            expired = session.ExpiresAt <= uow.Now,
        };
    }

    public async Task PutChunkAsync(OemActor actor, string sessionId, int index, HttpRequest request, CancellationToken ct)
    {
        if (index < 0) throw ApiException.BadRequest("分片序号越界");
        OemUploadSession session;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
            session = await LoadOwnSessionAsync(uow, current, sessionId, forUpdate: false, ct);
            if (session.Status != UploadStatuses.Uploading) throw ApiException.Conflict("会话不可上传（可能已合并或放弃）");
            if (session.ExpiresAt <= uow.Now) throw ApiException.Conflict("上传会话已过期，请重新发起");
        }
        if ((uint)index >= session.TotalChunks) throw ApiException.BadRequest("分片序号越界");
        var expected = ExpectedChunkLength(session, (uint)index);
        if (request.ContentLength is long length && (ulong)length != expected)
            throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {length}");
        storage.EnsureFreeSpace(expected);
        var path = storage.ChunkPath(sessionId, (uint)index);
        storage.SessionDirectory(sessionId, create: true, ct);
        var temporary = path + $".{Guid.NewGuid():N}.uploading";
        try
        {
            await WriteExactAsync(request.Body, temporary, expected, ct);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public async Task AbortAsync(OemActor actor, string sessionId, CancellationToken ct)
    {
        await using var mergeLock = await AcquireMergeLockAsync(sessionId, ct);
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
            var session = await LoadOwnSessionAsync(uow, current, sessionId, forUpdate: true, ct);
            if (session.Status == UploadStatuses.Completed) throw ApiException.Conflict("会话已完成，不可放弃");
            if (session.Status is UploadStatuses.Uploading or UploadStatuses.Merging)
            {
                session.Status = UploadStatuses.Aborted;
                session.ReservedBytes = 0;
                session.UpdatedAt = uow.Now;
                await uow.Db.SaveChangesAsync(ct);
            }
            await uow.CommitAsync(ct);
        }
        await TryDeleteDirectoryAsync(storage.SessionDirectory(sessionId, create: false, ct));
    }

    public async Task<object> MergeAsync(OemActor actor, string sessionId, CancellationToken ct)
    {
        await using var mergeLock = await AcquireMergeLockAsync(sessionId, ct);
        OemUploadSession session;
        OemActor current;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
            session = await LoadOwnSessionAsync(uow, current, sessionId, forUpdate: true, ct);
            if (session.Status == UploadStatuses.Completed && session.ResultFileId is ulong done)
            {
                await uow.CommitAsync(ct);
                return await FileJsonAsync(done, ct);
            }
            if (session.Status is not (UploadStatuses.Uploading or UploadStatuses.Merging)) throw ApiException.Conflict("会话已失效");
            if (session.ExpiresAt <= uow.Now) throw ApiException.Conflict("上传会话已过期，请重新发起");
            var missing = Enumerable.Range(0, (int)session.TotalChunks).Select(i => (uint)i).Except(UploadedChunks(session)).Count();
            if (missing > 0) throw ApiException.BadRequest($"分片不完整：还缺 {missing} 个分片");
            session.Status = UploadStatuses.Merging;
            session.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
        }

        var directory = storage.SessionDirectory(sessionId, create: false, ct);
        var mergeTemp = Path.Combine(directory, "merged.tmp");
        TryDeleteFile(mergeTemp);
        string? movedTo = null;
        var committed = false;
        try
        {
            var root = storage.Root();
            var chunks = new List<string>((int)session.TotalChunks);
            for (uint i = 0; i < session.TotalChunks; i++)
                chunks.Add(await FileStorage.ResolveExistingFileAsync(root, storage.ChunkPath(sessionId, i), ct));
            var hash = await FileStorage.HashAndCopyAsync(chunks, mergeTemp, session.FileSize, ct);
            if (hash.Bytes != session.FileSize) throw ApiException.BadRequest($"合并文件大小不符：期望 {session.FileSize}，实际 {hash.Bytes}");
            if (session.FileMd5 is not null && !hash.Md5.Equals(session.FileMd5, StringComparison.OrdinalIgnoreCase))
                throw ApiException.BadRequest("文件 MD5 校验失败，请重新上传");

            var storedName = OemStorage.NewStoredName();
            var relative = OemStorage.QuarantineRelative(storedName);
            var target = storage.Absolute(relative);
            storage.EnsureParent(target, ct);
            ulong fileId;
            await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
            {
                current = await OemAuthorizer.RecheckAsync(uow, current, ct);
                var transfer = await OemTransferProgression.LockTransferAsync(uow, session.TransferId, ct);
                OemTransferService.EnsureOwnDraft(current, transfer);
                var locked = await LoadOwnSessionAsync(uow, current, sessionId, forUpdate: true, ct);
                if (locked.Status != UploadStatuses.Merging) throw ApiException.Conflict("上传会话状态已变化，请重新查询");
                File.Move(mergeTemp, target, overwrite: false);
                movedTo = target;
                var extension = ExtensionOf(session.FileName);
                var file = new OemTransferFile
                {
                    TransferId = session.TransferId,
                    UploadedByInternalUserId = current is InternalOemActor ? current.Id : null,
                    UploadedByOemAccountId = current is OemAccountActor ? current.Id : null,
                    OriginalName = session.FileName, StoredName = storedName, Ext = extension,
                    MimeType = FileStorage.MimeType(session.FileName), SizeBytes = session.FileSize, Md5 = hash.Md5, Sha256 = hash.Sha256,
                    StoragePath = relative, PayloadStatus = PayloadStatuses.Quarantined, ScanStatus = ScanStatuses.Pending,
                    PurgeAttemptCount = 0, ConcurrencyVersion = 0, CreatedAt = uow.Now, UpdatedAt = uow.Now,
                };
                uow.Db.OemTransferFiles.Add(file);
                await uow.Db.SaveChangesAsync(ct);
                uow.Db.OemFileScanJobs.Add(new OemFileScanJob
                {
                    FileId = file.Id, FileSha256 = hash.Sha256, FileSizeBytes = session.FileSize, Status = ScanJobStatuses.Pending,
                    AttemptCount = 0, NextAttemptAt = uow.Now, ConcurrencyVersion = 0, CreatedAt = uow.Now,
                });
                locked.Status = UploadStatuses.Completed;
                locked.ResultFileId = file.Id;
                locked.ReservedBytes = 0;
                locked.UpdatedAt = uow.Now;
                OemTransferProgression.Touch(uow, transfer);
                await uow.Db.SaveChangesAsync(ct);
                await audit.WriteAsync(uow, current, "OEM_FILE_UPLOAD", "oem_file", file.Id, new
                {
                    targetName = file.OriginalName, transferId = file.TransferId, file.SizeBytes, file.Sha256,
                }, ct);
                await uow.CommitAsync(ct);
                committed = true;
                fileId = file.Id;
            }
            await TryDeleteDirectoryAsync(directory);
            return await FileJsonAsync(fileId, ct);
        }
        catch
        {
            if (movedTo is not null && !committed) TryDeleteFile(movedTo);
            TryDeleteFile(mergeTemp);
            await ResetMergeAsync(sessionId);
            throw;
        }
    }

    /// <summary>Maintenance: expire stale sessions, release their reservations and remove chunk directories.</summary>
    public async Task<int> ExpireStaleSessionsAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return 0;
        string[] ids;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var stale = await uow.Db.OemUploadSessions
                .Where(session => (session.Status == UploadStatuses.Uploading || session.Status == UploadStatuses.Merging) && session.ExpiresAt <= uow.Now)
                .Take(200).ToArrayAsync(ct);
            foreach (var session in stale)
            {
                session.Status = UploadStatuses.Expired;
                session.ReservedBytes = 0;
                session.UpdatedAt = uow.Now;
            }
            await uow.Db.SaveChangesAsync(ct);
            await uow.CommitAsync(ct);
            ids = stale.Select(session => session.Id).ToArray();
        }
        foreach (var id in ids) await TryDeleteDirectoryAsync(storage.SessionDirectory(id, create: false, ct));
        return ids.Length;
    }

    private async Task<object> FileJsonAsync(ulong fileId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var file = await uow.Db.OemTransferFiles.AsNoTracking().SingleAsync(item => item.Id == fileId, ct);
        return new { file.Id, file.TransferId, file.OriginalName, file.Ext, file.SizeBytes, file.Sha256, file.ScanStatus, file.PayloadStatus, file.CreatedAt };
    }

    private async Task ResetMergeAsync(string sessionId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, timeout.Token);
            await uow.Db.OemUploadSessions.Where(session => session.Id == sessionId && session.Status == UploadStatuses.Merging)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.Status, UploadStatuses.Uploading)
                    .SetProperty(session => session.UpdatedAt, uow.Now), timeout.Token);
            await uow.CommitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning("OEM merge reset for session failed ({ErrorType}); it expires with the session.", error.GetType().Name);
        }
    }

    private async Task<MySqlNamedLockLease> AcquireMergeLockAsync(string sessionId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _)) throw ApiException.NotFound();
        var connection = await appDb.OpenAsync(ct);
        try
        {
            var name = MySqlNamedLock.Name("oem-merge", connection.Database, sessionId);
            var lease = await MySqlNamedLock.TryAcquireAsync(connection, name, 0, ct) ?? throw ApiException.Conflict("该文件正在合并，请稍候");
            return new MySqlNamedLockLease(connection, lease);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<OemUploadSession> LoadOwnSessionAsync(OemUnitOfWork uow, OemActor actor, string sessionId, bool forUpdate, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _)) throw ApiException.NotFound();
        var session = forUpdate
            ? await uow.Db.OemUploadSessions.FromSqlInterpolated($"SELECT * FROM oem_upload_sessions WHERE id = {sessionId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await uow.Db.OemUploadSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, ct);
        if (session is null || session.UploaderRealm != actor.Realm || session.UploaderId != actor.Id) throw ApiException.NotFound();
        return session;
    }

    private List<uint> UploadedChunks(OemUploadSession session)
    {
        var result = new List<uint>();
        for (uint index = 0; index < session.TotalChunks; index++)
        {
            var path = storage.ChunkPath(session.Id, index);
            if (File.Exists(path) && (ulong)new FileInfo(path).Length == ExpectedChunkLength(session, index)) result.Add(index);
        }
        return result;
    }

    private static ulong ExpectedChunkLength(OemUploadSession session, uint index) =>
        index == session.TotalChunks - 1 ? session.FileSize - (ulong)session.ChunkSize * (session.TotalChunks - 1) : session.ChunkSize;

    private static async Task WriteExactAsync(Stream body, string path, ulong expected, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[64 * 1024];
        ulong total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += (ulong)read;
            if (total > expected) throw ApiException.BadRequest("分片大小超出预期");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw ApiException.BadRequest($"分片大小不符：期望 {expected}，实际 {total}");
        await output.FlushAsync(ct);
    }

    internal static string ValidateFileName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.EnumerateRunes().Count() > 255 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            throw ApiException.BadRequest("文件名需为 1~255 个字符且不能包含路径或特殊字符");
        if (!name.Contains('.') || name.EndsWith('.')) throw ApiException.BadRequest("文件必须带有扩展名");
        return name;
    }

    internal static string ExtensionOf(string name) => name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();

    private async Task TryDeleteDirectoryAsync(string directory)
    {
        try { await storage.DeleteDirectoryAsync(directory, CancellationToken.None); }
        catch (Exception error) { logger.LogWarning("OEM upload directory cleanup failed ({ErrorType}).", error.GetType().Name); }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex Md5Pattern();
}

/// <summary>Named MySQL lock plus the connection that owns it (the lock dies with the connection).</summary>
internal sealed class MySqlNamedLockLease(MySqlConnector.MySqlConnection connection, IAsyncDisposable namedLock) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await namedLock.DisposeAsync();
        await connection.DisposeAsync();
    }
}
