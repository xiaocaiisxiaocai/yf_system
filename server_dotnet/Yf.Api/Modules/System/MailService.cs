using System.Text.Json;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

public sealed class MailService
{
    private readonly AppDb db;
    private readonly AppOptions options;
    private readonly AuditService audit;
    private readonly SystemService system;
    private readonly ILogger<MailService> logger;
    private readonly ISmtpDelivery smtp;

    public MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        SystemService system,
        ILogger<MailService> logger)
        : this(db, options, audit, system, logger, new MailKitSmtpDelivery())
    {
    }

    internal MailService(
        AppDb db,
        AppOptions options,
        AuditService audit,
        SystemService system,
        ILogger<MailService> logger,
        ISmtpDelivery smtp)
    {
        this.db = db;
        this.options = options;
        this.audit = audit;
        this.system = system;
        this.logger = logger;
        this.smtp = smtp;
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
        var cfg = options.Smtp;
        var configured = cfg.IsConfigured;
        var counts = (await conn.QueryAsync<QueueCount>(new CommandDefinition("SELECT status,COUNT(*) AS count FROM email_outbox GROUP BY status", cancellationToken: ct))).ToDictionary(x => x.Status, x => x.Count);
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

    public async Task EnqueueStorageWarningAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        if (!await EnabledAsync(conn, ct)) return;
        var storage = await system.StorageAsync(ct);
        if (!storage.Warning) return;
        var recipients = await conn.QueryAsync<Recipient>(new CommandDefinition("SELECT DISTINCT u.id,u.email,u.employee_no AS EmployeeNo,u.real_name AS RealName FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id WHERE u.status='ACTIVE' AND r.status='ACTIVE' AND r.is_built_in=1 AND r.name='系统管理员'", cancellationToken: ct));
        foreach (var user in recipients)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                await audit.WriteAsync(conn, null, null, "EMAIL_SKIPPED_MISSING_EMAIL", "user", user.Id, new { eventType = "STORAGE_WARNING", reason = "RECIPIENT_EMAIL_MISSING", user.EmployeeNo, user.RealName }, null, ct);
                continue;
            }
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO email_outbox(event_type,dedupe_key,recipient_user_id,recipient_email,subject,body,status,retry_count,created_at) VALUES('STORAGE_WARNING',@key,@Id,@Email,@subject,@body,'PENDING',0,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE dedupe_key=VALUES(dedupe_key)",
                new { key = $"storage-warning:{DateTime.UtcNow:yyyy-MM-dd}:{user.Id}", user.Id, user.Email, subject = $"[协作平台] 存储空间告警：已使用 {storage.UsedPercent:F1}%", body = $"存储目录：{storage.Root}\n所在卷：{storage.MountPoint}\n已使用：{storage.UsedPercent:F1}%\n告警阈值：{storage.WarnPercent:F1}%\n可用空间：{storage.AvailableBytes} 字节\n\n请及时扩容或按数据保留策略清理文件。" }, cancellationToken: ct));
        }
    }

    public async Task PurgeExpiredSessionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Keep rotated hashes beyond their original expiry so replay still revokes the family.
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM refresh_tokens WHERE expires_at < DATE_SUB(UTC_TIMESTAMP(), INTERVAL 7 DAY)", cancellationToken: ct));
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (!options.Smtp.IsConfigured) return;
        await using var conn = await db.OpenAsync(ct);
        if (!await EnabledAsync(conn, ct)) return;
        var pending = await conn.QueryAsync<MailRow>(new CommandDefinition("SELECT id,event_type AS EventType,recipient_email AS RecipientEmail,subject,body,status,retry_count AS RetryCount,next_attempt_at AS NextAttemptAt FROM email_outbox WHERE (status='PENDING' AND (next_attempt_at IS NULL OR next_attempt_at<=UTC_TIMESTAMP())) OR (status='SENDING' AND next_attempt_at<=UTC_TIMESTAMP()) ORDER BY id LIMIT 10", cancellationToken: ct));
        foreach (var mail in pending)
        {
            var lease = DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.AddMinutes(10).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            var claimed = await conn.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET status='SENDING',next_attempt_at=@lease WHERE id=@Id AND status=@Status AND retry_count=@RetryCount AND next_attempt_at <=> @NextAttemptAt AND (next_attempt_at IS NULL OR next_attempt_at<=UTC_TIMESTAMP())", new { mail.Id, mail.Status, mail.RetryCount, mail.NextAttemptAt, lease }, cancellationToken: ct));
            if (claimed != 1) continue;
            string status = "SENT";
            string? error = null;
            var retries = mail.RetryCount;
            DateTime? next = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var delivery = await smtp.SendAsync(options.Smtp,
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
                if (!terminal) next = DateTime.UtcNow.AddSeconds(30 * Math.Pow(2, Math.Clamp(retries - 1, 0, 6)));
            }
            // Once SendAsync has returned, the SMTP server accepted the message. Persist that
            // outcome during a short window independent of host shutdown; a QUIT failure must
            // not turn a known delivery into a retry and send a duplicate message.
            using var completion = status == "SENT" ? new CancellationTokenSource(TimeSpan.FromSeconds(15)) : null;
            var completionToken = completion?.Token ?? ct;
            await using var tx = await AppDb.BeginTransactionAsync(conn, completionToken);
            var changed = await conn.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET status=@status,retry_count=@retries,last_error=@error,next_attempt_at=@next,sent_at=IF(@status='SENT',UTC_TIMESTAMP(6),sent_at) WHERE id=@Id AND status='SENDING' AND next_attempt_at=@lease", new { mail.Id, status, retries, error, next, lease }, tx, cancellationToken: completionToken));
            if (changed == 1) await audit.WriteAsync(conn, tx, null, status == "SENT" ? "EMAIL_SENT" : status == "FAILED" ? "EMAIL_FAILED" : "EMAIL_RETRY", "email_outbox", mail.Id, new { eventType = mail.EventType, recipient = MaskEmail(mail.RecipientEmail), status, retryCount = retries, error }, null, completionToken);
            await tx.CommitAsync(completionToken);
        }
    }
    private sealed class QueueCount { public string Status { get; set; } = ""; public ulong Count { get; set; } }
    private sealed class Recipient { public ulong Id { get; set; } public string Email { get; set; } = ""; public string EmployeeNo { get; set; } = ""; public string RealName { get; set; } = ""; }
    private sealed class MailRow { public ulong Id { get; set; } public string EventType { get; set; } = ""; public string RecipientEmail { get; set; } = ""; public string Subject { get; set; } = ""; public string Body { get; set; } = ""; public string Status { get; set; } = ""; public int RetryCount { get; set; } public DateTime? NextAttemptAt { get; set; } }
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
        await client.ConnectAsync(options.Host, options.Port,
            options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
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
        var nextStorage = DateTime.MinValue;
        do
        {
            try
            {
                if (DateTime.UtcNow >= nextStorage)
                {
                    nextStorage = DateTime.UtcNow.AddMinutes(10);
                    await mail.PurgeExpiredSessionsAsync(stoppingToken);
                    await mail.EnqueueStorageWarningAsync(stoppingToken);
                }
                await mail.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Mail worker batch failed ({ErrorType}); queue retained for retry.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
