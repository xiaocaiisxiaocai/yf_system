using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.SystemManagement;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>OEM mail: queued per recipient in the business transaction, governed by OEM switches, re-validated at send time.</summary>
public sealed class OemNotificationTests
{
    private sealed record Mail(string EventType, string RecipientRealm, ulong RecipientAccountId, string RecipientEmail, string Status, string Body, string? DedupeKey);
    private sealed record PolicyMail(ulong Id, string EventType, string DedupeKey, string RecipientEmail,
        string RecipientRealm, ulong AccountId, ulong TransferId);

    private static async Task<Mail[]> MailsAsync(OemTestHost host, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return (await conn.QueryAsync<Mail>("SELECT event_type AS EventType, recipient_realm AS RecipientRealm, recipient_account_id AS RecipientAccountId, " +
            "recipient_email AS RecipientEmail, status AS Status, body AS Body, dedupe_key AS DedupeKey FROM email_outbox WHERE event_type LIKE 'OEM\\_%' ORDER BY id")).ToArray();
    }

    [Fact(Timeout = 240_000)]
    public async Task ApproversAndEveryRecipientAreMailedIndividuallyWithoutFileNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await world.Admin.PostAsync($"/api/v1/oem/companies/{world.CompanyId}/accounts",
            new { employeeNo = "vendor_out2", realName = "第二人", email = "second@vendor.invalid", password = "Vendor#2026" }, ct).Ok();

        var draft = await CreateOutboundAsync(world, "机壳图纸", ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, "secret-part-name.pdf", OemTestHost.Pdf("x"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        Assert.Empty(await MailsAsync(host, ct));
        await host.RunOemJobsAsync(ct);

        var pending = Assert.Single(await MailsAsync(host, ct));
        Assert.Equal(OemMailEvents.ApprovalPending, pending.EventType);
        Assert.Equal(("internal", world.LeaderId), (pending.RecipientRealm, pending.RecipientAccountId));

        var task = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok()).AsArray().Single()!;
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        var mails = await MailsAsync(host, ct);
        var released = mails.Where(mail => mail.EventType == OemMailEvents.TransferReleased).ToArray();
        Assert.Equal(2, released.Length);
        Assert.All(released, mail => Assert.Equal("oem", mail.RecipientRealm));
        Assert.Equal(["second@vendor.invalid", "vendor_out@vendor.invalid"], released.Select(mail => mail.RecipientEmail).Order());
        Assert.Single(mails, mail => mail.EventType == OemMailEvents.SenderReleased && mail.RecipientAccountId == world.SenderId);
        Assert.All(mails, mail => Assert.DoesNotContain("secret-part-name", mail.Body));
        Assert.All(released, mail => Assert.Contains($"/oem-portal/transfers/{id}", mail.Body));
        Assert.Contains($"/oem/transfers/{id}", mails.Single(mail => mail.EventType == OemMailEvents.SenderReleased).Body);

        // A queued OEM-account address is a snapshot, not authority to send after the
        // live account address changes.
        var policy = new OemOutboxPolicy();
        await using var conn = await host.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var vendorMail = await conn.QuerySingleAsync<PolicyMail>(
            "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
            "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
            "WHERE event_type='OEM_TRANSFER_RELEASED' ORDER BY id LIMIT 1", transaction: tx);
        Assert.True((await policy.EvaluateAsync(conn, tx, Info(vendorMail), ct)).Allowed);
        await conn.ExecuteAsync("UPDATE oem_accounts SET email='changed@vendor.invalid' WHERE id=@AccountId", vendorMail, tx);
        Assert.False((await policy.EvaluateAsync(conn, tx, Info(vendorMail), ct)).Allowed);
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
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
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
        var policy = new OemOutboxPolicy();
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

        var policy = new OemOutboxPolicy();
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
}
