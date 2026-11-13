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

        // A pending-approval mail is stale at send time once the transfer is terminated, and one for an email that changed is not sent.
        var draft = await CreateOutboundAsync(world, "终止后过期", ct);
        await host.UploadAsync(world.Sender, TransferId(draft), "a.pdf", OemTestHost.Pdf("a"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var policy = new OemOutboxPolicy();
        const string pendingSql = "SELECT id, event_type AS EventType, dedupe_key AS DedupeKey, recipient_email AS RecipientEmail, recipient_realm AS RecipientRealm, " +
            "recipient_account_id AS AccountId, oem_transfer_id AS TransferId FROM email_outbox " +
            "WHERE event_type='OEM_APPROVAL_PENDING' AND oem_transfer_id=@transferId ORDER BY id";
        var pendingTransferId = TransferId(draft);
        await using (var conn = await host.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var row = Assert.Single(await conn.QueryAsync<PolicyMail>(pendingSql, new { transferId = pendingTransferId }, tx));
            Assert.True((await policy.EvaluateAsync(conn, tx, Info(row), ct)).Allowed);
            await conn.ExecuteAsync("UPDATE users SET email='changed@example.invalid' WHERE id=@AccountId", row, tx);
            Assert.False((await policy.EvaluateAsync(conn, tx, Info(row), ct)).Allowed);
        }
        var current = await world.Admin.GetAsync($"/api/v1/oem/transfers/{pendingTransferId}", ct).Ok();
        await world.Admin.PostAsync($"/api/v1/oem/transfers/{pendingTransferId}/cancel",
            new { reason = "终止", version = current["summary"]!["version"]!.GetValue<ulong>() }, ct).Ok();
        await using (var conn = await host.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var row = Assert.Single(await conn.QueryAsync<PolicyMail>(pendingSql, new { transferId = pendingTransferId }, tx));
            Assert.False((await policy.EvaluateAsync(conn, tx, Info(row), ct)).Allowed);
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
    public async Task TerminatedApprovalBlockMailIsStaleAtSendTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "阻断后终止", ct);
        var transferId = TransferId(draft);
        await host.UploadAsync(world.Sender, transferId, "a.pdf", OemTestHost.Pdf("a"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        await world.Admin.PutAsync($"/api/v1/admin/users/{world.LeaderId}/status", new { status = "DISABLED" }, ct).Ok();
        Assert.Equal(1, await host.Service<OemApprovalService>().RevalidateActiveInstancesAsync(ct));
        var blocked = await world.Admin.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();
        await world.Admin.PostAsync($"/api/v1/oem/transfers/{transferId}/cancel",
            new { reason = "审批人已停用，终止", version = blocked["summary"]!["version"]!.GetValue<ulong>() }, ct).Ok();

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
    public async Task InternalStaffWithoutEmailCanStillBeChosenAndReceiveInboundFilesJustWithoutMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var noMail = await host.CreateInternalUserAsync("zp_nomail", "NoMail#2026xx", ["oem:transfer_view", "oem:file_download"], world.Org.Section, ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE users SET email='' WHERE id=@noMail", new { noMail });

        // Listed for the vendor to choose even though no mail can reach them.
        var choices = (await world.Vendor.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Ok()).AsArray()
            .Select(item => item!["userId"]!.GetValue<ulong>()).ToArray();
        Assert.Contains(noMail, choices);

        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "无邮箱接收人" }, ct).Ok();
        var id = TransferId(draft);
        await host.UploadAsync(world.Vendor, id, "in.pdf", OemTestHost.Pdf("nm"), ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/send",
            new { version = Version(draft) + 1, recipientUserIds = new[] { noMail }, signerName = world.Vendor.Signer }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        // They receive (see and open) the transfer, but no release mail is queued for anyone.
        var client = await host.LoginInternalAsync("zp_nomail", "NoMail#2026xx", ct);
        var visible = (await client.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray().Select(item => item!.Id()).ToArray();
        Assert.Contains(id, visible);
        await client.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.DoesNotContain(await MailsAsync(host, ct), mail => mail.EventType == OemMailEvents.TransferReleased
            && mail.DedupeKey?.StartsWith($"oem:released:{id}:", StringComparison.Ordinal) == true);
    }

    [Fact(Timeout = 240_000)]
    public async Task InboundFilesMailOnlyThePeopleTheVendorChoseFromItsList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var receiver = await host.CreateInternalUserAsync("zp_receiver", "Receiver#2026x", ["oem:transfer_view", "oem:file_download"], world.Org.Section, ct);
        ulong viewer, admin;
        await using (var conn = await host.OpenAsync(ct))
        {
            viewer = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='zp_viewer'");
            admin = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM users WHERE employee_no='admin'");
        }
        const string url = "/api/v1/oem/internal-recipients";

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

        // By default everyone who can view and download is on the one list for every vendor (the leader, view only, is not); the vendor sees names only.
        var list = await world.Admin.GetAsync(url, ct).Ok();
        Assert.Contains(receiver, Listed(list, false));
        Assert.Contains(viewer, Listed(list, false));
        Assert.DoesNotContain(world.LeaderId, Listed(list, false));
        // System administrators see every transfer anyway and are never mailed about vendor files: not on the list at all.
        Assert.DoesNotContain(admin, Listed(list, false).Concat(Listed(list, true)));
        var choices = (await world.Vendor.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Ok()).AsArray();
        Assert.Contains(receiver, choices.Select(item => item!["userId"]!.GetValue<ulong>()));
        Assert.DoesNotContain(admin, choices.Select(item => item!["userId"]!.GetValue<ulong>()));
        Assert.All(choices, item => Assert.Null(item!["employeeNo"]));
        await world.Admin.GetAsync("/api/v1/oem/transfer-recipient-choices", ct).Status(HttpStatusCode.Forbidden);
        await world.Vendor.GetAsync(url, ct).Status(HttpStatusCode.Forbidden);
        await world.Viewer.PutAsync($"{url}/{viewer}", new { removed = true }, ct).Status(HttpStatusCode.Forbidden);

        // Removing someone takes them off the list, and so off every vendor's choices at once.
        var all = await world.Admin.PutAsync($"{url}/{viewer}", new { removed = true }, ct).Ok();
        Assert.Contains(viewer, Listed(all, true));
        Assert.DoesNotContain(viewer, Listed(await world.Admin.GetAsync(url, ct).Ok(), false));
        Assert.DoesNotContain(viewer, await ChoicesAsync());

        // Re-import: the chosen removed people go back on the list in one step; an empty choice is refused and others cannot do it.
        await world.Admin.PostAsync($"{url}/restore", new { userIds = Array.Empty<ulong>() }, ct).Status(HttpStatusCode.BadRequest);
        await world.Viewer.PostAsync($"{url}/restore", new { userIds = new[] { viewer } }, ct).Status(HttpStatusCode.Forbidden);
        var restored = await world.Admin.PostAsync($"{url}/restore", new { userIds = new[] { viewer } }, ct).Ok();
        Assert.Contains(viewer, Listed(restored, false));
        Assert.Contains(viewer, await ChoicesAsync());
        await world.Admin.PutAsync($"{url}/{viewer}", new { removed = true }, ct).Ok();
        Assert.DoesNotContain(viewer, await ChoicesAsync());

        // The vendor must choose at least one person, and only from its list.
        var (second, secondVersion) = await DraftAsync("选择通知人");
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send", new { version = secondVersion }, ct).Status(HttpStatusCode.BadRequest);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send",
            new { version = secondVersion, recipientUserIds = new[] { viewer } }, ct).Status(HttpStatusCode.BadRequest);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{second}/send",
            new { version = secondVersion, recipientUserIds = new[] { receiver }, signerName = world.Vendor.Signer }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var mail = Assert.Single(await ReleasedMailsAsync(second));
        Assert.Equal(("internal", receiver), (mail.RecipientRealm, mail.RecipientAccountId));

        // Company-sent transfers do not take a recipient choice.
        var outbound = await CreateOutboundAsync(world, "公司发出", ct);
        await host.UploadAsync(world.Sender, TransferId(outbound), "o.pdf", OemTestHost.Pdf("o"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(outbound)}/send",
            new { version = Version(outbound) + 1, recipientUserIds = new[] { receiver } }, ct).Status(HttpStatusCode.BadRequest);

        // Before delivery the choice is rechecked: a person since removed from the list is not mailed; restoring them allows it again.
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
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{third}/send", new { version = thirdVersion, signerName = world.Vendor.Signer }, ct).Ok();
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

    [Fact(Timeout = 240_000)]
    public async Task OnlyAdministratorsSeeOtherPeoplesTransfersAndVendorsSignInboundOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var receiver = await host.CreateInternalUserAsync("zp_receiver", "Receiver#2026x", ["oem:transfer_view", "oem:file_download"], world.Org.Section, ct);
        var receiverClient = await host.LoginInternalAsync("zp_receiver", "Receiver#2026x", ct);
        static ulong[] Ids(JsonNode page) => page["list"]!.AsArray().Select(item => item!.Id()).ToArray();

        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "签名回传" }, ct).Ok();
        var id = TransferId(draft);
        await host.UploadAsync(world.Vendor, id, "in.pdf", OemTestHost.Pdf("in"), ct);
        var version = Version(draft) + 1;
        var send = $"/api/v1/oem/transfers/{id}/send";

        // The shared vendor login must sign with a person's name (free text, not limited to registered contacts).
        await world.Vendor.PostAsync(send, new { version, recipientUserIds = new[] { receiver } }, ct).Status(HttpStatusCode.BadRequest);
        var sent = await world.Vendor.PostAsync(send, new { version, recipientUserIds = new[] { receiver }, signerName = $"  {world.Vendor.Signer}  " }, ct).Ok();
        Assert.Equal(world.Vendor.Signer, sent["summary"]!["signerName"]!.GetValue<string>());
        await host.RunOemJobsAsync(ct);

        // Inbound: the chosen person and administrators see it; other staff with view permission do not.
        Assert.Contains(id, Ids(await receiverClient.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Equal(world.Vendor.Signer, (await receiverClient.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok())["summary"]!["signerName"]!.GetValue<string>());
        Assert.DoesNotContain(id, Ids(await world.Viewer.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        await world.Viewer.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        Assert.Contains(id, Ids(await world.Admin.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Contains(id, Ids(await world.Admin.GetAsync($"/api/v1/oem/transfers?sender={Uri.EscapeDataString(world.Vendor.Signer!)}", ct).Ok()));

        // Outbound: another person's transfer is hidden from staff with view permission, visible to administrators.
        var outbound = await CreateOutboundAsync(world, "公司发出", ct);
        await host.UploadAsync(world.Sender, TransferId(outbound), "o.pdf", OemTestHost.Pdf("o"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(outbound)}/send", new { version = Version(outbound) + 1 }, ct).Ok();
        Assert.DoesNotContain(TransferId(outbound), Ids(await world.Viewer.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.DoesNotContain(TransferId(outbound), Ids(await receiverClient.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Contains(TransferId(outbound), Ids(await world.Admin.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Contains(TransferId(outbound), Ids(await world.Sender.GetAsync("/api/v1/oem/transfers", ct).Ok()));

        // A supervisor sees everything their organisation (and below) sent or was notified about; a leader elsewhere sees none of it.
        var leader = world.Leader;
        Assert.Contains(id, Ids(await leader.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Contains(TransferId(outbound), Ids(await leader.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        await leader.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        var otherOrg = await host.CreateOrgPathAsync("别的组织", ct);
        var otherLeaderId = await host.CreateInternalUserAsync("zp_leader2", "Leader2#2026x", ["oem:transfer_view", "oem:file_download"], otherOrg.Section, ct);
        await world.Admin.PutAsync($"/api/v1/admin/departments/{otherOrg.Section}/leader", new { leaderUserId = otherLeaderId }, ct).Ok();
        var otherLeader = await host.LoginInternalAsync("zp_leader2", "Leader2#2026x", ct);
        Assert.Empty(Ids(await otherLeader.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        await otherLeader.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        // A leader higher up sees the same through the organisation tree.
        var upperLeaderId = await host.CreateInternalUserAsync("zp_leader3", "Leader3#2026x", ["oem:transfer_view"], world.Org.Division, ct);
        await world.Admin.PutAsync($"/api/v1/admin/departments/{world.Org.Division}/leader", new { leaderUserId = upperLeaderId }, ct).Ok();
        var upperLeader = await host.LoginInternalAsync("zp_leader3", "Leader3#2026x", ct);
        Assert.Contains(id, Ids(await upperLeader.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        Assert.Contains(TransferId(outbound), Ids(await upperLeader.GetAsync("/api/v1/oem/transfers", ct).Ok()));

        // The signature is free text: any person's name works even when the vendor has no registered contact, but it stays required.
        var noContact = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "无联络人" }, ct).Ok();
        await host.UploadAsync(world.Vendor, TransferId(noContact), "n.pdf", OemTestHost.Pdf("n"), ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_company_contacts SET status='DISABLED' WHERE oem_company_id=@CompanyId", new { world.CompanyId });
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(noContact)}/send",
            new { version = Version(noContact) + 1, recipientUserIds = new[] { receiver } }, ct).Status(HttpStatusCode.BadRequest);
        var freeSigned = await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(noContact)}/send",
            new { version = Version(noContact) + 1, recipientUserIds = new[] { receiver }, signerName = " 临时同事 " }, ct).Ok();
        Assert.Equal("临时同事", freeSigned["summary"]!["signerName"]!.GetValue<string>());

        // Company-sent transfers take no signature.
        var second = await CreateOutboundAsync(world, "不签名", ct);
        await host.UploadAsync(world.Sender, TransferId(second), "s.pdf", OemTestHost.Pdf("s"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(second)}/send",
            new { version = Version(second) + 1, signerName = "张三" }, ct).Status(HttpStatusCode.BadRequest);
    }

    private static OutboxMailInfo Info(PolicyMail row) =>
        new(row.Id, row.EventType, row.DedupeKey, null, row.RecipientEmail,
            row.RecipientRealm, row.AccountId, row.TransferId);
}
