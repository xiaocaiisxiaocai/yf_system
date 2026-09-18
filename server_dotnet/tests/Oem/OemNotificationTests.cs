using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.SystemManagement;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>OEM mail: queued per recipient in the business transaction, governed by OEM switches, re-validated at send time.</summary>
public sealed class OemNotificationTests
{
    private sealed record Mail(string EventType, string RecipientRealm, ulong RecipientAccountId, string RecipientEmail, string Status, string Body, string? DedupeKey);

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
            var rows = (await conn.QueryAsync<(ulong Id, string EventType, string DedupeKey, ulong AccountId, ulong TransferId)>(
                "SELECT id, event_type, dedupe_key, recipient_account_id, oem_transfer_id FROM email_outbox WHERE event_type='OEM_APPROVAL_PENDING' ORDER BY id",
                transaction: tx)).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.False((await policy.EvaluateAsync(conn, tx, Info(rows[0]), ct)).Allowed);
            Assert.True((await policy.EvaluateAsync(conn, tx, Info(rows[1]), ct)).Allowed);
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

    private static OutboxMailInfo Info((ulong Id, string EventType, string DedupeKey, ulong AccountId, ulong TransferId) row) =>
        new(row.Id, row.EventType, row.DedupeKey, null, "internal", row.AccountId, row.TransferId);
}
