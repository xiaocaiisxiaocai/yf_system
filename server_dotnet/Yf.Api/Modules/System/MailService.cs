using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>Identity and routing facts of one queued mail, handed to an <see cref="IOutboxRecipientPolicy"/>.</summary>
public sealed record OutboxMailInfo(
    ulong Id, string EventType, string? DedupeKey, ulong? RecipientUserId, string? RecipientRealm, ulong? RecipientAccountId, ulong? OemTransferId);

public sealed record OutboxDecision(bool Allowed, string? Reason, string? AuditReason)
{
    public static readonly OutboxDecision Allow = new(true, null, null);
    public static OutboxDecision Cancel(string reason, string auditReason) => new(false, reason, auditReason);
}

/// <summary>
/// Lets another business line own the send-time rules for its own mail (identified
/// by an event-type prefix) while sharing this queue and SMTP worker. Mail it owns is
/// governed only by that line's switches and recipient checks, and its delivery audit
/// rows carry the line's action prefix so each line's audit view stays separate.
/// </summary>
public interface IOutboxRecipientPolicy
{
    string EventTypePrefix { get; }
    string AuditActionPrefix { get; }
    Task<OutboxDecision> EvaluateAsync(MySqlConnection conn, MySqlTransaction tx, OutboxMailInfo mail, CancellationToken ct);
}

public sealed class MailService
{
    private readonly AppDb db;
    private readonly AuditService audit;
    private readonly ILogger<MailService> logger;
    private readonly ISmtpDelivery smtp;
    private readonly SmtpSettingsService settings;
    private readonly IOutboxRecipientPolicy[] policies;

    public MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        ILogger<MailService> logger,
        IEnumerable<IOutboxRecipientPolicy> policies)
        : this(db, options, audit, logger, new MailKitSmtpDelivery(), policies)
    {
    }

    internal MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        ILogger<MailService> logger,
        ISmtpDelivery smtp,
        IEnumerable<IOutboxRecipientPolicy>? policies = null)
    {
        this.db = db;
        this.audit = audit;
        this.logger = logger;
        this.smtp = smtp;
        this.policies = policies?.ToArray() ?? [];
        settings = new(db, options, audit);
    }

    private IOutboxRecipientPolicy? PolicyFor(string eventType) =>
        policies.FirstOrDefault(policy => eventType.StartsWith(policy.EventTypePrefix, StringComparison.Ordinal));

    /// <summary>Delivery audit action for a mail: externally owned mail uses its line's prefix.</summary>
    private string AuditAction(string eventType, string action) => (PolicyFor(eventType)?.AuditActionPrefix ?? string.Empty) + action;

    private IQueryable<Infrastructure.Entities.EmailOutbox> CollaborationOnly(IQueryable<Infrastructure.Entities.EmailOutbox> query)
    {
        foreach (var policy in policies)
        {
            var prefix = policy.EventTypePrefix;
            query = query.Where(mail => !mail.EventType.StartsWith(prefix));
        }
        return query;
    }

    public static string MaskEmail(string address)
    {
        var text = address.Trim();
        var at = text.IndexOf('@');
        return at < 0 ? text.Length == 0 ? "未配置" : "***" : (at == 0 ? "*" : text[..1]) + "***" + text[at..];
    }

    public static string SanitizeError(string message)
    {
        if (new[] { "SMTP 认证失败", "SMTP 连接超时", "SMTP 连接失败", "SMTP 发送失败", "收件地址或发件地址无效" }.Contains(message)) return message;
        var text = message.ToLowerInvariant();
        if (text.Contains("地址") || text.Contains("address") || text.Contains("mailbox")) return "收件地址或发件地址无效";
        if (text.Contains("timeout") || text.Contains("timed out") || text.Contains("连接超时")) return "SMTP 连接超时";
        if (new[] { "auth", "credential", "login", "password", "认证失败" }.Any(text.Contains)) return "SMTP 认证失败";
        return new[] { "connect", "dns", "network", "tls" }.Any(text.Contains) ? "SMTP 连接失败" : "SMTP 发送失败";
    }

    public static async Task<bool> EnabledAsync(MySqlConnection conn, CancellationToken ct)
        => (await EmailNotificationPolicy.LoadAsync(conn, null, ct)).GlobalEnabled;

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var resolved = await settings.ResolveAsync(conn, null, ct);
        var notificationPolicy = await EmailNotificationPolicy.LoadAsync(conn, null, ct);
        var cfg = resolved.Options;
        var configured = resolved.Configured;
        await using var context = EfDb.Use(conn);
        var counts = (await CollaborationOnly(context.EmailOutbox)
            .Where(mail => mail.EventType != "STORAGE_WARNING")
            .GroupBy(mail => mail.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() })
            .ToListAsync(ct))
            .ToDictionary(item => item.Status, item => checked((ulong)item.Count));
        var missingCount = checked((ulong)await context.Users.LongCountAsync(
            user => user.Status == "ACTIVE" && user.Email.Trim() == string.Empty, ct));
        var missing = await context.Users
            .Where(user => user.Status == "ACTIVE" && user.Email.Trim() == string.Empty)
            .OrderBy(user => user.EmployeeNo)
            .Take(20)
            .Select(user => new
            {
                UserId = user.Id,
                user.EmployeeNo,
                user.RealName,
                user.UserType,
                user.Status,
            })
            .ToListAsync(ct);
        string[] auditActions =
        [
            "EMAIL_SENT", "EMAIL_FAILED", "EMAIL_RETRY", "EMAIL_SKIPPED_MISSING_EMAIL", "EMAIL_CANCELLED_STALE",
        ];
        var recent = await context.AuditLogs
            .Where(log => Enumerable.Contains(auditActions, log.Action))
            .OrderByDescending(log => log.CreatedAt).ThenByDescending(log => log.Id)
            .Take(10)
            .Select(log => new MailAuditRow
            {
                Id = log.Id,
                Action = log.Action,
                TargetType = log.TargetType,
                TargetId = log.TargetId,
                Detail = log.Detail,
                CreatedAt = log.CreatedAt,
            })
            .ToListAsync(ct);
        return new
        {
            configured, host = configured ? cfg.Host : null, port = configured ? (int?)cfg.Port : null, from = configured ? MaskEmail(cfg.From) : null,
            notificationsEnabled = notificationPolicy.GlobalEnabled,
            notificationPolicy = notificationPolicy.ToResponse(),
            queue = new { pending = counts.GetValueOrDefault("PENDING"), sending = counts.GetValueOrDefault("SENDING"), sent = counts.GetValueOrDefault("SENT"), failed = counts.GetValueOrDefault("FAILED"), cancelled = counts.GetValueOrDefault("CANCELLED") },
            latestSentAt = await CollaborationOnly(context.EmailOutbox).Where(mail => mail.Status == "SENT")
                .Select(mail => mail.SentAt).MaxAsync(ct),
            latestFailedAt = await context.AuditLogs.Where(log => log.Action == "EMAIL_FAILED")
                .Select(log => (DateTime?)log.CreatedAt).MaxAsync(ct),
            missingEmailCount = missingCount, missingEmailAccounts = missing,
            recent = recent.Select(x => new { x.Id, x.Action, x.TargetType, x.TargetId, detail = SafeDetail(x.Detail), x.CreatedAt })
        };
    }

    private static object SafeDetail(string? detail)
    {
        var output = new Dictionary<string, object?>();
        if (detail is null) return output;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(detail); }
        catch (JsonException) { return output; }
        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return output;
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                if (new[] { "eventType", "status", "reason", "employeeNo", "realName", "retryCount", "userId" }.Contains(property.Name)) output[property.Name] = property.Value.Clone();
                if (property.Value.ValueKind == JsonValueKind.String && property.Name == "recipient") output[property.Name] = MaskEmail(property.Value.GetString()!);
                if (property.Value.ValueKind == JsonValueKind.String && property.Name == "error") output[property.Name] = SanitizeError(property.Value.GetString()!);
            }
        }
        return output;
    }

    public async Task PurgeExpiredSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Keep rotated hashes beyond their original expiry so replay still revokes the family.
        await using var context = EfDb.Use(conn);
        var cutoff = (await DatabaseUtcNowAsync(context, ct)).AddDays(-7);
        await context.RefreshTokens.Where(token => token.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        ResolvedSmtpSettings resolved;
        EmailNotificationPolicy policy;
        MailRow[] pending;
        await using (var conn = await db.OpenAsync(ct))
        {
            policy = await EmailNotificationPolicy.LoadAsync(conn, null, ct);
            if (!policy.GlobalEnabled)
            {
                // Only collaboration mail is cancelled; other business lines keep their own switches.
                await CancelPolicyDisabledAsync(conn, ct);
                if (policies.Length == 0) return;
            }
            resolved = await settings.ResolveAsync(conn, null, ct);
            if (!resolved.Configured) return;
            await using var context = EfDb.Use(conn);
            var now = await DatabaseUtcNowAsync(context, ct);
            pending = await (
                from mail in context.EmailOutbox
                join recipient in context.Users on mail.RecipientUserId equals (ulong?)recipient.Id into recipients
                from recipient in recipients.DefaultIfEmpty()
                where mail.EventType != "STORAGE_WARNING"
                      && mail.SentAt == null
                      && ((mail.Status == "PENDING"
                           && (mail.NextAttemptAt == null || mail.NextAttemptAt <= now))
                          || (mail.Status == "SENDING" && mail.NextAttemptAt <= now))
                orderby mail.Id
                select new MailRow
                {
                    Id = mail.Id,
                    EventType = mail.EventType,
                    ProjectId = mail.ProjectId,
                    DedupeKey = mail.DedupeKey,
                    RecipientUserId = mail.RecipientUserId,
                    RecipientUserType = recipient == null ? null : recipient.UserType,
                    RecipientEmail = mail.RecipientEmail,
                    Subject = mail.Subject,
                    Body = mail.Body,
                    Status = mail.Status,
                    RetryCount = mail.RetryCount,
                    NextAttemptAt = mail.NextAttemptAt,
                    RecipientRealm = mail.RecipientRealm,
                    RecipientAccountId = mail.RecipientAccountId,
                    OemTransferId = mail.OemTransferId,
                }).Take(10).ToArrayAsync(ct);
        }
        foreach (var mail in pending)
        {
            DateTime lease;
            await using (var claimConnection = await db.OpenAsync(ct))
            {
                await using var claimTransaction = await AppDb.BeginTransactionAsync(claimConnection, ct);
                await using var claimContext = EfDb.Use(claimConnection, claimTransaction);
                var claimNow = await DatabaseUtcNowAsync(claimContext, ct);
                lease = claimNow.AddMinutes(10);
                var claimQuery = claimContext.EmailOutbox.Where(item =>
                    item.Id == mail.Id
                    && item.SentAt == null
                    && item.Status == mail.Status
                    && item.RetryCount == mail.RetryCount
                    && (mail.NextAttemptAt == null
                        ? item.NextAttemptAt == null
                        : item.NextAttemptAt == mail.NextAttemptAt)
                    && (item.NextAttemptAt == null || item.NextAttemptAt <= claimNow));
                var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, "SENDING")
                    .SetProperty(item => item.NextAttemptAt, lease), ct);
                if (claimed != 1) continue;
                var owner = PolicyFor(mail.EventType);
                if (owner is not null)
                {
                    if (!await AuthorizeExternalAsync(owner, mail, lease, claimConnection, claimTransaction, claimContext, ct))
                    {
                        await claimTransaction.CommitAsync(ct);
                        continue;
                    }
                    await claimTransaction.CommitAsync(ct);
                }
                else
                {
                    var currentPolicy = await EmailNotificationPolicy.LoadAsync(claimConnection, claimTransaction, ct);
                    var currentRecipientType = mail.RecipientUserId is { } recipientId
                        ? await claimContext.Users
                            .Where(user => user.Id == recipientId && user.Status == "ACTIVE")
                            .Select(user => user.UserType)
                            .SingleOrDefaultAsync(ct)
                        : mail.RecipientUserType;
                    var notificationAllowed = currentPolicy.Allows(mail.EventType, currentRecipientType);
                    var recipientAuthorized = notificationAllowed && (mail.EventType == "PROJECT_SUBMITTED"
                        ? await IsCurrentPendingAcceptanceAsync(claimConnection, claimTransaction, mail, ct)
                        : mail.ProjectId is null || mail.RecipientUserId is null
                            ? mail.ProjectId is null && mail.RecipientUserId is null
                            : await ProjectNotificationService.IsCurrentProjectRecipientAsync(
                                claimConnection,
                                claimTransaction,
                                mail.ProjectId.Value,
                                mail.RecipientUserId.Value,
                                ct));
                    if (!recipientAuthorized)
                    {
                        var policyDisabled = !notificationAllowed;
                        var reason = policyDisabled
                            ? EmailNotificationPolicy.DisabledReason
                            : mail.EventType == "PROJECT_SUBMITTED"
                                ? ProjectNotificationService.SupersededAcceptanceMailReason
                                : ProjectNotificationService.StaleProjectMailReason;
                        var cancelled = await claimContext.EmailOutbox.Where(item =>
                                item.Id == mail.Id
                                && item.Status == "SENDING"
                                && item.NextAttemptAt == lease)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(item => item.Status, "CANCELLED")
                                .SetProperty(item => item.NextAttemptAt, (DateTime?)null)
                                .SetProperty(item => item.LastError, reason), ct);
                        if (cancelled == 1)
                        {
                            await audit.WriteAsync(
                                claimConnection,
                                claimTransaction,
                                null,
                                "EMAIL_CANCELLED_STALE",
                                "email_outbox",
                                mail.Id,
                                new
                                {
                                    eventType = mail.EventType,
                                    status = "CANCELLED",
                                    reason = policyDisabled
                                        ? EmailNotificationPolicy.DisabledAuditReason
                                        : mail.EventType == "PROJECT_SUBMITTED"
                                        ? "PROJECT_ACCEPTANCE_STALE"
                                        : "PROJECT_RECIPIENT_UNAUTHORIZED",
                                },
                                null,
                                ct);
                        }
                        await claimTransaction.CommitAsync(ct);
                        continue;
                    }
                    await claimTransaction.CommitAsync(ct);
                }
            }
            // SMTP may take a minute. The durable lease protects this message;
            // no pooled database connection is needed while waiting on the network.
            string status = "SENT";
            string? error = null;
            var retries = mail.RetryCount;
            int? retryDelaySeconds = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var delivery = await smtp.SendAsync(resolved.Options,
                    new(mail.RecipientEmail, mail.Subject, mail.Body), timeout.Token);
                if (delivery.DisconnectFailureType is not null)
                    logger.LogWarning("SMTP message {OutboxId} was accepted, but connection cleanup failed ({ErrorType}).",
                        mail.Id, delivery.DisconnectFailureType);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                retries++;
                error = ex is OperationCanceledException ? "SMTP 连接超时" : SanitizeError(ex.Message);
                var terminal = retries >= 3 || ex is FormatException || ex is SmtpCommandException command && (int)command.StatusCode >= 500;
                status = terminal ? "FAILED" : "PENDING";
                if (!terminal) retryDelaySeconds = 30 * (1 << Math.Clamp(retries - 1, 0, 6));
            }
            // Once SendAsync has returned, the SMTP server accepted the message. Persist that
            // outcome during a short window independent of host shutdown; a QUIT failure must
            // not turn a known delivery into a retry and send a duplicate message.
            using var completion = status == "SENT" ? new CancellationTokenSource(TimeSpan.FromSeconds(15)) : null;
            var completionToken = completion?.Token ?? ct;
            await using var conn = await db.OpenAsync(completionToken);
            await using var tx = await AppDb.BeginTransactionAsync(conn, completionToken);
            await using var context = EfDb.Use(conn, tx);
            var completionNow = await DatabaseUtcNowAsync(context, completionToken);
            var nextAttemptAt = retryDelaySeconds is { } seconds ? completionNow.AddSeconds(seconds) : (DateTime?)null;
            var completionQuery = context.EmailOutbox.Where(item =>
                item.Id == mail.Id
                && item.Status == "SENDING"
                && item.NextAttemptAt == lease);
            var changed = status == "SENT"
                ? await completionQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, status)
                    .SetProperty(item => item.RetryCount, retries)
                    .SetProperty(item => item.LastError, error)
                    .SetProperty(item => item.NextAttemptAt, (DateTime?)null)
                    .SetProperty(item => item.SentAt, completionNow), completionToken)
                : await completionQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, status)
                    .SetProperty(item => item.RetryCount, retries)
                    .SetProperty(item => item.LastError, error)
                    .SetProperty(item => item.NextAttemptAt, nextAttemptAt), completionToken);
            if (changed == 1) await audit.WriteAsync(conn, tx, null, AuditAction(mail.EventType, status == "SENT" ? "EMAIL_SENT" : status == "FAILED" ? "EMAIL_FAILED" : "EMAIL_RETRY"), "email_outbox", mail.Id, new { eventType = mail.EventType, recipient = MaskEmail(mail.RecipientEmail), status, retryCount = retries, error }, null, completionToken);
            await tx.CommitAsync(completionToken);
        }
    }

    private async Task CancelPolicyDisabledAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var context = EfDb.Use(conn);
        var rows = await context.EmailOutbox
            .Where(mail => mail.EventType != "STORAGE_WARNING"
                           && mail.SentAt == null
                           && (mail.Status == "PENDING" || mail.Status == "FAILED"))
            .OrderBy(mail => mail.Id)
            .Select(mail => new DisabledMailRow { Id = mail.Id, EventType = mail.EventType })
            .ToListAsync(ct);
        foreach (var row in rows.Where(row => PolicyFor(row.EventType) is null))
        {
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var itemContext = EfDb.Use(conn, tx);
            var changed = await itemContext.EmailOutbox.Where(mail =>
                    mail.Id == row.Id
                    && mail.SentAt == null
                    && (mail.Status == "PENDING" || mail.Status == "FAILED"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(mail => mail.Status, "CANCELLED")
                    .SetProperty(mail => mail.NextAttemptAt, (DateTime?)null)
                    .SetProperty(mail => mail.LastError, EmailNotificationPolicy.DisabledReason), ct);
            if (changed == 1)
            {
                await audit.WriteAsync(conn, tx, null, "EMAIL_CANCELLED_STALE", "email_outbox", row.Id,
                    new { eventType = row.EventType, status = "CANCELLED", reason = EmailNotificationPolicy.DisabledAuditReason }, null, ct);
            }
            await tx.CommitAsync(ct);
        }
    }
    /// <summary>Send-time decision for mail owned by another business line; cancels (and audits) stale mail in place.</summary>
    private async Task<bool> AuthorizeExternalAsync(IOutboxRecipientPolicy owner, MailRow mail, DateTime lease,
        MySqlConnection conn, MySqlTransaction tx, YfDbContext context, CancellationToken ct)
    {
        var decision = await owner.EvaluateAsync(conn, tx, new OutboxMailInfo(
            mail.Id, mail.EventType, mail.DedupeKey, mail.RecipientUserId, mail.RecipientRealm, mail.RecipientAccountId, mail.OemTransferId), ct);
        if (decision.Allowed) return true;
        var cancelled = await context.EmailOutbox.Where(item => item.Id == mail.Id && item.Status == "SENDING" && item.NextAttemptAt == lease)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "CANCELLED")
                .SetProperty(item => item.NextAttemptAt, (DateTime?)null)
                .SetProperty(item => item.LastError, decision.Reason), ct);
        if (cancelled == 1)
            await audit.WriteAsync(conn, tx, null, owner.AuditActionPrefix + "EMAIL_CANCELLED_STALE", "email_outbox", mail.Id,
                new { eventType = mail.EventType, status = "CANCELLED", reason = decision.AuditReason }, null, ct);
        return false;
    }

    private sealed class DisabledMailRow { public ulong Id { get; init; } public string EventType { get; init; } = ""; }
    private static async Task<bool> IsCurrentPendingAcceptanceAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        MailRow mail,
        CancellationToken ct)
    {
        if (mail.ProjectId is null
            || mail.RecipientUserId is null
            || !ProjectNotificationService.TryParseAcceptanceDedupeKey(
                mail.DedupeKey,
                out var projectId,
                out var submissionId,
                out var recipientId)
            || projectId != mail.ProjectId.Value
            || recipientId != mail.RecipientUserId.Value)
        {
            return false;
        }
        return await ProjectNotificationService.IsCurrentPendingAcceptanceAsync(
            conn,
            tx,
            projectId,
            submissionId,
            recipientId,
            ct);
    }

    private static Task<DateTime> DatabaseUtcNowAsync(YfDbContext context, CancellationToken ct) =>
        context.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP() AS Value").SingleAsync(ct);

    private sealed class MailAuditRow
    {
        public ulong Id { get; init; }
        public string Action { get; init; } = string.Empty;
        public string? TargetType { get; init; }
        public string? TargetId { get; init; }
        public string? Detail { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class MailRow
    {
        public ulong Id { get; set; }
        public string EventType { get; set; } = "";
        public ulong? ProjectId { get; set; }
        public string? DedupeKey { get; set; }
        public ulong? RecipientUserId { get; set; }
        public string? RecipientUserType { get; set; }
        public string RecipientEmail { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string Status { get; set; } = "";
        public int RetryCount { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string? RecipientRealm { get; set; }
        public ulong? RecipientAccountId { get; set; }
        public ulong? OemTransferId { get; set; }
    }
}

internal sealed record SmtpEnvelope(string Recipient, string Subject, string Body);
internal sealed record SmtpDeliveryResult(string? DisconnectFailureType);

internal interface ISmtpDelivery
{
    Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct);
}

internal sealed class MailKitSmtpDelivery : ISmtpDelivery
{
    public async Task<SmtpDeliveryResult> SendAsync(
        SmtpOptions options,
        SmtpEnvelope envelope,
        CancellationToken ct)
    {
        if (!MailboxAddress.TryParse(options.From, out var from)
            && !MailboxAddress.TryParse(options.Username, out from))
            throw new FormatException("发件地址无效");
        if (!MailboxAddress.TryParse(envelope.Recipient, out var to))
            throw new FormatException("收件地址无效");

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = envelope.Subject;
        message.Body = new TextPart("plain") { Text = envelope.Body };

        using var client = new SmtpClient { Timeout = 60000 };
        await client.ConnectAsync(options.Host, options.Port, options.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "StartTls" => SecureSocketOptions.StartTls,
            _ => options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
        }, ct);
        await client.AuthenticateAsync(options.Username, options.Password, ct);
        await client.SendAsync(message, ct);

        return await CompleteAcceptedDeliveryAsync(
            cleanupToken => client.DisconnectAsync(true, cleanupToken));
    }

    internal static async Task<SmtpDeliveryResult> CompleteAcceptedDeliveryAsync(
        Func<CancellationToken, Task> disconnect)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await disconnect(cleanup.Token);
            return new(null);
        }
        catch (Exception error)
        {
            return new(error.GetType().Name);
        }
    }
}

public sealed class MailWorker(MailService mail, AppOptions options, ILogger<MailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var nextCleanup = DateTime.MinValue;
        do
        {
            try
            {
                if (DateTime.UtcNow >= nextCleanup)
                {
                    nextCleanup = DateTime.UtcNow.AddMinutes(10);
                    await mail.PurgeExpiredSessionsAsync(stoppingToken);
                }
                await mail.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Mail worker batch failed ({ErrorType}); queue retained for retry.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
