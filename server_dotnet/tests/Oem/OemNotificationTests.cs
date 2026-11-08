using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;
using Yf.Api.Modules.SystemManagement;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>OEM mail: queued per recipient in the business transaction, governed by OEM switches, re-validated at send time.</summary>
public sealed class OemNotificationTests
{
    private sealed record Mail(string EventType, string RecipientRealm, ulong RecipientAccountId, string RecipientEmail,
        string Status, string Subject, string Body, string? DedupeKey);
    private sealed record PolicyMail(ulong Id, string EventType, string DedupeKey, string RecipientEmail,
        string RecipientRealm, ulong AccountId, ulong TransferId);

    private static async Task<Mail[]> MailsAsync(OemTestHost host, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return (await conn.QueryAsync<Mail>("SELECT event_type AS EventType, recipient_realm AS RecipientRealm, recipient_account_id AS RecipientAccountId, " +
            "recipient_email AS RecipientEmail, status AS Status, subject AS Subject, body AS Body, dedupe_key AS DedupeKey " +
            "FROM email_outbox WHERE event_type LIKE 'OEM\\_%' ORDER BY id")).ToArray();
    }

    [Fact(Timeout = 240_000)]
    public async Task MissingVendorContactsAreAuditedWithoutFallingBackToSharedAccountEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await using (var connection = await host.OpenAsync(ct))
        {
            await connection.ExecuteAsync("DELETE FROM oem_company_contacts WHERE oem_company_id=@CompanyId; UPDATE oem_accounts SET email='legacy-account@example.invalid' WHERE oem_company_id=@CompanyId", new { world.CompanyId });
        }
        var draft = await CreateOutboundAsync(world, "无联络人邮箱", ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, "test.pdf", OemTestHost.Pdf("test"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var task = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok())["list"]!.AsArray().Single()!;
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM email_outbox WHERE event_type='OEM_TRANSFER_RELEASED'"));
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_EMAIL_SKIPPED_MISSING_EMAIL' AND target_id=@Id", new { Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.DoesNotContain(await MailsAsync(host, ct), mail => mail.RecipientEmail == "legacy-account@example.invalid");
    }

    [Fact(Timeout = 240_000)]
    public async Task ApproversAndEveryRecipientAreMailedIndividuallyWithoutFileNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await world.Admin.PostAsync($"/api/v1/oem/companies/{world.CompanyId}/accounts",
            new { employeeNo = "vendor_out2", password = "Vendor#2026" }, ct).Ok();
        await world.Admin.PostAsync($"/api/v1/oem/companies/{world.CompanyId}/contacts",
            new { name = "第二联系人", email = "second@vendor.invalid", phone = "13800000002" }, ct).Ok();

        // The real browser send flow historically derives the platform title from the
        // first file name. Mail must still use only the stable transfer number.
        const string sensitiveTitle = "secret-part-name.pdf";
        var draft = await CreateOutboundAsync(world, sensitiveTitle, ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, sensitiveTitle, OemTestHost.Pdf("x"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        Assert.Empty(await MailsAsync(host, ct));
        await host.RunOemJobsAsync(ct);

        var pending = Assert.Single(await MailsAsync(host, ct));
        Assert.Equal(OemMailEvents.ApprovalPending, pending.EventType);
        Assert.Equal(("internal", world.LeaderId), (pending.RecipientRealm, pending.RecipientAccountId));

        var task = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok())["list"]!.AsArray().Single()!;
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var mails = await MailsAsync(host, ct);
        var released = mails.Where(mail => mail.EventType == OemMailEvents.TransferReleased).ToArray();
        Assert.Equal(2, released.Length);
        Assert.All(released, mail => Assert.Equal("oem", mail.RecipientRealm));
        Assert.Equal(["second@vendor.invalid", "vendor_out@vendor.invalid"], released.Select(mail => mail.RecipientEmail).Order());
        Assert.DoesNotContain(mails, mail => mail.EventType == OemMailEvents.SenderReleased && mail.RecipientRealm == OemRealms.Oem);
        Assert.All(mails, mail =>
        {
            Assert.DoesNotContain(sensitiveTitle, mail.Subject);
            Assert.DoesNotContain(sensitiveTitle, mail.Body);
            Assert.Contains($"传递单 #{id}", mail.Subject);
            Assert.Contains($"传递单编号：#{id}", mail.Body);
            Assert.Contains($"发送方：内部账号 #{world.SenderId}", mail.Body);
        });
        Assert.All(released, mail => Assert.Contains($"/oem-portal/transfers/{id}", mail.Body));
        Assert.All(mails.Where(mail => mail.EventType == OemMailEvents.SenderReleased),
            mail => Assert.Contains($"/oem/transfers/{id}", mail.Body));

        // A queued contact address is a snapshot, not authority to send after the
        // live contact address changes.
        var policy = new OemOutboxPolicy(host.Service<AppOptions>());
        await using var conn = await host.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var vendorMail = await conn.QuerySingleAsync<PolicyMail>(
            "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
            "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
            "WHERE event_type='OEM_TRANSFER_RELEASED' ORDER BY id LIMIT 1", transaction: tx);
        Assert.True((await policy.EvaluateAsync(conn, tx, Info(vendorMail), ct)).Allowed);
        await conn.ExecuteAsync("""
            UPDATE oem_company_contacts SET email='changed@vendor.invalid'
            WHERE oem_company_id=@CompanyId AND email=@RecipientEmail;
            """, new { CompanyId = world.CompanyId, vendorMail.RecipientEmail }, tx);
        Assert.False((await policy.EvaluateAsync(conn, tx, Info(vendorMail), ct)).Allowed);
    }

    [Fact(Timeout = 240_000)]
    public async Task EveryOemNotificationUsesStableTransferNumberWithoutTheGeneratedOrEnteredTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await world.Admin.PutAsync("/api/v1/oem/notify-policies", new
        {
            items = new[]
            {
                new { key = OemSettingCatalog.NotifyEnabled, value = "true" },
                new { key = OemSettingCatalog.NotifyApprovalPending, value = "true" },
                new { key = OemSettingCatalog.NotifyTransferReleased, value = "true" },
                new { key = OemSettingCatalog.NotifySenderResult, value = "true" },
                new { key = OemSettingCatalog.NotifyReceipt, value = "true" },
            },
        }, ct).Ok();
        await host.CreateInternalUserAsync("mail_file_policy_admin", "FilePolicy#2026x",
            ["oem:file_policy_manage"], world.Org.Section, ct);

        const string sensitiveTitle = "客户机密-手填标题-secret-assembly.pdf";
        var draft = await CreateOutboundAsync(world, sensitiveTitle, ct);
        var transferId = TransferId(draft);
        var uploaded = await host.UploadAsync(world.Sender, transferId, sensitiveTitle, OemTestHost.Pdf("private"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        var handler = Assert.Single(host.Service<IEnumerable<IOemEventHandler>>().OfType<OemNotificationHandler>());
        await using (var uow = await OemUnitOfWork.BeginAsync(host.Service<IDbContextFactory<YfDbContext>>(), ct))
        {
            IOemEvent[] events =
            [
                new TransferReleasedEvent(transferId),
                new TransferClosedEvent(transferId, TransferLifecycle.Rejected, $"业务退回：{sensitiveTitle}\n旧说明"),
                new ApprovalBlockedEvent(transferId, $"审批人不可用：{sensitiveTitle}\n旧说明"),
                new DraftFileValidationFailedEvent(transferId, uploaded.Id(), ValidationStatuses.Invalid),
                new FileMissingEvent(transferId, uploaded.Id(), PayloadStatuses.StorageLost),
                new FirstReceiptEvent(transferId, uploaded.Id()),
                new FilePurgedEvent(transferId, uploaded.Id()),
                new FilePurgeFailingEvent(transferId, uploaded.Id(), 3),
            ];
            foreach (var domainEvent in events) await handler.HandleAsync(uow, domainEvent, ct);
            await uow.CommitAsync(ct);
        }

        var mails = await MailsAsync(host, ct);
        string[] eventTypes =
        [
            OemMailEvents.ApprovalPending,
            OemMailEvents.TransferReleased,
            OemMailEvents.SenderReleased,
            OemMailEvents.TransferClosed,
            OemMailEvents.ApprovalBlocked,
            OemMailEvents.ValidationFailed,
            OemMailEvents.FileMissing,
            OemMailEvents.FirstReceipt,
            OemMailEvents.FilePurged,
            OemMailEvents.PurgeFailing,
        ];
        foreach (var eventType in eventTypes)
            Assert.Contains(mails, mail => mail.EventType == eventType);
        Assert.All(mails, mail =>
        {
            Assert.DoesNotContain(sensitiveTitle, mail.Subject);
            Assert.DoesNotContain(sensitiveTitle, mail.Body);
            Assert.Contains($"传递单 #{transferId}", mail.Subject);
            Assert.Contains($"传递单编号：#{transferId}", mail.Body);
            Assert.Contains($"发送方：内部账号 #{world.SenderId}", mail.Body);
        });
    }

    [Fact(Timeout = 240_000)]
    public async Task PreUpgradeRetryIsRewrittenBeforeTheSmtpTransportReceivesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        const string sensitiveTitle = "legacy-secret-file-name.pdf";
        var draft = await CreateOutboundAsync(world, sensitiveTitle, ct);
        var transferId = TransferId(draft);
        await host.UploadAsync(world.Sender, transferId, sensitiveTitle, OemTestHost.Pdf("legacy"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        string queuedRecipient;
        string dedupeKey;
        await using (var connection = await host.OpenAsync(ct))
        {
            var queued = await connection.QuerySingleAsync<(string RecipientEmail, string DedupeKey)>(
                "SELECT recipient_email AS RecipientEmail,dedupe_key AS DedupeKey FROM email_outbox " +
                "WHERE event_type='OEM_APPROVAL_PENDING' AND oem_transfer_id=@transferId",
                new { transferId });
            queuedRecipient = queued.RecipientEmail;
            dedupeKey = queued.DedupeKey;
            await connection.ExecuteAsync("""
                UPDATE email_outbox
                SET subject=@subject,body=@body,status='PENDING',retry_count=1,next_attempt_at=NULL
                WHERE event_type='OEM_APPROVAL_PENDING' AND oem_transfer_id=@transferId
                """, new
            {
                transferId,
                subject = $"旧标题：{sensitiveTitle}\r\n旧姓名",
                body = $"旧姓名，您好：\n传递单：{sensitiveTitle}\n说明：请处理 {sensitiveTitle}\n旧链接",
            });
        }

        var options = host.Service<AppOptions>();
        options.Smtp = new()
        {
            Host = "smtp.example.invalid",
            Port = 587,
            Username = "sender@example.invalid",
            Password = "not-used",
            From = "sender@example.invalid",
            Security = "StartTls",
        };
        var delivery = new RecordingDelivery();
        var service = new MailService(
            new AppDb(options),
            options,
            host.Service<AuditService>(),
            NullLogger<MailService>.Instance,
            delivery,
            policies: [new OemOutboxPolicy(options)]);
        Assert.Equal(1, await service.FlushAsync(ct));

        var envelope = Assert.Single(delivery.Envelopes);
        Assert.Equal(queuedRecipient, envelope.Recipient);
        Assert.DoesNotContain(sensitiveTitle, envelope.Subject);
        Assert.DoesNotContain(sensitiveTitle, envelope.Body);
        Assert.DoesNotContain("旧姓名", envelope.Subject);
        Assert.DoesNotContain("旧姓名", envelope.Body);
        Assert.DoesNotContain("旧说明", envelope.Body);
        Assert.Contains($"传递单 #{transferId}", envelope.Subject);
        Assert.Contains($"传递单编号：#{transferId}", envelope.Body);
        Assert.Contains($"发送方：内部账号 #{world.SenderId}", envelope.Body);
        Assert.Contains($"/oem/transfers/{transferId}", envelope.Body);

        await using var verify = await host.OpenAsync(ct);
        var stored = await verify.QuerySingleAsync<(string Status, string Subject, string Body, string DedupeKey, int RetryCount)>(
            "SELECT status AS Status,subject AS Subject,body AS Body,dedupe_key AS DedupeKey,retry_count AS RetryCount " +
            "FROM email_outbox WHERE event_type='OEM_APPROVAL_PENDING' AND oem_transfer_id=@transferId",
            new { transferId });
        Assert.Equal("SENT", stored.Status);
        Assert.Equal(1, stored.RetryCount);
        Assert.Equal(dedupeKey, stored.DedupeKey);
        Assert.Equal(envelope.Subject, stored.Subject);
        Assert.Contains($"传递单编号：#{transferId}", stored.Body);
        Assert.DoesNotContain(sensitiveTitle, stored.Body);
    }

    [Fact(Timeout = 240_000)]
    public async Task OemSwitchesAreIndependentAndStaleMailIsCancelledAtSendTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);

        // The OEM "released" switch suppresses recipient mail only.
        await world.Admin.PutAsync("/api/v1/oem/notify-policies",
            new { items = new[] { new { key = "oem.notify.event.transfer_released", value = "false" } } }, ct).Ok();
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传" }, ct).Ok();
        await host.UploadAsync(world.Vendor, TransferId(inbound), "r.pdf", OemTestHost.Pdf("r"), ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(inbound)}/send", new { version = Version(inbound) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var mails = await MailsAsync(host, ct);
        Assert.DoesNotContain(mails, mail => mail.EventType == OemMailEvents.TransferReleased);
        Assert.Contains(mails, mail => mail.EventType == OemMailEvents.SenderReleased);

        // A pending-approval mail whose task was reassigned is stale at send time.
        var draft = await CreateOutboundAsync(world, "改派后过期", ct);
        await host.UploadAsync(world.Sender, TransferId(draft), "a.pdf", OemTestHost.Pdf("a"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var backup = await host.CreateInternalUserAsync("mail_backup", "Backup#2026x", ["oem:flow_approve"], world.Org.Section, ct);
        var detail = await world.Admin.GetAsync($"/api/v1/oem/transfers/{TransferId(draft)}", ct).Ok();
        var task = detail["approval"]!["nodes"]![0]!["tasks"]![0]!;
        await world.Admin.PostAsync($"/api/v1/oem/approvals/{task.Id()}/reassign",
            new { newApproverUserId = backup, reason = "出差", version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        var policy = new OemOutboxPolicy(host.Service<AppOptions>());
        await using (var conn = await host.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var rows = (await conn.QueryAsync<PolicyMail>(
                "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
                "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
                "WHERE event_type='OEM_APPROVAL_PENDING' ORDER BY id",
                transaction: tx)).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.False((await policy.EvaluateAsync(conn, tx, Info(rows[0]), ct)).Allowed);
            Assert.True((await policy.EvaluateAsync(conn, tx, Info(rows[1]), ct)).Allowed);
            await conn.ExecuteAsync("UPDATE users SET email='changed@example.invalid' WHERE id=@AccountId", rows[1], tx);
            Assert.False((await policy.EvaluateAsync(conn, tx, Info(rows[1]), ct)).Allowed);
        }

        // Turning collaboration mail off cancels only collaboration mail; OEM mail stays queued.
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("INSERT INTO email_outbox(event_type,recipient_email,subject,body,status,retry_count) VALUES('MESSAGE_CREATED','x@example.invalid','s','b','PENDING',0)");
            await conn.ExecuteAsync("UPDATE system_configs SET cfg_value='false' WHERE cfg_key='notify.enabled'");
        }
        var mailService = new MailService(new AppDb(host.Service<AppOptions>()), host.Service<AppOptions>(), host.Service<AuditService>(),
            NullLogger<MailService>.Instance, [policy]);
        await mailService.FlushAsync(ct);
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal("CANCELLED", await conn.ExecuteScalarAsync<string>("SELECT status FROM email_outbox WHERE event_type='MESSAGE_CREATED'"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM email_outbox WHERE event_type LIKE 'OEM\\_%' AND status='CANCELLED'"));
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task RecoveredApprovalBlockMailIsStaleAtSendTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var backup = await host.CreateInternalUserAsync("blocked_mail_backup", "Backup#2026x", ["oem:flow_approve"], world.Org.Section, ct);
        var draft = await CreateOutboundAsync(world, "阻断已恢复", ct);
        var transferId = TransferId(draft);
        await host.UploadAsync(world.Sender, transferId, "a.pdf", OemTestHost.Pdf("a"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        await world.Admin.PutAsync($"/api/v1/admin/users/{world.LeaderId}/status", new { status = "DISABLED" }, ct).Ok();
        Assert.Equal(1, await host.Service<OemApprovalService>().RevalidateActiveInstancesAsync(ct));
        var blocked = await world.Admin.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();
        var blockedTask = blocked["approval"]!["nodes"]![0]!["tasks"]!.AsArray().Single()!;
        await world.Admin.PostAsync($"/api/v1/oem/approvals/{blockedTask.Id()}/reassign",
            new { newApproverUserId = backup, reason = "恢复审批", version = blockedTask["version"]!.GetValue<ulong>() }, ct).Ok();

        var policy = new OemOutboxPolicy(host.Service<AppOptions>());
        await using var conn = await host.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<PolicyMail>(
            "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
            "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
            "WHERE event_type='OEM_APPROVAL_BLOCKED' AND oem_transfer_id=@transferId", new { transferId }, tx)).ToArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows) Assert.False((await policy.EvaluateAsync(conn, tx, Info(row), ct)).Allowed);
    }

    private static OutboxMailInfo Info(PolicyMail row) =>
        new(row.Id, row.EventType, row.DedupeKey, null, row.RecipientEmail,
            row.RecipientRealm, row.AccountId, row.TransferId);

    private sealed class RecordingDelivery : ISmtpDelivery
    {
        public List<SmtpEnvelope> Envelopes { get; } = [];

        public Task<SmtpDeliveryResult> SendAsync(SmtpOptions options, SmtpEnvelope envelope, CancellationToken ct)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(new SmtpDeliveryResult(null));
        }
    }
}
