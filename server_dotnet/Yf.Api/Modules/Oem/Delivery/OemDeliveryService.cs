using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Modules.Oem.Delivery;

public static class DownloadPurposes
{
    public const string Download = "DOWNLOAD";
    public const string Preview = "PREVIEW";
    public const string Review = "REVIEW";
}

public static class DownloadSessionStatuses
{
    public const string Started = "STARTED";
    public const string Completed = "COMPLETED";
    public const string Expired = "EXPIRED";
}

public static class DownloadLeaseStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// Preview, download and delivery receipts. Every request — including each Range
/// request of a resumed download — re-validates the live account, login session,
/// transfer visibility and file state. A download counts as received only when one
/// logical session has delivered every byte; the first such recipient session sets the
/// file's receipt time and (per the retention snapshot) its purge time. Active streams
/// hold a lease that the purge worker honours for a bounded drain period.
/// </summary>
public sealed class OemDeliveryService(
    IDbContextFactory<YfDbContext> dbFactory,
    OemStorage storage,
    OemDownloadGrantService grants,
    OemDownloadRateLimiter rateLimiter,
    OemEventDispatcher dispatcher,
    OemAuditWriter audit,
    AppOptions options,
    ILogger<OemDeliveryService> logger)
{
    public const long PreviewLimitBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Inline preview (bearer-authenticated). Never counts as a receipt.</summary>
    public async Task<IResult> PreviewAsync(OemActor actor, ulong fileId, CancellationToken ct)
    {
        string path;
        OemTransferFile file;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
            var (loaded, _, capabilities) = await LoadReadableAsync(uow, current, fileId, ct);
            file = loaded;
            if (file.SizeBytes > PreviewLimitBytes) throw new ApiException(413, 41301, "文件超过在线预览上限，请下载后查看");
            path = await storage.ResolveExistingFileAsync(file.StoragePath, ct);
            await audit.WriteAsync(uow, current, "OEM_FILE_PREVIEW", "oem_file", fileId, new
            {
                targetName = file.OriginalName, transferId = file.TransferId, purpose = capabilities.ContentAccess.ToString().ToUpperInvariant(),
            }, ct);
            await uow.CommitAsync(ct);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return Results.File(stream, file.MimeType ?? "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>Starts a logical download session and hands the browser a path-scoped HttpOnly grant cookie.</summary>
    public async Task<OemDownloadSessionResponse> StartAsync(HttpContext context, OemActor actor, ulong fileId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var (file, transfer, capabilities) = await LoadReadableAsync(uow, current, fileId, ct);
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        var deadline = uow.Now.Add(settings.DownloadSessionTtl);
        if (file.PurgeDueAt is DateTime due && due.Add(settings.PurgeDrain) < deadline) deadline = due.Add(settings.PurgeDrain);
        var session = new OemDownloadSession
        {
            Id = Guid.NewGuid().ToString("D"), FileId = file.Id, FileStoredName = file.StoredName, FileSha256 = file.Sha256,
            ActorRealm = current.Realm, ActorId = current.Id, LoginSessionId = current.LoginSessionId,
            Purpose = capabilities.ContentAccess == ContentPurpose.Review ? DownloadPurposes.Review : DownloadPurposes.Download,
            RecipientSide = capabilities.IsRecipient, ExpectedSize = file.SizeBytes, Status = DownloadSessionStatuses.Started,
            AbsoluteDeadline = deadline, ConcurrencyVersion = 0, CreatedAt = uow.Now,
        };
        uow.Db.OemDownloadSessions.Add(session);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_DOWNLOAD_START", "oem_file", fileId, new
        {
            targetName = file.OriginalName, transferId = transfer.Id, downloadSessionId = session.Id, session.Purpose, recipientSide = session.RecipientSide,
        }, ct);
        await uow.CommitAsync(ct);
        var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(Min(deadline, uow.Now.Add(GrantLifetime)), DateTimeKind.Utc));
        var token = grants.Issue(new DownloadGrant(current.Realm, current.Id, current.LoginSessionId, session.Id, fileId, file.StoredName, expiresAt.ToUnixTimeSeconds()));
        context.Response.Cookies.Append(OemDownloadGrantService.CookieName(fileId), token, new CookieOptions
        {
            HttpOnly = true, Secure = options.CookieSecure, SameSite = SameSiteMode.Strict,
            Path = OemDownloadGrantService.CookiePath(fileId), Expires = expiresAt,
        });
        return new OemDownloadSessionResponse(session.Id, OemDownloadGrantService.CookiePath(fileId), expiresAt.UtcDateTime, session.Purpose);
    }

    /// <summary>Streams the file (or one byte range) for a cookie-authenticated download session.</summary>
    public async Task StreamAsync(HttpContext context, ulong fileId, CancellationToken ct)
    {
        if (!context.Request.Cookies.TryGetValue(OemDownloadGrantService.CookieName(fileId), out var token))
            throw ApiException.Unauthorized("缺少下载凭证，请从平台页面发起下载");
        var grant = grants.Parse(token, fileId, DateTimeOffset.UtcNow);
        string leaseId;
        OemDownloadSession session;
        OemTransferFile file;
        ByteRange range;
        DateTime hardDeadline;
        TimeSpan idleTimeout;
        string path;
        await using (var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct))
        {
            var actor = await LiveActorAsync(uow, grant, ct);
            session = await uow.Db.OemDownloadSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == grant.DownloadSessionId, ct)
                ?? throw ApiException.Unauthorized("下载会话不存在");
            if (session.FileId != fileId || session.ActorRealm != actor.Realm || session.ActorId != actor.Id || session.FileStoredName != grant.StoredName)
                throw ApiException.Forbidden();
            if (session.Status == DownloadSessionStatuses.Expired || session.AbsoluteDeadline <= uow.Now)
                throw new ApiException(410, 41001, "下载会话已过期，请重新发起下载");
            var settings = await OemSettings.LoadAsync(uow.Db, ct);
            if (!rateLimiter.Allow(actor.Realm, actor.Id, settings.MaxRequestsPerMinute))
                throw new ApiException(429, 42901, "下载请求过于频繁，请稍后再试");
            var (readable, _, capabilities) = await LoadReadableAsync(uow, actor, fileId, ct);
            file = readable;
            if (file.StoredName != session.FileStoredName || file.Sha256 != session.FileSha256) throw new ApiException(410, 41002, "文件已变化，请重新发起下载");
            // The receipt side is decided by live capabilities, never by the client.
            if (capabilities.IsRecipient != session.RecipientSide) throw ApiException.Forbidden();
            var parsed = ByteRange.Parse(context.Request.Headers.Range.ToString(), file.SizeBytes, out var unsatisfiable);
            if (unsatisfiable || parsed is null)
            {
                context.Response.Headers.ContentRange = $"bytes */{file.SizeBytes}";
                throw new ApiException(416, 41601, "请求的字节范围无效");
            }
            range = parsed.Value;

            // Admission and purge claiming serialise on the file row: a purge cannot slip
            // between this check and opening the file.
            var locked = await uow.Db.OemTransferFiles.FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {fileId} FOR UPDATE").SingleAsync(ct);
            if (locked.PayloadStatus != PayloadStatuses.Available || locked.PurgeDueAt is DateTime due && due <= uow.Now)
                throw new ApiException(410, 41003, "文件已到期或不可用");
            var active = await uow.Db.OemDownloadLeases.CountAsync(lease => lease.SessionId == session.Id
                && lease.Status == DownloadLeaseStatuses.Active && lease.LeaseUntil > uow.Now, ct);
            if (active >= settings.MaxParallelPerSession) throw new ApiException(429, 42902, "同一下载的并行请求过多");
            idleTimeout = settings.IdleTimeout;
            hardDeadline = Min(session.AbsoluteDeadline, uow.Now.Add(settings.MaxRequestDuration));
            if (locked.PurgeDueAt is DateTime purgeDue) hardDeadline = Min(hardDeadline, purgeDue.Add(settings.PurgeDrain));
            leaseId = Guid.NewGuid().ToString("D");
            uow.Db.OemDownloadLeases.Add(new OemDownloadLease
            {
                Id = leaseId, SessionId = session.Id, FileId = fileId, Owner = Owner, StartedAt = uow.Now, LastProgressAt = uow.Now,
                LeaseUntil = Min(hardDeadline, uow.Now.Add(idleTimeout)), HardDeadline = hardDeadline, Status = DownloadLeaseStatuses.Active,
            });
            await uow.Db.SaveChangesAsync(ct);
            path = await storage.ResolveExistingFileAsync(locked.StoragePath, ct);
            await uow.CommitAsync(ct);
        }

        var delivered = false;
        try
        {
            delivered = await SendRangeAsync(context, path, file, range, leaseId, hardDeadline, idleTimeout, ct);
        }
        finally
        {
            await FinishAsync(leaseId, session, file, range, delivered, CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<OemDownloadStatusResponse>> StatusAsync(OemActor actor, ulong fileId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var sessions = await uow.Db.OemDownloadSessions.AsNoTracking()
            .Where(item => item.FileId == fileId && item.ActorRealm == current.Realm && item.ActorId == current.Id)
            .OrderByDescending(item => item.CreatedAt).Take(5).ToArrayAsync(ct);
        var ids = sessions.Select(item => item.Id).ToArray();
        var ranges = await uow.Db.OemDownloadRanges.AsNoTracking().Where(range => Enumerable.Contains(ids, range.SessionId)).ToArrayAsync(ct);
        return sessions.Select(item => new OemDownloadStatusResponse(
            item.Id, item.Status, item.Purpose, item.CreatedAt, item.CompletedAt, item.ExpectedSize,
            ranges.Where(range => range.SessionId == item.Id).Aggregate(0UL, (sum, range) => sum + range.EndOffset - range.StartOffset + 1))).ToArray();
    }

    private async Task<bool> SendRangeAsync(HttpContext context, string path, OemTransferFile file, ByteRange range, string leaseId,
        DateTime hardDeadline, TimeSpan idleTimeout, CancellationToken ct)
    {
        var partial = range.Start != 0 || range.End != file.SizeBytes - 1;
        var response = context.Response;
        response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength = (long)range.Length;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.CacheControl = "private, no-store";
        if (partial) response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{file.SizeBytes}";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(file.OriginalName);
        response.Headers.ContentDisposition = disposition.ToString();

        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remainingUntilDeadline = DateTime.SpecifyKind(hardDeadline, DateTimeKind.Utc) - DateTime.UtcNow;
        guard.CancelAfter(remainingUntilDeadline > TimeSpan.Zero ? remainingUntilDeadline : TimeSpan.Zero);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek((long)range.Start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = range.Length;
        var nextHeartbeat = DateTime.UtcNow.Add(Heartbeat);
        try
        {
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min((ulong)buffer.Length, remaining)), guard.Token);
                if (read == 0) throw new IOException("文件长度与记录不符");
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(guard.Token))
                {
                    // A client that stops reading is cut off after the idle timeout.
                    idle.CancelAfter(idleTimeout);
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                }
                remaining -= (ulong)read;
                if (DateTime.UtcNow >= nextHeartbeat)
                {
                    nextHeartbeat = DateTime.UtcNow.Add(Heartbeat);
                    if (!await HeartbeatAsync(leaseId, idleTimeout, guard.Token)) return false;
                }
            }
            await response.Body.FlushAsync(guard.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Idle timeout or hard deadline: abort the connection so the client sees an incomplete download.
            context.Abort();
            return false;
        }
    }

    /// <summary>Extends the lease; false when the purge worker cancelled it (the stream must stop).</summary>
    private async Task<bool> HeartbeatAsync(string leaseId, TimeSpan idleTimeout, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var lease = await uow.Db.OemDownloadLeases.FromSqlInterpolated($"SELECT * FROM oem_download_leases WHERE id = {leaseId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (lease is null || lease.Status != DownloadLeaseStatuses.Active || lease.HardDeadline <= uow.Now) return false;
        lease.LastProgressAt = uow.Now;
        lease.LeaseUntil = Min(lease.HardDeadline, uow.Now.Add(idleTimeout));
        await uow.Db.SaveChangesAsync(ct);
        await uow.CommitAsync(ct);
        return true;
    }

    internal async Task FinishAsync(string leaseId, OemDownloadSession session, OemTransferFile file, ByteRange range, bool delivered, CancellationToken ct)
    {
        try
        {
            await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
            // Keep the same lock order as purge: transfer -> file -> session -> lease.
            // Otherwise a purge holding the transfer/file rows can deadlock with a
            // completed download that held its lease before recording the receipt.
            await OemTransferProgression.LockTransferAsync(uow, file.TransferId, ct);
            var lockedFile = await uow.Db.OemTransferFiles
                .FromSqlInterpolated($"SELECT * FROM oem_transfer_files WHERE id = {file.Id} FOR UPDATE").SingleAsync(ct);
            var lockedSession = await uow.Db.OemDownloadSessions
                .FromSqlInterpolated($"SELECT * FROM oem_download_sessions WHERE id = {session.Id} FOR UPDATE").SingleAsync(ct);
            var lease = await uow.Db.OemDownloadLeases.FromSqlInterpolated($"SELECT * FROM oem_download_leases WHERE id = {leaseId} FOR UPDATE").SingleAsync(ct);
            var leaseValid = lease.Status == DownloadLeaseStatuses.Active;
            if (leaseValid) lease.Status = DownloadLeaseStatuses.Released;
            await uow.Db.SaveChangesAsync(ct);
            if (delivered && leaseValid) await RecordDeliveryAsync(uow, lockedSession, lockedFile, range, ct);
            await dispatcher.CommitAsync(uow, ct);
        }
        catch (Exception error)
        {
            logger.LogWarning("OEM download bookkeeping for session {SessionId} failed ({ErrorType}).", session.Id, error.GetType().Name);
        }
    }

    /// <summary>Merges the delivered range; completes the session and records the first receipt when every byte was delivered.</summary>
    private async Task RecordDeliveryAsync(OemUnitOfWork uow, OemDownloadSession session, OemTransferFile file, ByteRange range, CancellationToken ct)
    {
        if (session.Status != DownloadSessionStatuses.Started) return;
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        var existing = await uow.Db.OemDownloadRanges.Where(item => item.SessionId == session.Id).ToArrayAsync(ct);
        var set = new ByteRangeSet(existing.Select(item => (item.StartOffset, item.EndOffset)));
        set.Add(range.Start, range.End);
        if (set.Ranges.Count > settings.MaxRangesPerSession)
        {
            // Refuse to grow the evidence further, but keep what was already recorded.
            logger.LogWarning("OEM download session {SessionId} exceeded its range budget.", session.Id);
            return;
        }
        uow.Db.OemDownloadRanges.RemoveRange(existing);
        uow.Db.OemDownloadRanges.AddRange(set.Ranges.Select(item => new OemDownloadRange { SessionId = session.Id, StartOffset = item.Start, EndOffset = item.End }));
        session.LastProgressAt = uow.Now;
        session.ConcurrencyVersion++;
        if (set.Covers(session.ExpectedSize))
        {
            session.Status = DownloadSessionStatuses.Completed;
            session.CompletedAt = uow.Now;
        }
        await uow.Db.SaveChangesAsync(ct);
        if (session.Status != DownloadSessionStatuses.Completed) return;

        var transferId = file.TransferId;
        var transfer = await uow.Db.OemTransfers.SingleAsync(item => item.Id == transferId, ct);
        var downloader = await AuditActorOfAsync(uow, session, ct);
        await audit.WriteAsync(uow, downloader, "OEM_DOWNLOAD_COMPLETE", "oem_file", file.Id, new
        {
            targetName = file.OriginalName, transferId, downloadSessionId = session.Id, session.Purpose, recipientSide = session.RecipientSide,
        }, ct);
        if (!session.RecipientSide || file.FirstRecipientDownloadAt is not null) return;
        var snapshot = new RetentionSnapshot(transfer.RetentionMode!, transfer.ReleaseTtlMinutes, transfer.ReceiptGraceMinutes);
        file.FirstRecipientDownloadAt = uow.Now;
        file.FirstRecipientRealm = session.ActorRealm;
        file.FirstRecipientId = session.ActorId;
        var due = snapshot.Strategy.PurgeDueAfterFirstReceipt(snapshot, file.PurgeDueAt, transfer.ExpiresAt, uow.Now);
        if (due != file.PurgeDueAt)
        {
            file.PurgeDueAt = due;
            file.PurgeReason = PurgeReasons.Retention;
        }
        file.ConcurrencyVersion++;
        file.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, downloader, "OEM_FILE_FIRST_RECEIPT", "oem_file", file.Id,
            new { targetName = file.OriginalName, transferId, file.PurgeDueAt }, ct);
        uow.Raise(new FirstReceiptEvent(transferId, file.Id));
    }

    /// <summary>Audit identity of the downloader (bookkeeping runs after the request, so it is rebuilt from the session).</summary>
    private static async Task<OemActor> AuditActorOfAsync(OemUnitOfWork uow, OemDownloadSession session, CancellationToken ct)
    {
        if (session.ActorRealm != OemRealms.Oem)
            return new InternalOemActor(new CurrentUser(session.ActorId, string.Empty, UserTypes.Internal, null, session.LoginSessionId), session.LoginSessionId);
        var account = await uow.Db.OemAccounts.AsNoTracking().Where(item => item.Id == session.ActorId)
            .Select(item => new { item.EmployeeNo, item.RealName, item.OemCompanyId }).SingleAsync(ct);
        return new OemAccountActor(session.ActorId, account.EmployeeNo, account.RealName, account.OemCompanyId, session.LoginSessionId);
    }

    /// <summary>File, transfer and capability for a content read; throws unless the caller may read a ready, unexpired file.</summary>
    private static async Task<(OemTransferFile File, OemTransfer Transfer, TransferCapabilities Capabilities)> LoadReadableAsync(
        OemUnitOfWork uow, OemActor actor, ulong fileId, CancellationToken ct)
    {
        if ((await OemSettings.LoadAsync(uow.Db, ct)).ReconcileRequired)
            throw new ApiException(503, 50302, "系统正在进行存储核对，暂时不能读取文件");
        var file = await uow.Db.OemTransferFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fileId, ct) ?? throw ApiException.NotFound();
        var transfer = await uow.Db.OemTransfers.AsNoTracking().SingleAsync(item => item.Id == file.TransferId, ct);
        var capabilities = await OemTransferReader.CapabilitiesAsync(uow, actor, transfer, ct);
        if (!capabilities.View || file.PurgeReason == PurgeReasons.FileRemoved) throw ApiException.NotFound();
        if (!capabilities.CanReadContent) throw ApiException.Forbidden("无权读取该文件内容");
        if (file.ScanStatus != ValidationStatuses.Valid || file.PayloadStatus != PayloadStatuses.Available)
            throw new ApiException(409, 40910, "文件尚未通过文件校验或已不可用");
        if (file.PurgeDueAt is DateTime due && due <= uow.Now) throw new ApiException(410, 41003, "文件已到期或不可用");
        return (file, transfer, capabilities);
    }

    /// <summary>Rebuilds the caller behind a download grant from live data (account, vendor, login session).</summary>
    private static async Task<OemActor> LiveActorAsync(OemUnitOfWork uow, DownloadGrant grant, CancellationToken ct)
    {
        if (grant.Realm == OemRealms.Oem)
        {
            if (!await Identity.OemAuthService.HasActiveSessionAsync(uow.Db, grant.ActorId, grant.LoginSessionId, uow.Now, ct))
                throw ApiException.Unauthorized("登录状态已失效，请重新登录");
            var account = await uow.Db.OemAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == grant.ActorId, ct) ?? throw ApiException.Unauthorized();
            var actor = new OemAccountActor(account.Id, account.EmployeeNo, account.RealName, account.OemCompanyId, grant.LoginSessionId);
            return await OemAuthorizer.RecheckAsync(uow, actor, ct);
        }
        if (grant.Realm != OemRealms.Internal) throw ApiException.Unauthorized();
        var sessionActive = await uow.Db.RefreshTokens.AnyAsync(token => token.UserId == grant.ActorId && token.SessionId == grant.LoginSessionId
            && !token.Revoked && token.ExpiresAt > uow.Now && token.SessionExpiresAt > uow.Now, ct);
        if (!sessionActive) throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        var user = await uow.Db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == grant.ActorId, ct) ?? throw ApiException.Unauthorized();
        var internalActor = new InternalOemActor(new CurrentUser(user.Id, user.EmployeeNo, user.UserType, user.SupplierId, grant.LoginSessionId), grant.LoginSessionId);
        return await OemAuthorizer.RecheckAsync(uow, internalActor, ct);
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
}
