using System.Text.Json;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
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
    {
        var value = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT cfg_value FROM system_configs WHERE cfg_key='notify.enabled'", cancellationToken: ct));
        return value is null || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1";
    }

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var resolved = await settings.ResolveAsync(conn, null, ct);
        var cfg = resolved.Options;
        var configured = resolved.Configured;
        var counts = (await conn.QueryAsync<QueueCount>(new CommandDefinition("SELECT status,COUNT(*) AS count FROM email_outbox WHERE event_type <> 'STORAGE_WARNING' GROUP BY status", cancellationToken: ct))).ToDictionary(x => x.Status, x => x.Count);
        var missingCount = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM users WHERE status='ACTIVE' AND TRIM(email)=''", cancellationToken: ct));
        var missing = await conn.QueryAsync(new CommandDefinition("SELECT id AS userId,employee_no AS employeeNo,real_name AS realName,user_type AS userType,status FROM users WHERE status='ACTIVE' AND TRIM(email)='' ORDER BY employee_no LIMIT 20", cancellationToken: ct));
        var recent = await conn.QueryAsync<AuditRow>(new CommandDefinition("SELECT id,action,target_type AS TargetType,target_id AS TargetId,detail,created_at AS CreatedAt FROM audit_logs WHERE action IN ('EMAIL_SENT','EMAIL_FAILED','EMAIL_RETRY','EMAIL_SKIPPED_MISSING_EMAIL') ORDER BY created_at DESC,id DESC LIMIT 10", cancellationToken: ct));
        return new
        {
            configured, host = configured ? cfg.Host : null, port = configured ? (int?)cfg.Port : null, from = configured ? MaskEmail(cfg.From) : null,
            notificationsEnabled = await EnabledAsync(conn, ct),
            queue = new { pending = counts.GetValueOrDefault("PENDING"), sending = counts.GetValueOrDefault("SENDING"), sent = counts.GetValueOrDefault("SENT"), failed = counts.GetValueOrDefault("FAILED") },
            latestSentAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT MAX(sent_at) FROM email_outbox WHERE status='SENT'", cancellationToken: ct)),
            latestFailedAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT MAX(created_at) FROM audit_logs WHERE action='EMAIL_FAILED'", cancellationToken: ct)),
            missingEmailCount = missingCount, missingEmailAccounts = missing,
            recent = recent.Select(x => new { x.Id, x.Action, x.TargetType, x.TargetId, detail = SafeDetail(x.Detail), x.CreatedAt })
        };
    }

    private static object SafeDetail(string? detail)
    {
        var output = new Dictionary<string, object?>();
        if (detail is null) return output;
        using var parsed = JsonDocument.Parse(detail);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) return output;
        foreach (var property in parsed.RootElement.EnumerateObject())
        {
            if (new[] { "eventType", "status", "reason", "employeeNo", "realName", "retryCount", "userId" }.Contains(property.Name)) output[property.Name] = property.Value.Clone();
            if (property.Value.ValueKind == JsonValueKind.String && property.Name == "recipient") output[property.Name] = MaskEmail(property.Value.GetString()!);
            if (property.Value.ValueKind == JsonValueKind.String && property.Name == "error") output[property.Name] = SanitizeError(property.Value.GetString()!);
        }
        return output;
    }

    public async Task PurgeExpiredSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Keep rotated hashes beyond their original expiry so replay still revokes the family.
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM refresh_tokens WHERE expires_at < DATE_SUB(UTC_TIMESTAMP(), INTERVAL 7 DAY)", cancellationToken: ct));
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        ResolvedSmtpSettings resolved;
        MailRow[] pending;
        await using (var conn = await db.OpenAsync(ct))
        {
            if (!await EnabledAsync(conn, ct)) return;
            resolved = await settings.ResolveAsync(conn, null, ct);
            if (!resolved.Configured) return;
            pending = (await conn.QueryAsync<MailRow>(new CommandDefinition("SELECT id,event_type AS EventType,project_id AS ProjectId,dedupe_key AS DedupeKey,recipient_user_id AS RecipientUserId,recipient_email AS RecipientEmail,subject,body,status,retry_count AS RetryCount,next_attempt_at AS NextAttemptAt FROM email_outbox WHERE event_type <> 'STORAGE_WARNING' AND sent_at IS NULL AND ((status='PENDING' AND (next_attempt_at IS NULL OR next_attempt_at<=UTC_TIMESTAMP())) OR (status='SENDING' AND next_attempt_at<=UTC_TIMESTAMP())) ORDER BY id LIMIT 10", cancellationToken: ct))).ToArray();
        }
        foreach (var mail in pending)
        {
            DateTime lease;
            await using (var claimConnection = await db.OpenAsync(ct))
            {
                await using var claimTransaction = await AppDb.BeginTransactionAsync(claimConnection, ct);
                var claimed = await claimConnection.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET status='SENDING',next_attempt_at=UTC_TIMESTAMP()+INTERVAL 10 MINUTE WHERE id=@Id AND sent_at IS NULL AND status=@Status AND retry_count=@RetryCount AND next_attempt_at <=> @NextAttemptAt AND (next_attempt_at IS NULL OR next_attempt_at<=UTC_TIMESTAMP())", new { mail.Id, mail.Status, mail.RetryCount, mail.NextAttemptAt }, claimTransaction, cancellationToken: ct));
                if (claimed != 1) continue;
                lease = await claimConnection.QuerySingleAsync<DateTime>(new CommandDefinition(
                    "SELECT next_attempt_at FROM email_outbox WHERE id=@Id", new { mail.Id }, claimTransaction, cancellationToken: ct));
                var recipientAuthorized = mail.EventType == "PROJECT_SUBMITTED"
                    ? await IsCurrentPendingAcceptanceAsync(claimConnection, claimTransaction, mail, ct)
                    : mail.ProjectId is null || mail.RecipientUserId is null
                        ? mail.ProjectId is null && mail.RecipientUserId is null
                        : await ProjectNotificationService.IsCurrentProjectRecipientAsync(
                            claimConnection,
                            claimTransaction,
                            mail.ProjectId.Value,
                            mail.RecipientUserId.Value,
                            ct);
                if (!recipientAuthorized)
                {
                    var cancelled = await claimConnection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE email_outbox
                        SET status='CANCELLED',next_attempt_at=NULL,last_error=@Reason
                        WHERE id=@Id AND status='SENDING' AND next_attempt_at=@Lease
                        """,
                        new
                        {
                            mail.Id,
                            Lease = lease,
                            Reason = mail.EventType == "PROJECT_SUBMITTED"
                                ? ProjectNotificationService.SupersededAcceptanceMailReason
                                : ProjectNotificationService.StaleProjectMailReason,
                        },
                        claimTransaction,
                        cancellationToken: ct));
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
                                reason = mail.EventType == "PROJECT_SUBMITTED"
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
            var changed = await conn.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET status=@status,retry_count=@retries,last_error=@error,next_attempt_at=IF(@retryDelaySeconds IS NULL,NULL,TIMESTAMPADD(SECOND,@retryDelaySeconds,UTC_TIMESTAMP())),sent_at=IF(@status='SENT',UTC_TIMESTAMP(6),sent_at) WHERE id=@Id AND status='SENDING' AND next_attempt_at=@lease", new { mail.Id, status, retries, error, retryDelaySeconds, lease }, tx, cancellationToken: completionToken));
            if (changed == 1) await audit.WriteAsync(conn, tx, null, status == "SENT" ? "EMAIL_SENT" : status == "FAILED" ? "EMAIL_FAILED" : "EMAIL_RETRY", "email_outbox", mail.Id, new { eventType = mail.EventType, recipient = MaskEmail(mail.RecipientEmail), status, retryCount = retries, error }, null, completionToken);
            await tx.CommitAsync(completionToken);
        }
    }
    private sealed class QueueCount { public string Status { get; set; } = ""; public ulong Count { get; set; } }
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

    private sealed class MailRow
    {
        public ulong Id { get; set; }
        public string EventType { get; set; } = "";
        public ulong? ProjectId { get; set; }
        public string? DedupeKey { get; set; }
        public ulong? RecipientUserId { get; set; }
        public string RecipientEmail { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string Status { get; set; } = "";
        public int RetryCount { get; set; }
        public DateTime? NextAttemptAt { get; set; }
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
