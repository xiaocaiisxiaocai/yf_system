using System.Data;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Outsource;

/// <summary>Wakes the mail worker right after a confirmation instead of waiting for the next poll.</summary>
public sealed class OutsourceMailSignal
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Wake()
    {
        if (signal.CurrentCount == 0) { try { signal.Release(); } catch (SemaphoreFullException) { } }
    }

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await signal.WaitAsync(timeout, ct);
}

/// <summary>Outcome of one SMTP transaction: the addresses the server refused (others were accepted).</summary>
public sealed record OutsourceTransportResult(IReadOnlyList<string> Rejected);

/// <summary>A refusal of the message itself (not a connection problem); retrying the same content will not help.</summary>
public sealed class OutsourcePermanentMailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends one prepared message. Connection, TLS, authentication and timeout failures are thrown as is (retryable).</summary>
public interface IOutsourceMailTransport
{
    Task<OutsourceTransportResult> SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct);
}

/// <summary>MailKit transport that keeps going when some recipients are refused, so the rest still get the mail.</summary>
public sealed class MailKitOutsourceTransport : IOutsourceMailTransport
{
    private sealed class TolerantSmtpClient : SmtpClient
    {
        public List<string> Rejected { get; } = [];

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
            Rejected.Add(mailbox.Address);
    }

    public async Task<OutsourceTransportResult> SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        using var client = new TolerantSmtpClient { Timeout = 60000 };
        await client.ConnectAsync(options.Host, options.Port, MailKitSmtpDelivery.SocketOptionsFor(options), ct);
        await client.AuthenticateAsync(options.Username, options.Password, ct);
        try
        {
            await client.SendAsync(message, ct);
        }
        catch (SmtpCommandException error) when (error.ErrorCode is SmtpErrorCode.RecipientNotAccepted)
        {
            // Every recipient was refused; MailKit reports that as a command error.
            client.Rejected.Add(error.Mailbox?.Address ?? "");
            return new(client.Rejected.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (SmtpCommandException error) when (error.StatusCode is >= (SmtpStatusCode)500 and < (SmtpStatusCode)600)
        {
            throw new OutsourcePermanentMailException("邮件被服务器拒收：" + error.Message, error);
        }
        try { await client.DisconnectAsync(true, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        return new(client.Rejected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

/// <summary>
/// Delivers queued outsourcing mails with the shared SMTP settings: one message per mail with every To and Cc together.
/// Refused addresses are skipped (PARTIAL); a mail whose primary recipients are all refused is FAILED. Connection-level
/// failures retry with growing delays (1, 2, 4 … 30 minutes) up to <see cref="MaximumAttempts"/>.
/// </summary>
public sealed class OutsourceMailWorker(
    IDbContextFactory<YfDbContext> dbFactory,
    SmtpSettingsService smtpSettings,
    IOutsourceMailTransport transport,
    OutsourceMailSignal signal,
    AppOptions options,
    AuditService audit,
    ILogger<OutsourceMailWorker> logger) : BackgroundService
{
    internal const int MaximumAttempts = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleSending = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Outsourcing mail delivery cycle failed; retrying."); }
            try { await signal.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Sends every due queued mail once; returns how many were attempted.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        // A worker that died mid-send leaves SENDING behind. Whether the vendor got the mail is unknown, so it is not sent
        // again by itself: it fails with that explanation and purchasing resends after checking.
        var stale = await context.OutsourceMails.AsNoTracking()
            .Where(x => x.Status == OutsourceMailStatuses.Sending && x.UpdatedAt < now - StaleSending)
            .Select(x => x.Id).ToArrayAsync(ct);
        foreach (var lostId in stale)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var locked = await context.OutsourceMails
                .FromSqlInterpolated($"SELECT * FROM outsource_mails WHERE id={lostId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
            if (locked is null || locked.Status != OutsourceMailStatuses.Sending || locked.UpdatedAt >= now - StaleSending)
            {
                await transaction.RollbackAsync(ct);
                continue;
            }
            var message = OutsourceMailKinds.IsElectricalHandoff(locked.Kind)
                ? "发送中断，不确定收件人是否已收到；请确认后再重新发送"
                : "发送中断，不确定厂商是否已收到；请确认后再重新发送";
            // Reopen items before updating outsource_mails so item.UpdatedAt <= mail.UpdatedAt holds for RetryAsync.
            if (locked.Kind == OutsourceMailKinds.Notice)
                await ReopenItemsAsync(context, locked.Id, locked.VendorId, ct);
            await context.OutsourceMails.Where(x => x.Id == locked.Id && x.Status == OutsourceMailStatuses.Sending)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OutsourceMailStatuses.Failed)
                    .SetProperty(x => x.LastError, message), ct);
            await transaction.CommitAsync(ct);
        }
        var due = await context.OutsourceMails.AsNoTracking()
            .Where(x => x.Status == OutsourceMailStatuses.Queued && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.Id).Select(x => x.Id).Take(20).ToArrayAsync(ct);
        if (due.Length == 0) return 0;
        var smtp = await smtpSettings.ResolveAsync(context.Database.Connection(), null, ct);
        var attempted = 0;
        foreach (var id in due)
        {
            if (!smtp.Configured)
            {
                await context.OutsourceMails.Where(x => x.Id == id && x.Status == OutsourceMailStatuses.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastError, "SMTP 尚未设定，设定后自动发送")
                        .SetProperty(x => x.NextAttemptAt, now.AddMinutes(1)), ct);
                continue;
            }
            var claimed = await context.OutsourceMails.Where(x => x.Id == id && x.Status == OutsourceMailStatuses.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OutsourceMailStatuses.Sending), ct);
            if (claimed == 0) continue;
            attempted++;
            await DeliverAsync(context, id, smtp.Options, ct);
        }
        return attempted;
    }

    private async Task DeliverAsync(YfDbContext context, ulong id, SmtpOptions smtp, CancellationToken ct)
    {
        var mail = await context.OutsourceMails.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var recipients = await context.OutsourceMailRecipients.AsNoTracking().Where(x => x.MailId == id).OrderBy(x => x.Id).ToArrayAsync(ct);
        string status;
        string? error = null;
        IReadOnlyList<string> rejected = [];
        DateTime? nextAttempt = null;
        try
        {
            var result = await transport.SendAsync(smtp, BuildMessage(smtp, mail, recipients), ct);
            rejected = result.Rejected;
            var rejectedSet = rejected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var primaryAccepted = recipients.Any(x => x.Kind == "TO" && !rejectedSet.Contains(x.Email));
            status = !primaryAccepted ? OutsourceMailStatuses.Failed
                : rejectedSet.Count > 0 ? OutsourceMailStatuses.Partial : OutsourceMailStatuses.Sent;
            if (!primaryAccepted)
            {
                error = OutsourceMailKinds.IsElectricalHandoff(mail.Kind) ? "内部收件地址全部被拒收" : "厂商收件地址全部被拒收";
                if (recipients.Any(x => x.Kind == "CC" && !rejectedSet.Contains(x.Email)))
                    error += "（抄送人已收到此邮件，重新发送会再抄送一次）";
            }
            else if (rejectedSet.Count > 0) error = "部分地址被拒收：" + string.Join("、", rejected);
        }
        catch (OutsourcePermanentMailException permanent)
        {
            status = OutsourceMailStatuses.Failed;
            error = Cut(permanent.Message);
        }
        catch (Exception transient) when (transient is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var attempts = mail.Attempts + 1;
            error = Cut(Describe(transient));
            if (attempts >= MaximumAttempts) status = OutsourceMailStatuses.Failed;
            else
            {
                status = OutsourceMailStatuses.Queued;
                nextAttempt = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, attempts - 1)));
            }
            logger.LogWarning(transient, "Outsourcing mail {MailId} attempt {Attempt} failed.", id, attempts);
        }
        var rejectedList = rejected.ToArray();
        if (rejectedList.Length > 0)
        {
            await context.OutsourceMailRecipients.Where(x => x.MailId == id && rejectedList.Contains(x.Email))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Rejected, true), CancellationToken.None);
        }
        // Confirming marked the items sent when the mail was queued; one that never reached the vendor must not stay "sent".
        if (status == OutsourceMailStatuses.Failed && mail.Kind == OutsourceMailKinds.Notice)
            await ReopenItemsAsync(context, id, mail.VendorId, CancellationToken.None);
        var delivered = status is OutsourceMailStatuses.Sent or OutsourceMailStatuses.Partial;
        await context.OutsourceMails.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, x => x.Attempts + 1)
            .SetProperty(x => x.LastError, error).SetProperty(x => x.NextAttemptAt, nextAttempt)
            .SetProperty(x => x.SentAt, x => delivered ? DateTime.UtcNow : x.SentAt), CancellationToken.None);
        if (status != OutsourceMailStatuses.Queued)
        {
            await audit.WriteAsync(context.Database.Connection(), null, null,
                delivered ? "OUTSOURCE_MAIL_SENT" : "OUTSOURCE_MAIL_FAILED", "outsource_mail", id, new
                {
                    targetName = mail.Subject, status, rejected = rejectedList, error,
                }, null, CancellationToken.None);
        }
    }

    /// <summary>Items of a failed notice go back to "有变更待通知" so the list shows the vendor still has to be told.</summary>
    internal static Task<int> ReopenItemsAsync(YfDbContext context, ulong mailId, ulong vendorId, CancellationToken ct) =>
        context.OutsourceItems.Where(item => item.SendStatus == OutsourceSendStatuses.Sent && item.NotifiedVendorId == vendorId
                && context.OutsourceMailItems.Any(link => link.MailId == mailId && link.ItemId == item.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SendStatus, OutsourceSendStatuses.PendingNotice), ct);

    internal static MimeMessage BuildMessage(SmtpOptions smtp, OutsourceMail mail, IReadOnlyList<OutsourceMailRecipient> recipients)
    {
        if (!MailboxAddress.TryParse(smtp.From, out var from) && !MailboxAddress.TryParse(smtp.Username, out from))
            throw new OutsourcePermanentMailException("SMTP 发件地址无效");
        var message = new MimeMessage { Subject = mail.Subject };
        message.From.Add(from);
        foreach (var recipient in recipients)
        {
            var address = new MailboxAddress(recipient.Name ?? "", recipient.Email);
            if (recipient.Kind == "TO") message.To.Add(address); else message.Cc.Add(address);
        }
        var body = new BodyBuilder { HtmlBody = mail.BodyHtml, TextBody = mail.BodyText };
        if (mail.Attachment is { } bytes && mail.AttachmentName is { } name)
            body.Attachments.Add(name, bytes, new ContentType("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        message.Body = body.ToMessageBody();
        return message;
    }

    private static string Describe(Exception error) => error switch
    {
        AuthenticationException => "SMTP 登录失败",
        SslHandshakeException => "SMTP 加密连接失败",
        SocketException or ServiceNotConnectedException => "无法连接 SMTP 服务器",
        TimeoutException or OperationCanceledException => "SMTP 连接超时",
        SmtpCommandException command => "SMTP 暂时拒绝：" + command.Message,
        _ => "发送失败：" + error.Message,
    };

    private static string Cut(string value) => value.Length <= 500 ? value : value[..500];
}
