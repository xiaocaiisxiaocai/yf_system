using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.SystemManagement;

public sealed class MailService
{
    private readonly AppDb db;
    private readonly AuditService audit;
    private readonly ILogger<MailService> logger;
    private readonly ISmtpDelivery smtp;
    private readonly SmtpSettingsService settings;
    private readonly AppOptions options;

    public MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        ILogger<MailService> logger)
        : this(db, options, audit, logger, new MailKitSmtpDelivery())
    {
    }

    internal MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        ILogger<MailService> logger,
        ISmtpDelivery smtp)
    {
        this.db = db;
        this.audit = audit;
        this.logger = logger;
        this.smtp = smtp;
        this.options = options;
        settings = new(db, options, audit);
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

    public async Task<MailStatusResponse> StatusAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var resolved = await settings.ResolveAsync(conn, null, ct);
        var notificationPolicy = await EmailNotificationPolicy.LoadAsync(conn, null, ct);
        var cfg = resolved.Options;
        var configured = resolved.Configured;
        await using var context = EfDb.Use(conn);
        var counts = (await context.EmailOutbox
            .Where(mail => mail.EventType != "STORAGE_WARNING")
            .GroupBy(mail => mail.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() })
            .ToListAsync(ct))
            .ToDictionary(item => item.Status, item => checked((ulong)item.Count));
        var missingCount = checked((ulong)await context.Users.LongCountAsync(
            user => user.Status == AccountStatuses.Active && user.Email.Trim() == string.Empty, ct));
        var missing = await context.Users
            .Where(user => user.Status == AccountStatuses.Active && user.Email.Trim() == string.Empty)
            .OrderBy(user => user.EmployeeNo)
            .Take(20)
            .Select(user => new MissingEmailAccount(user.Id, user.EmployeeNo, user.RealName, user.UserType, user.Status))
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
        var latestSentAt = await context.EmailOutbox.Where(mail => mail.Status == "SENT")
            .Select(mail => mail.SentAt).MaxAsync(ct);
        var latestFailedAt = await context.AuditLogs.Where(log => log.Action == "EMAIL_FAILED")
            .Select(log => (DateTime?)log.CreatedAt).MaxAsync(ct);
        return new MailStatusResponse(
            configured,
            configured ? cfg.Host : null,
            configured ? cfg.Port : null,
            configured ? MaskEmail(cfg.From) : null,
            notificationPolicy.GlobalEnabled,
            notificationPolicy.ToResponse(),
            new MailQueueCounts(counts.GetValueOrDefault("PENDING"), counts.GetValueOrDefault("SENDING"),
                counts.GetValueOrDefault("SENT"), counts.GetValueOrDefault("FAILED"), counts.GetValueOrDefault("CANCELLED")),
            latestSentAt,
            latestFailedAt,
            missingCount,
            missing,
            recent.Select(x => new MailAuditEntry(x.Id, x.Action, x.TargetType, x.TargetId, SafeDetail(x.Detail), x.CreatedAt)).ToArray());
    }

    private static IReadOnlyDictionary<string, object?> SafeDetail(string? detail)
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

    internal const int PurgeBatchSize = 1000;

    /// <summary>
    /// Deletes finished (sent, failed or cancelled) outbox rows older than <see cref="AppOptions.MailRetentionDays"/>.
    /// Pending rows are never touched. Small id batches keep each delete short.
    /// </summary>
    public async Task<long> PurgeFinishedAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        DateTime cutoff;
        await using (var clock = EfDb.Use(conn))
            cutoff = (await DbClock.UtcNowAsync(clock, ct, 0)).AddDays(-options.MailRetentionDays);
        long deleted = 0;
        while (true)
        {
            await using var context = EfDb.Use(conn);
            var ids = await context.EmailOutbox
                .Where(mail => (mail.Status == "SENT" || mail.Status == "FAILED" || mail.Status == "CANCELLED")
                    && mail.CreatedAt < cutoff)
                .OrderBy(mail => mail.Id).Select(mail => mail.Id).Take(PurgeBatchSize).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            deleted += await context.EmailOutbox.Where(mail => Enumerable.Contains(ids, mail.Id)).ExecuteDeleteAsync(ct);
            if (ids.Length < PurgeBatchSize) break;
        }
        if (deleted > 0) logger.LogInformation("Purged {Count} finished outbox rows older than {Days} days.", deleted, options.MailRetentionDays);
        return deleted;
    }

    internal const int BatchSize = 10;

    /// <summary>Delivers up to <see cref="BatchSize"/> due messages and returns how many were picked up.</summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        ResolvedSmtpSettings resolved;
        EmailNotificationPolicy policy;
        MailRow[] pending;
        await using (var conn = await db.OpenAsync(ct))
        {
            policy = await EmailNotificationPolicy.LoadAsync(conn, null, ct);
            if (!policy.GlobalEnabled)
            {
                await CancelPolicyDisabledAsync(conn, ct);
                return 0;
            }
            resolved = await settings.ResolveAsync(conn, null, ct);
            if (!resolved.Configured) return 0;
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
                }).Take(BatchSize).ToArrayAsync(ct);
        }
        // One SMTP connection (connect + authenticate) serves the whole batch instead of every message.
        var batch = smtp.OpenBatch();
        try
        {
            foreach (var mail in pending)
                await DeliverAsync(batch, resolved, mail, ct);
        }
        finally
        {
            var closed = await batch.CloseAsync();
            if (closed.DisconnectFailureType is not null)
                logger.LogWarning("SMTP batch connection cleanup failed ({ErrorType}); accepted messages stay sent.",
                    closed.DisconnectFailureType);
        }
        return pending.Length;
    }

    private async Task DeliverAsync(ISmtpBatch batch, ResolvedSmtpSettings resolved, MailRow mail, CancellationToken ct)
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
                .SetProperty(item => item.DedupeKey,
                    item => item.EventType == "FILE_UPLOADED" ? null : item.DedupeKey)
                .SetProperty(item => item.NextAttemptAt, lease), ct);
            if (claimed != 1) return;
            var claimedMail = await claimContext.EmailOutbox.AsNoTracking()
                .Where(item => item.Id == mail.Id && item.Status == "SENDING" && item.NextAttemptAt == lease)
                .Select(item => new
                {
                    item.RecipientEmail,
                    item.Subject,
                    item.Body,
                })
                .SingleAsync(ct);
            // Enqueue and claim serialize on the outbox row. Reload after claiming so
            // a file appended immediately before this claim is included in this send.
            mail.RecipientEmail = claimedMail.RecipientEmail;
            mail.Subject = claimedMail.Subject;
            mail.Body = claimedMail.Body;
            var currentPolicy = await EmailNotificationPolicy.LoadAsync(claimConnection, claimTransaction, ct);
            var currentRecipient = mail.RecipientUserId is { } recipientId
                ? await claimContext.Users
                    .Where(user => user.Id == recipientId && user.Status == AccountStatuses.Active)
                    .Select(user => new CurrentRecipient { UserType = user.UserType, Email = user.Email })
                    .SingleOrDefaultAsync(ct)
                : null;
            var currentRecipientType = currentRecipient?.UserType ?? mail.RecipientUserType;
            var recipientAddressValid = mail.RecipientUserId is null
                ? MailboxAddress.TryParse(mail.RecipientEmail, out _)
                : currentRecipient is not null
                  && !string.IsNullOrWhiteSpace(currentRecipient.Email)
                  && string.Equals(currentRecipient.Email.Trim(), mail.RecipientEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                  && MailboxAddress.TryParse(currentRecipient.Email, out _);
            var notificationAllowed = currentPolicy.Allows(mail.EventType, currentRecipientType);
            var recipientAuthorized = notificationAllowed && recipientAddressValid && (mail.EventType == "PROJECT_SUBMITTED"
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
                    : !recipientAddressValid
                        ? "收件账号或地址已失效"
                    : mail.EventType == "PROJECT_SUBMITTED"
                        ? ProjectNotificationService.SupersededAcceptanceMailReason
                        : ProjectNotificationService.StaleProjectMailReason;
                var cancelled = await claimContext.EmailOutbox.Where(item =>
                        item.Id == mail.Id
                        && item.Status == "SENDING"
                        && item.NextAttemptAt == lease)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, "CANCELLED")
                        .SetProperty(item => item.DedupeKey,
                            item => item.EventType == "FILE_UPLOADED" ? null : item.DedupeKey)
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
                                : !recipientAddressValid
                                ? "RECIPIENT_ADDRESS_STALE"
                                : mail.EventType == "PROJECT_SUBMITTED"
                                ? "PROJECT_ACCEPTANCE_STALE"
                                : "PROJECT_RECIPIENT_UNAUTHORIZED",
                        },
                        null,
                        ct);
                }
                await claimTransaction.CommitAsync(ct);
                return;
            }
            await claimTransaction.CommitAsync(ct);
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
            var delivery = await batch.SendAsync(resolved.Options,
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
            logger.LogWarning(
                "SMTP delivery for outbox {OutboxId} failed ({ErrorType}, {SafeError}); sanitized stack: {SanitizedStack}",
                mail.Id, ex.GetType().Name, error, SanitizedExceptionTrace(ex));
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
        if (changed == 1) await audit.WriteAsync(conn, tx, null, status == "SENT" ? "EMAIL_SENT" : status == "FAILED" ? "EMAIL_FAILED" : "EMAIL_RETRY", "email_outbox", mail.Id, new { eventType = mail.EventType, recipient = MaskEmail(mail.RecipientEmail), status, retryCount = retries, error }, null, completionToken);
        await tx.CommitAsync(completionToken);
    }

    internal const int CancellationBatchSize = 1000;

    private async Task CancelPolicyDisabledAsync(MySqlConnection conn, CancellationToken ct)
    {
        while (true)
        {
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var select = conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = $"""
                SELECT id,event_type
                FROM email_outbox
                WHERE event_type<>'STORAGE_WARNING' AND sent_at IS NULL AND status='PENDING'
                ORDER BY id
                LIMIT {CancellationBatchSize}
                FOR UPDATE
                """;
            var rows = new List<DisabledMailRow>(CancellationBatchSize);
            await using (var reader = await select.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    rows.Add(new DisabledMailRow { Id = reader.GetUInt64(0), EventType = reader.GetString(1) });
            }
            if (rows.Count == 0)
            {
                await tx.CommitAsync(ct);
                break;
            }

            await using var itemContext = EfDb.Use(conn, tx);
            var ids = rows.Select(row => row.Id).ToArray();
            var changed = await itemContext.EmailOutbox.Where(mail =>
                    Enumerable.Contains(ids, mail.Id)
                    && mail.SentAt == null
                    && mail.Status == "PENDING")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(mail => mail.Status, "CANCELLED")
                    .SetProperty(mail => mail.DedupeKey,
                        mail => mail.EventType == "FILE_UPLOADED" ? null : mail.DedupeKey)
                    .SetProperty(mail => mail.NextAttemptAt, (DateTime?)null)
                    .SetProperty(mail => mail.LastError, EmailNotificationPolicy.DisabledReason), ct);
            if (changed != rows.Count)
                throw new InvalidOperationException("待发送邮件批量取消数量不一致");
            await audit.WriteBatchAsync(conn, tx, null,
                rows.Select(row => new AuditWrite(
                    "EMAIL_CANCELLED_STALE",
                    "email_outbox",
                    row.Id,
                    new { eventType = row.EventType, status = "CANCELLED", reason = EmailNotificationPolicy.DisabledAuditReason }))
                    .ToArray(),
                null,
                ct);
            await tx.CommitAsync(ct);
            if (rows.Count < CancellationBatchSize) break;
        }
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
        DbClock.UtcNowAsync(context, ct, 0);

    internal static string SanitizedExceptionTrace(Exception error)
    {
        var output = new StringBuilder();
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (output.Length > 0) output.Append(" <- ");
            output.Append(current.GetType().FullName);
            var frames = new StackTrace(current, false).GetFrames();
            if (frames is null) continue;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                output.Append(" at ")
                    .Append(method.DeclaringType?.FullName ?? "<unknown>")
                    .Append('.')
                    .Append(method.Name);
            }
        }
        return output.Length == 0 ? error.GetType().FullName ?? "Exception" : output.ToString();
    }

    internal static void LogWorkerFailure(ILogger logger, Exception error) =>
        logger.LogWarning(
            "Mail worker batch failed ({ErrorType}); queue retained for retry. Sanitized stack: {SanitizedStack}",
            error.GetType().Name,
            SanitizedExceptionTrace(error));

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
    }
    private sealed class CurrentRecipient
    {
        public string UserType { get; init; } = "";
        public string Email { get; init; } = "";
    }
}

internal sealed record SmtpEnvelope(string Recipient, string Subject, string Body);
internal sealed record SmtpDeliveryResult(string? DisconnectFailureType);

internal interface ISmtpDelivery
{
    Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct);

    /// <summary>A delivery scope for one flush. The default sends each message independently.</summary>
    ISmtpBatch OpenBatch() => new PassThroughSmtpBatch(this);
}

/// <summary>Messages sent through one batch may share a connection; a batch is used by a single flush only.</summary>
internal interface ISmtpBatch
{
    Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct);
    Task<SmtpDeliveryResult> CloseAsync();
}

internal sealed class PassThroughSmtpBatch(ISmtpDelivery delivery) : ISmtpBatch
{
    public Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct) =>
        delivery.SendAsync(options, envelope, ct);
    public Task<SmtpDeliveryResult> CloseAsync() => Task.FromResult(new SmtpDeliveryResult(null));
}

internal sealed class MailKitSmtpDelivery : ISmtpDelivery
{
    public async Task<SmtpDeliveryResult> SendAsync(
        SmtpOptions options,
        SmtpEnvelope envelope,
        CancellationToken ct)
    {
        var batch = new MailKitSmtpBatch();
        try { await batch.SendAsync(options, envelope, ct); }
        catch { await batch.CloseAsync(); throw; }
        return await batch.CloseAsync();
    }

    public ISmtpBatch OpenBatch() => new MailKitSmtpBatch();

    internal static MimeMessage BuildMessage(SmtpOptions options, SmtpEnvelope envelope)
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
        return message;
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

/// <summary>
/// Keeps one authenticated connection for the messages of a single flush. Any failure drops the
/// connection so the next message starts fresh; the connection is closed (QUIT) when the batch ends.
/// </summary>
internal sealed class MailKitSmtpBatch : ISmtpBatch
{
    private SmtpClient? client;
    private string? clientKey;

    public async Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
    {
        var message = MailKitSmtpDelivery.BuildMessage(options, envelope);
        var key = string.Join('\n', options.Host, options.Port, options.Security, options.Username, options.Password);
        try
        {
            if (client is null || clientKey != key || !client.IsConnected)
            {
                await CloseAsync();
                var fresh = new SmtpClient { Timeout = 60000 };
                try
                {
                    await fresh.ConnectAsync(options.Host, options.Port, options.Security switch
                    {
                        "SslOnConnect" => SecureSocketOptions.SslOnConnect,
                        "StartTls" => SecureSocketOptions.StartTls,
                        _ => options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
                    }, ct);
                    await fresh.AuthenticateAsync(options.Username, options.Password, ct);
                }
                catch
                {
                    fresh.Dispose();
                    throw;
                }
                client = fresh;
                clientKey = key;
            }
            await client.SendAsync(message, ct);
            return new(null);
        }
        catch
        {
            Drop();
            throw;
        }
    }

    public async Task<SmtpDeliveryResult> CloseAsync()
    {
        var current = client;
        client = null;
        clientKey = null;
        if (current is null) return new(null);
        try
        {
            return current.IsConnected
                ? await MailKitSmtpDelivery.CompleteAcceptedDeliveryAsync(token => current.DisconnectAsync(true, token))
                : new(null);
        }
        finally { current.Dispose(); }
    }

    private void Drop()
    {
        client?.Dispose();
        client = null;
        clientKey = null;
    }
}

public sealed class MailWorker(MailService mail, AppOptions options, ILogger<MailWorker> logger) : BackgroundService
{
    // Drain a backlog within one tick instead of 10 messages per 30 seconds, but bound the work
    // so one tick cannot run indefinitely.
    internal const int MaximumBatchesPerTick = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var nextPurge = DateTime.MinValue;
        do
        {
            try
            {
                for (var round = 0; round < MaximumBatchesPerTick; round++)
                    if (await mail.FlushAsync(stoppingToken) < MailService.BatchSize) break;
                if (DateTime.UtcNow >= nextPurge)
                {
                    nextPurge = DateTime.UtcNow.AddHours(1);
                    await mail.PurgeFinishedAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                MailService.LogWorkerFailure(logger, ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
