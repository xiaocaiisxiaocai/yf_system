using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
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

        var draft = await CreateOutboundAsync(world, "机壳图纸", ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, "secret-part-name.pdf", OemTestHost.Pdf("x"), ct);
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
        Assert.All(mails, mail => Assert.DoesNotContain("secret-part-name", mail.Body));
        Assert.All(released, mail => Assert.Contains($"/oem-portal/transfers/{id}", mail.Body));
        Assert.All(mails.Where(mail => mail.EventType == OemMailEvents.SenderReleased),
            mail => Assert.Contains($"/oem/transfers/{id}", mail.Body));

        // A queued contact address is a snapshot, not authority to send after the
        // live contact address changes.
        var policy = new OemOutboxPolicy();
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
        await world.Vendor.VendorSendAsync(TransferId(inbound), Version(inbound) + 1, ct).Ok();
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

    [Fact(Timeout = 240_000)]
    public async Task InboundFilesMailOnlyThePeopleTheVendorChoseFromItsList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var receiver = await host.CreateInternalUserAsync("zp_receiver", "Receiver#2026x", ["oem:transfer_view", "oem:file_download"], world.Org.Section, ct);
        ulong viewer;
        await using (var conn = await host.OpenAsync(ct))
            viewer = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='zp_viewer'");
        var url = $"/api/v1/oem/companies/{world.CompanyId}/internal-recipients";

        async Task<(ulong Id, ulong Version)> DraftAsync(string title)
        {
            var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title }, ct).Ok();
            await host.UploadAsync(world.Vendor, TransferId(draft), "in.pdf", OemTestHost.Pdf(title), ct);
            return (TransferId(draft), Version(draft) + 1);
        }
        async Task<Mail[]> ReleasedMailsAsync(ulong transferId) => (await MailsAsync(host, ct))
            .Where(mail => mail.EventType == OemMailEvents.TransferReleased && mail.DedupeKey?.StartsWith($"oem:released:{transferId}:", StringComparison.Ordinal) == true)
            .ToArray();
        async Task<ulong[]> ChoicesAsync() => (await world.Vendor.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Ok()).AsArray()
            .Select(item => item!["userId"]!.GetValue<ulong>()).ToArray();
        static ulong[] Listed(JsonNode list, bool removed) => list.AsArray()
            .Where(item => item!["removed"]!.GetValue<bool>() == removed).Select(item => item!["userId"]!.GetValue<ulong>()).ToArray();

        // By default everyone who can view and download is on the vendor's list (the leader, view only, is not); the vendor sees names only.
        var list = await world.Admin.GetAsync(url, ct).Ok();
        Assert.Contains(receiver, Listed(list, false));
        Assert.Contains(viewer, Listed(list, false));
        Assert.DoesNotContain(world.LeaderId, Listed(list, false));
        var choices = (await world.Vendor.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Ok()).AsArray();
        Assert.Contains(receiver, choices.Select(item => item!["userId"]!.GetValue<ulong>()));
        Assert.All(choices, item => Assert.Null(item!["employeeNo"]));
        var listed = (await world.Admin.GetAsync("/api/v1/oem/companies", ct).Ok())["list"]!.AsArray().Single(item => item!.Id() == world.CompanyId)!;
        Assert.Equal(choices.Count, listed["internalRecipientCount"]!.GetValue<long>());
        await world.Admin.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Status(HttpStatusCode.Forbidden);
        await world.Vendor.GetAsync(url, ct).Status(HttpStatusCode.Forbidden);
        await world.Viewer.PutAsync($"/api/v1/oem/internal-recipients/{viewer}", new { removed = true }, ct).Status(HttpStatusCode.Forbidden);

        // Removing someone from all vendors takes them off every vendor's list at once.
        var all = await world.Admin.PutAsync($"/api/v1/oem/internal-recipients/{viewer}", new { removed = true }, ct).Ok();
        Assert.Contains(viewer, Listed(all, true));
        Assert.DoesNotContain(viewer, (await world.Admin.GetAsync(url, ct).Ok()).AsArray().Select(item => item!["userId"]!.GetValue<ulong>()));
        Assert.DoesNotContain(viewer, await ChoicesAsync());

        // The vendor must choose at least one person, and only from its list.
        var (second, secondVersion) = await DraftAsync("选择通知人");
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send", new { version = secondVersion }, ct).Status(HttpStatusCode.BadRequest);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send",
            new { version = secondVersion, recipientUserIds = new[] { viewer } }, ct).Status(HttpStatusCode.BadRequest);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send",
            new { version = secondVersion, recipientUserIds = new[] { receiver } }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var mail = Assert.Single(await ReleasedMailsAsync(second));
        Assert.Equal(("internal", receiver), (mail.RecipientRealm, mail.RecipientAccountId));

        // Company-sent transfers do not take a recipient choice.
        var outbound = await CreateOutboundAsync(world, "公司发出", ct);
        await host.UploadAsync(world.Sender, TransferId(outbound), "o.pdf", OemTestHost.Pdf("o"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(outbound)}/send",
            new { version = Version(outbound) + 1, recipientUserIds = new[] { receiver } }, ct).Status(HttpStatusCode.BadRequest);

        // Before delivery the choice is rechecked: a person since removed from this vendor's list is not mailed; restoring them allows it again.
        var policy = new OemOutboxPolicy();
        PolicyMail queued;
        await using (var connection = await host.OpenAsync(ct))
            queued = await connection.QuerySingleAsync<PolicyMail>(
                "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
                "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
                "WHERE event_type='OEM_TRANSFER_RELEASED' AND dedupe_key LIKE @Key", new { Key = $"oem:released:{second}:%" });
        async Task<bool> AllowedAsync()
        {
            await using var connection = await host.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            return (await policy.EvaluateAsync(connection, tx, Info(queued), ct)).Allowed;
        }
        Assert.True(await AllowedAsync());
        var company = await world.Admin.PutAsync($"{url}/{receiver}", new { removed = true }, ct).Ok();
        Assert.Contains(receiver, Listed(company, true));
        Assert.DoesNotContain(receiver, await ChoicesAsync());
        Assert.False(await AllowedAsync());
        await world.Admin.PutAsync($"{url}/{receiver}", new { removed = false }, ct).Ok();
        Assert.True(await AllowedAsync());

        // With everyone removed the vendor may send without choosing; nobody is mailed and the skip is audited.
        foreach (var userId in Listed(await world.Admin.GetAsync(url, ct).Ok(), false))
            await world.Admin.PutAsync($"{url}/{userId}", new { removed = true }, ct).Ok();
        Assert.Empty(await ChoicesAsync());
        var (third, thirdVersion) = await DraftAsync("无人可通知");
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{third}/send", new { version = thirdVersion }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        Assert.Empty(await ReleasedMailsAsync(third));
        await using (var conn = await host.OpenAsync(ct))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE action='OEM_EMAIL_SKIPPED_NO_INTERNAL_RECIPIENT' AND target_id=@Id",
                new { Id = third.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        // Newly created staff with view + download join every list automatically.
        var newcomer = await host.CreateInternalUserAsync("zp_newcomer", "Newcomer#2026x", ["oem:transfer_view", "oem:file_download"], world.Org.Section, ct);
        Assert.Equal([newcomer], await ChoicesAsync());
    }

    private static OutboxMailInfo Info(PolicyMail row) =>
        new(row.Id, row.EventType, row.DedupeKey, null, row.RecipientEmail,
            row.RecipientRealm, row.AccountId, row.TransferId);
}
