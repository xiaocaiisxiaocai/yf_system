using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class MailDeliveryTests
{
    [Fact(Timeout = 30_000)]
    public async Task SlowSmtpReleasesTheOnlyDatabaseConnectionAndKeepsItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        var delivery = new PausedDelivery();
        var audit = new AuditService(Array.Empty<IProjectAuditCapture>());
        var service = new MailService(scope.Database, scope.Options, audit,
            NullLogger<MailService>.Instance, delivery);
        var flush = service.FlushAsync(ct);
        try
        {
            await delivery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using (var connection = await scope.Database.OpenAsync(deadline.Token))
            {
                Assert.Equal("SENDING", await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT status FROM email_outbox WHERE id=1", cancellationToken: deadline.Token)));
                Assert.InRange(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),next_attempt_at) FROM email_outbox WHERE id=1",
                    cancellationToken: deadline.Token)), 590, 600);
            }
            // A second worker can access the same one-connection pool, but cannot
            // take or deliver the first worker's still-valid lease.
            await service.FlushAsync(deadline.Token);
            Assert.Equal(1, delivery.SendCount);
        }
        finally
        {
            delivery.Resume.TrySetResult();
            await flush;
        }
        await using var finalConnection = await scope.Database.OpenAsync(ct);
        Assert.Equal("SENT", await finalConnection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task AcceptedMessageRemainsSentWhenSmtpDisconnectFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delivery = new AcceptedThenDisconnectFailedDelivery(stopping.Cancel);
        var audit = new AuditService(Array.Empty<IProjectAuditCapture>());
        var service = new MailService(scope.Database, scope.Options, audit,
            NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(stopping.Token);
        await service.FlushAsync(ct);

        await using var connection = await scope.Database.OpenAsync(ct);
        var row = await connection.QuerySingleAsync<OutboxState>(new CommandDefinition(
            "SELECT status AS Status,retry_count AS RetryCount,sent_at AS SentAt FROM email_outbox WHERE id=1",
            cancellationToken: ct));
        Assert.Equal("SENT", row.Status);
        Assert.Equal(0, row.RetryCount);
        Assert.NotNull(row.SentAt);
        Assert.Equal(1, delivery.SendCount);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='EMAIL_SENT' AND target_id='1'",
            cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task SendFailureKeepsMessagePendingForRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        var delivery = new SendFailedDelivery();
        var audit = new AuditService(Array.Empty<IProjectAuditCapture>());
        var service = new MailService(scope.Database, scope.Options, audit,
            NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        await using var connection = await scope.Database.OpenAsync(ct);
        var row = await connection.QuerySingleAsync<OutboxState>(new CommandDefinition(
            "SELECT status AS Status,retry_count AS RetryCount,next_attempt_at AS NextAttemptAt,sent_at AS SentAt FROM email_outbox WHERE id=1",
            cancellationToken: ct));
        Assert.Equal("PENDING", row.Status);
        Assert.Equal(1, row.RetryCount);
        Assert.NotNull(row.NextAttemptAt);
        Assert.InRange(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),next_attempt_at) FROM email_outbox WHERE id=1",
            cancellationToken: ct)), 20, 30);
        Assert.Null(row.SentAt);
        Assert.Equal(1, delivery.SendCount);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='EMAIL_RETRY' AND target_id='1'",
            cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task ExistingWorkerReadsChangedDatabaseSettingsWithoutRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        scope.Options.Smtp.Host = "";
        var audit = new AuditService([]);
        var delivery = new CaptureSettingsDelivery();
        var settings = new SmtpSettingsService(scope.Database, scope.Options, audit);
        var service = new MailService(scope.Database, scope.Options, audit,
            NullLogger<MailService>.Instance, delivery);
        await service.FlushAsync(ct);
        Assert.Empty(delivery.Seen);
        for (var round = 0; round < 2; round++)
        {
            await using (var conn = await scope.Database.OpenAsync(ct))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Host = "smtp" + round + ".example.invalid", Port = round == 0 ? 465 : 587,
                    Username = "sender@example.invalid", From = "notice@example.invalid",
                    Security = round == 0 ? "SslOnConnect" : "StartTls", ProtectedPassword = settings.Protect("fixture-code-" + round)
                });
                await conn.ExecuteAsync(new CommandDefinition("INSERT INTO system_configs(cfg_key,cfg_value) VALUES('mail.smtp',@json) ON DUPLICATE KEY UPDATE cfg_value=@json", new { json }, cancellationToken: ct));
                if (round == 1) await conn.ExecuteAsync(new CommandDefinition("INSERT INTO email_outbox(event_type,recipient_email,subject,body,status,retry_count) VALUES('TEST_NOTIFICATION','recipient@example.invalid','second','body','PENDING',0)", cancellationToken: ct));
            }
            await service.FlushAsync(ct);
            Assert.Equal(round + 1, delivery.Seen.Count);
            Assert.Equal("smtp" + round + ".example.invalid", delivery.Seen[round].Host);
            Assert.Equal("fixture-code-" + round, delivery.Seen[round].Password);
        }
        Assert.Equal("StartTls", delivery.Seen[1].Security);
    }

    [Fact(Timeout = 30_000)]
    public async Task RetiredStorageWarningsAreNotDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
            await conn.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET event_type='STORAGE_WARNING' WHERE id=1", cancellationToken: ct));
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);
        await service.FlushAsync(ct);
        Assert.Empty(delivery.Seen);
        await using var check = await scope.Database.OpenAsync(ct);
        Assert.Equal("PENDING", await check.ExecuteScalarAsync<string>(new CommandDefinition("SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task LegacyProjectSubmissionWithoutVersionedRecipientIsCancelledBeforeSmtp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
            await conn.ExecuteAsync(new CommandDefinition("UPDATE email_outbox SET event_type='PROJECT_SUBMITTED' WHERE id=1", cancellationToken: ct));
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        Assert.Empty(delivery.Seen);
        await using (var check = await scope.Database.OpenAsync(ct))
        {
            Assert.Equal("CANCELLED", await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
            Assert.Equal(1, await check.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE action='EMAIL_CANCELLED_STALE' AND target_id='1'", cancellationToken: ct)));
        }
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(
            await service.StatusAsync(ct), JsonSerializerOptions.Web));
        Assert.Equal(1, status.RootElement.GetProperty("queue").GetProperty("cancelled").GetInt64());
        Assert.Contains(status.RootElement.GetProperty("recent").EnumerateArray(),
            row => row.GetProperty("action").GetString() == "EMAIL_CANCELLED_STALE");
    }

    [Fact(Timeout = 30_000)]
    public async Task QueuedKnownMailIsCancelledWhenItsEventRuleIsDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO system_configs(cfg_key,cfg_value) VALUES('notify.event.message_created','false')
                ON DUPLICATE KEY UPDATE cfg_value='false';
                UPDATE email_outbox SET event_type='MESSAGE_CREATED' WHERE id=1;
                """, cancellationToken: ct));
        }
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        Assert.Empty(delivery.Seen);
        await using var check = await scope.Database.OpenAsync(ct);
        Assert.Equal("CANCELLED", await check.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
        Assert.Equal(EmailNotificationPolicy.DisabledReason,
            await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT last_error FROM email_outbox WHERE id=1", cancellationToken: ct)));
        Assert.Contains("NOTIFICATION_POLICY_DISABLED",
            await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT detail FROM audit_logs WHERE action='EMAIL_CANCELLED_STALE' AND target_id='1'", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task QueuedMailIsCancelledWhenGlobalNotificationsAreDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO system_configs(cfg_key,cfg_value) VALUES('notify.enabled','false')
                ON DUPLICATE KEY UPDATE cfg_value='false';
                UPDATE email_outbox SET event_type='MESSAGE_CREATED' WHERE id=1;
                """, cancellationToken: ct));
        }
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        Assert.Empty(delivery.Seen);
        await using var check = await scope.Database.OpenAsync(ct);
        Assert.Equal("CANCELLED", await check.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
        Assert.Contains(EmailNotificationPolicy.DisabledAuditReason,
            await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT detail FROM audit_logs WHERE action='EMAIL_CANCELLED_STALE' AND target_id='1'", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task QueuedKnownMailIsCancelledWhenItsRecipientAudienceIsDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO system_configs(cfg_key,cfg_value) VALUES('notify.supplier.enabled','false')
                ON DUPLICATE KEY UPDATE cfg_value='false';
                INSERT INTO users(id,employee_no,real_name,email,user_type,supplier_id,status)
                VALUES(10,'supplier-10','供应商账号','recipient@example.invalid','SUPPLIER',100,'ACTIVE');
                UPDATE email_outbox SET event_type='MESSAGE_CREATED',recipient_user_id=10 WHERE id=1;
                """, cancellationToken: ct));
        }
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        Assert.Empty(delivery.Seen);
        await using var check = await scope.Database.OpenAsync(ct);
        Assert.Equal("CANCELLED", await check.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
        Assert.Equal(EmailNotificationPolicy.DisabledReason,
            await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT last_error FROM email_outbox WHERE id=1", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task QueuedProjectMailIsCancelledWhenRecipientLostProjectAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var conn = await scope.Database.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO permissions(id,code) VALUES(10,'project:list');
                INSERT INTO roles(id,status) VALUES(10,'ACTIVE');
                INSERT INTO role_permissions(role_id,permission_id) VALUES(10,10);
                INSERT INTO users(id,employee_no,user_type,supplier_id,status)
                VALUES(10,'old-owner','INTERNAL',NULL,'ACTIVE'),(11,'new-owner','INTERNAL',NULL,'ACTIVE');
                INSERT INTO user_roles(user_id,role_id) VALUES(10,10),(11,10);
                INSERT INTO projects(id,supplier_id,created_by,responsible_user_id,status,confirm_side)
                VALUES(7,100,10,11,'IN_PROGRESS',NULL);
                UPDATE email_outbox
                SET event_type='MESSAGE_CREATED',project_id=7,recipient_user_id=10
                WHERE id=1;
                """, cancellationToken: ct));
        }
        var delivery = new CaptureSettingsDelivery();
        var service = new MailService(scope.Database, scope.Options, new AuditService([]), NullLogger<MailService>.Instance, delivery);

        await service.FlushAsync(ct);

        Assert.Empty(delivery.Seen);
        await using var check = await scope.Database.OpenAsync(ct);
        Assert.Equal("CANCELLED", await check.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
        Assert.Equal(ProjectNotificationService.StaleProjectMailReason,
            await check.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT last_error FROM email_outbox WHERE id=1", cancellationToken: ct)));
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkflowCancellationDoesNotOverwriteAnInFlightSmtpLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDatabaseScope.CreateOrSkipAsync(ct);
        await using var conn = await scope.Database.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE email_outbox SET event_type='PROJECT_SUBMITTED',project_id=7,status='SENDING',next_attempt_at=UTC_TIMESTAMP()+INTERVAL 10 MINUTE WHERE id=1",
            cancellationToken: ct));
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);

        var changed = await ProjectNotificationService.CancelPendingAcceptanceAsync(
            conn,
            tx,
            7,
            ProjectNotificationService.SupersededAcceptanceMailReason,
            ct);
        await tx.CommitAsync(ct);

        Assert.Equal(0, changed);
        Assert.Equal("SENDING", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM email_outbox WHERE id=1", cancellationToken: ct)));
    }

    private sealed class CaptureSettingsDelivery : ISmtpDelivery
    {
        public List<SmtpOptions> Seen { get; } = [];
        public Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
        { Seen.Add(options); return Task.FromResult(new SmtpDeliveryResult(null)); }
    }

    private sealed class AcceptedThenDisconnectFailedDelivery(Action accepted) : ISmtpDelivery
    {
        public int SendCount { get; private set; }

        public async Task<SmtpDeliveryResult> SendAsync(
            SmtpOptions options,
            SmtpEnvelope envelope,
            CancellationToken ct)
        {
            SendCount++;
            accepted();
            return await MailKitSmtpDelivery.CompleteAcceptedDeliveryAsync(
                cleanupToken =>
                {
                    Assert.False(cleanupToken.IsCancellationRequested);
                    throw new IOException("simulated QUIT failure after DATA was accepted");
                });
        }
    }

    private sealed class SendFailedDelivery : ISmtpDelivery
    {
        public int SendCount { get; private set; }

        public Task<SmtpDeliveryResult> SendAsync(
            SmtpOptions options,
            SmtpEnvelope envelope,
            CancellationToken ct)
        {
            SendCount++;
            throw new IOException("simulated failure before SMTP accepted the message");
        }
    }

    private sealed class PausedDelivery : ISmtpDelivery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }
        public async Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
        {
            SendCount++;
            Started.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return new(null);
        }
    }

    private sealed class OutboxState
    {
        public string Status { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public DateTime? NextAttemptAt { get; init; }
        public DateTime? SentAt { get; init; }
    }

    private sealed class MailDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        public AppDb Database { get; } = database;
        public AppOptions Options { get; } = options;

        public static async Task<MailDatabaseScope> CreateOrSkipAsync(CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Mail delivery tests only allow local MySQL");

            var credentials = uri.UserInfo.Split(':', 2);
            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = "yf_test_dotnet_mail_" + Guid.NewGuid().ToString("N");
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var applicationOptions = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_mail_test_storage"),
                    WorkerEnabled = false,
                    Smtp = new SmtpOptions
                    {
                        Host = "smtp.example.invalid",
                        Port = 465,
                        Username = "sender@example.invalid",
                        Password = "test-only",
                        From = "sender@example.invalid"
                    }
                };
                var database = new AppDb(applicationOptions);
                await using var connection = await database.OpenAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition("""
                    CREATE TABLE system_configs(
                        cfg_key VARCHAR(100) PRIMARY KEY,
                        cfg_value TEXT NOT NULL
                    );
                    CREATE TABLE email_outbox(
                        id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                        event_type VARCHAR(32) NOT NULL,
                        project_id BIGINT UNSIGNED NULL,
                        dedupe_key VARCHAR(128) NULL,
                        recipient_user_id BIGINT UNSIGNED NULL,
                        recipient_email VARCHAR(128) NOT NULL,
                        subject VARCHAR(255) NOT NULL,
                        body TEXT NOT NULL,
                        status VARCHAR(16) NOT NULL,
                        retry_count INT NOT NULL,
                        next_attempt_at DATETIME(3) NULL,
                        last_error VARCHAR(1024) NULL,
                        sent_at DATETIME(6) NULL
                    );
                    CREATE TABLE audit_logs(
                        id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                        user_id BIGINT UNSIGNED NULL,
                        employee_no VARCHAR(64) NULL,
                        action VARCHAR(100) NOT NULL,
                        target_type VARCHAR(100) NULL,
                        target_id VARCHAR(100) NULL,
                        detail JSON NULL,
                        ip VARCHAR(64) NULL,
                        created_at DATETIME(6) NOT NULL
                    );
                    CREATE TABLE users(
                        id BIGINT UNSIGNED PRIMARY KEY,
                        employee_no VARCHAR(64) NOT NULL,
                        real_name VARCHAR(64) NOT NULL DEFAULT '',
                        email VARCHAR(128) NOT NULL DEFAULT '',
                        user_type VARCHAR(16) NOT NULL,
                        supplier_id BIGINT UNSIGNED NULL,
                        status VARCHAR(16) NOT NULL
                    );
                    CREATE TABLE permissions(
                        id BIGINT UNSIGNED PRIMARY KEY,
                        code VARCHAR(100) NOT NULL
                    );
                    CREATE TABLE roles(
                        id BIGINT UNSIGNED PRIMARY KEY,
                        status VARCHAR(16) NOT NULL
                    );
                    CREATE TABLE user_roles(
                        user_id BIGINT UNSIGNED NOT NULL,
                        role_id BIGINT UNSIGNED NOT NULL
                    );
                    CREATE TABLE role_permissions(
                        role_id BIGINT UNSIGNED NOT NULL,
                        permission_id BIGINT UNSIGNED NOT NULL
                    );
                    CREATE TABLE suppliers(
                        id BIGINT UNSIGNED PRIMARY KEY,
                        status VARCHAR(16) NOT NULL
                    );
                    CREATE TABLE projects(
                        id BIGINT UNSIGNED PRIMARY KEY,
                        project_group_id BIGINT UNSIGNED NULL,
                        supplier_id BIGINT UNSIGNED NOT NULL,
                        created_by BIGINT UNSIGNED NOT NULL,
                        responsible_user_id BIGINT UNSIGNED NULL,
                        status VARCHAR(32) NOT NULL,
                        confirm_side VARCHAR(16) NULL
                    );
                    INSERT INTO system_configs(cfg_key,cfg_value) VALUES('notify.enabled','true');
                    INSERT INTO email_outbox
                        (id,event_type,recipient_email,subject,body,status,retry_count,next_attempt_at,last_error,sent_at)
                    VALUES
                        (1,'TEST_NOTIFICATION','recipient@example.invalid','subject','body','PENDING',0,NULL,NULL,NULL);
                    """, cancellationToken: ct));
                return new(administration, databaseName, database, applicationOptions);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }
}
