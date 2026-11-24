using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests.Oem;

/// <summary>The outsourcing list in the ODM/OEMA vendor portal (外协清单契约 → 厂商查看).</summary>
public sealed class OemOutsourceViewTests
{
    [Fact(Timeout = 180_000)]
    public async Task VendorsReadRecordsForTheirOwnCompany()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendor = await host.CreateVendorAsync(admin, "外协代工甲", "os_vendor_a", ct);
        var other = await host.CreateVendorAsync(admin, "外协代工乙", "os_vendor_b", ct);
        var companyA = await host.CompanyIdOfAsync("os_vendor_a", ct);
        var companyB = await host.CompanyIdOfAsync("os_vendor_b", ct);
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("""
                INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at) VALUES('超穎电子股份有限公司','超穎',1,UTC_TIMESTAMP(3));
                INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,customer,machine_model,status,remark,closed_at,created_by) VALUES
                  ('A2',2,'2026-11-04',@a,'超穎电子股份有限公司','ST-201L','PAUSED','内部也可见的备注',NULL,1),
                  ('A1',1,'2026-11-03',@a,'其他客户','ST-100','NORMAL',NULL,NULL,1),
                  ('A3',3,'2026-11-05',@a,'其他客户','ST-300','NORMAL',NULL,UTC_TIMESTAMP(3),1),
                  ('B1',4,'2026-11-04',@b,'超穎电子股份有限公司','ST-201L','NORMAL',NULL,NULL,1);
                """, new { a = companyA, b = companyB });
        }

        // 是否有RobotV is internal by default; 备注 is hidden on the field settings page here.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='remark'");

        // Only the vendor's own open records, by serial number, with the columns shown to vendors.
        var page = await vendor.GetAsync("/api/v1/oem/outsource-items", ct).Ok();
        Assert.Equal(2UL, page["total"]!.GetValue<ulong>());
        var list = page["list"]!.AsArray();
        Assert.Equal(["A1", "A2"], list.Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        var a2 = list[1]!;
        Assert.Equal(("PAUSED", "超穎", "暂停", "外协代工甲"), (a2["status"]!.GetValue<string>(),
            a2["values"]!["customer"]!.GetValue<string>(), a2["values"]!["status"]!.GetValue<string>(), a2["values"]!["vendor"]!.GetValue<string>()));
        var columns = page["columns"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToArray();
        Assert.Equal(["serialNo", "outsourceDate", "status", "electricalHandoff", "workOrderNo", "files"], columns.Take(6));
        Assert.False(a2["electricalHandoff"]!.GetValue<bool>());
        Assert.Equal("否", a2["values"]!["electricalHandoff"]!.GetValue<string>());
        Assert.DoesNotContain("sendStatus", columns);
        Assert.DoesNotContain(columns, key => key is "importedBy" or "modifiedBy" or "hasRobotV" or "remark");
        Assert.Null(a2["values"]!["remark"]);

        // Keyword (work order, customer full or short name, model) and completed records on request.
        Assert.Equal(["A2"], (await vendor.GetAsync("/api/v1/oem/outsource-items?keyword=超穎", ct).Ok())["list"]!.AsArray()
            .Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        Assert.Equal(3UL, (await vendor.GetAsync("/api/v1/oem/outsource-items?includeClosed=true", ct).Ok())["total"]!.GetValue<ulong>());

        // The other vendor sees only its own record; internal staff use the outsourcing module, not this view.
        Assert.Equal(["B1"], (await other.GetAsync("/api/v1/oem/outsource-items", ct).Ok())["list"]!.AsArray()
            .Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        await admin.GetAsync("/api/v1/oem/outsource-items", ct).Status(HttpStatusCode.Forbidden);

        // Export: the same filter and visible columns as the portal, over all pages.
        using var export = await vendor.Http.GetAsync("/api/v1/oem/outsource-items/export?keyword=ST", ct);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        var sheet = await SheetAsync(export, ct);
        Assert.Contains(Cell("A1"), sheet, StringComparison.Ordinal);
        Assert.Contains(Cell("A2"), sheet, StringComparison.Ordinal);
        Assert.DoesNotContain(Cell("A3"), sheet, StringComparison.Ordinal);
        Assert.DoesNotContain(Cell("B1"), sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("内部也可见的备注", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("发送状态", sheet, StringComparison.Ordinal);
        Assert.Contains(Cell("超穎"), sheet, StringComparison.Ordinal);
        Assert.Contains(Cell("是否转迅得电控"), sheet, StringComparison.Ordinal);
        await admin.GetAsync("/api/v1/oem/outsource-items/export", ct).Status(HttpStatusCode.Forbidden);

        // A disabled vendor loses access at once.
        await admin.PutAsync($"/api/v1/oem/companies/{companyA}/status", new { status = "DISABLED" }, ct).Ok();
        await vendor.GetAsync("/api/v1/oem/outsource-items", ct).Status(HttpStatusCode.Unauthorized);
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorHandoffIsScopedAndVersionedAndQueuesOnlyItsOwnModuleMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendor = await host.CreateVendorAsync(admin, "外协电控厂商", "handoff_vendor", ct);
        var other = await host.CreateVendorAsync(admin, "另一家厂商", "handoff_other", ct);
        var company = await host.CompanyIdOfAsync("handoff_vendor", ct);
        var otherCompany = await host.CompanyIdOfAsync("handoff_other", ct);
        const string password = "HandoffCc#2026";
        var noticeUser = await host.CreateInternalUserAsync("notice_cc", password, [], null, ct);
        var handoffUser = await host.CreateInternalUserAsync("handoff_to", password, [], null, ct);
        var disabledUser = await host.CreateInternalUserAsync("handoff_disabled", password, [], null, ct);
        var blankEmailUser = await host.CreateInternalUserAsync("handoff_no_email", password, [], null, ct);
        const string ccPath = "/api/v1/outsource/settings/default-cc";
        await admin.PutAsync(ccPath, new
        {
            entries = new[] { new { userId = noticeUser, modules = new[] { "OUTSOURCE_NOTICE" } } }
                .Concat(new[] { handoffUser, disabledUser, blankEmailUser }
                    .Select(userId => new { userId, modules = new[] { "ELECTRICAL_HANDOFF" } })).ToArray(),
        }, ct).Ok();
        var typeId = (await admin.PostAsync("/api/v1/outsource/settings/types", new { code = "OEMA", name = "进料+制造", sortNo = 1 }, ct).Ok()).Id();
        await admin.PutAsync($"/api/v1/outsource/settings/types/{typeId}/vendors", new { vendorIds = new[] { company } }, ct).Ok();

        ulong id, closedId, oemAccountId;
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("""
                UPDATE users SET status='DISABLED' WHERE id=@disabledUser;
                UPDATE users SET email='' WHERE id=@blankEmailUser;
                INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at) VALUES
                  (@company,'outsourcing@vendor.invalid',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),(@otherCompany,'other@vendor.invalid',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at)
                  VALUES('客户电控全称','客户简称',1,UTC_TIMESTAMP(3));
                INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,customer,machine_model,type_code,status,
                  closed_at,created_by,modified_by,modified_at) VALUES
                  ('HANDOFF-1',1,'2026-11-04',@company,'客户电控全称','ST-201L','OEMA','PAUSED',NULL,1,1,'2026-11-07 02:03:04'),
                  ('HANDOFF-CLOSED',2,'2026-11-04',@company,NULL,NULL,'OEMA','NORMAL',UTC_TIMESTAMP(3),1,NULL,NULL),
                  ('HANDOFF-OTHER',3,'2026-11-04',@otherCompany,NULL,NULL,'OEMA','NORMAL',NULL,1,NULL,NULL);
                """, new { company, otherCompany, disabledUser, blankEmailUser });
            id = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM outsource_items WHERE work_order_no='HANDOFF-1'");
            closedId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM outsource_items WHERE work_order_no='HANDOFF-CLOSED'");
            oemAccountId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM oem_accounts WHERE employee_no='handoff_vendor'");
        }

        var path = $"/api/v1/oem/outsource-items/{id}/electrical-handoff";
        var closedPath = $"/api/v1/oem/outsource-items/{closedId}/electrical-handoff";
        var handoff = new { expected = false, handoff = true, signerName = "  转交<签名>  " };
        await admin.PostAsync(path, handoff, ct).Status(HttpStatusCode.Forbidden);
        await other.PostAsync(path, handoff, ct).Status(HttpStatusCode.NotFound);
        var closed = await vendor.PostAsync(closedPath, handoff, ct).Status(HttpStatusCode.BadRequest);
        Assert.Equal("已完成的记录不能操作", closed.Json!["message"]!.GetValue<string>());
        var closedWithdraw = await vendor.PostAsync(closedPath, new { expected = true, handoff = false, signerName = "已结案" }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("已完成的记录不能操作", closedWithdraw.Json!["message"]!.GetValue<string>());
        await vendor.PostAsync(path, new { expected = false, handoff = true }, ct).Status(HttpStatusCode.BadRequest);
        await vendor.PostAsync(path, handoff with { signerName = "   " }, ct).Status(HttpStatusCode.BadRequest);
        await vendor.PostAsync(path, handoff with { signerName = new string('字', 33) }, ct).Status(HttpStatusCode.BadRequest);
        await vendor.PostAsync(path, new { handoff = true, signerName = "必须提供期望值" }, ct).Status(HttpStatusCode.BadRequest);
        await vendor.PostAsync(path, new { expected = false, signerName = "必须提供目标值" }, ct).Status(HttpStatusCode.BadRequest);

        const string mailsPath = "/api/v1/outsource/mails";
        var itemIds = new[] { id };
        var preview = await admin.PostAsync(mailsPath + "/preview", new { itemIds }, ct).Ok();
        var originalNoticeId = (await admin.PostAsync(mailsPath + "/send",
            new { itemIds, fingerprint = preview["fingerprint"]!.GetValue<string>(), requestKey = "handoff-initial-notice" }, ct)
            .Ok())["mails"]![0]!.Id();
        string? snapshot;
        await using (var conn = await host.OpenAsync(ct))
            snapshot = await conn.ExecuteScalarAsync<string>("SELECT last_sent_snapshot FROM outsource_items WHERE id=@id", new { id });
        Assert.NotNull(snapshot);

        // Two identical requests against one expected value can persist and queue only one change.
        var attempted = await Task.WhenAll(vendor.PostAsync(path, handoff, ct), vendor.PostAsync(path, handoff, ct));
        var stale = Assert.Single(attempted, x => x.Status == HttpStatusCode.Conflict);
        Assert.Equal(40903, stale.Code);
        var turned = Assert.Single(attempted, x => x.Status == HttpStatusCode.OK).Expect(HttpStatusCode.OK);
        Assert.Equal(id, turned.Id());
        Assert.Equal("PAUSED", turned["status"]!.GetValue<string>());
        Assert.True(turned["electricalHandoff"]!.GetValue<bool>());
        Assert.Equal("是", turned["values"]!["electricalHandoff"]!.GetValue<string>());

        const string itemsPath = "/api/v1/outsource/items";
        var row = (await admin.GetAsync(itemsPath + "?keyword=HANDOFF-1", ct).Ok())["list"]![0]!;
        Assert.Equal("SENT", row["sendStatus"]!.GetValue<string>());
        Assert.True(row["electricalHandoff"]!.GetValue<bool>());
        Assert.Equal("转交<签名>", row["electricalHandoffSigner"]!.GetValue<string>());
        var turnedAt = row["electricalHandoffAt"]!.GetValue<DateTime>();
        Assert.DoesNotContain(row["changedFields"]?.AsArray().Select(x => x!.GetValue<string>()) ?? [], x => x == "electricalHandoff");
        var edited = await admin.PutAsync(itemsPath + "/" + id, new
        {
            vendorId = company, status = "PAUSED", expectedUpdatedAt = row["updatedAt"]!.GetValue<DateTime>(), electricalHandoff = false,
        }, ct).Ok();
        Assert.True(edited["electricalHandoff"]!.GetValue<bool>());
        await admin.PutAsync(itemsPath + "/batch", new
        {
            items = new[] { new { id, expectedUpdatedAt = edited["updatedAt"]!.GetValue<DateTime>() } },
            values = new { electricalHandoff = "false" },
        }, ct).Status(HttpStatusCode.BadRequest);

        using (var export = await admin.Http.GetAsync(itemsPath + "/export?keyword=HANDOFF-1", ct))
        {
            var sheet = await SheetAsync(export, ct);
            Assert.Contains(Cell("是否转迅得电控"), sheet, StringComparison.Ordinal);
            Assert.Contains(Cell("是"), sheet, StringComparison.Ordinal);
        }

        var mail = Assert.Single((await admin.GetAsync(mailsPath, ct).Ok())["list"]!.AsArray(), x =>
            x!["kind"]!.GetValue<string>() == "ELECTRICAL_HANDOFF")!;
        var handoffMailId = mail.Id();
        Assert.Equal("QUEUED", mail["status"]!.GetValue<string>());
        Assert.Equal("【转迅得电控】外协电控厂商 工令 HANDOFF-1", mail["subject"]!.GetValue<string>());
        Assert.Equal("外协电控厂商（签名：转交<签名>）", mail["createdByName"]!.GetValue<string>());
        Assert.Equal(["HANDOFF-1"], mail["workOrderNos"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(mail["attachmentName"]);
        Assert.Equal([("TO", "handoff_to@example.invalid", "MODULE"), ("CC", "outsourcing@vendor.invalid", "VENDOR")],
            mail["recipients"]!.AsArray().Select(x =>
                (x!["kind"]!.GetValue<string>(), x["email"]!.GetValue<string>(), x["source"]!.GetValue<string>())));
        await admin.GetAsync($"{mailsPath}/{handoffMailId}/attachment", ct).Status(HttpStatusCode.NotFound);

        await using (var conn = await host.OpenAsync(ct))
        {
            var stored = await conn.QuerySingleAsync<(ulong ModifiedBy, DateTime ModifiedAt, string Snapshot)>(
                "SELECT modified_by,modified_at,last_sent_snapshot FROM outsource_items WHERE id=@id", new { id });
            Assert.Equal((1UL, new DateTime(2026, 11, 7, 2, 3, 4), snapshot), stored);
            var author = await conn.QuerySingleAsync<(ulong? Internal, ulong? Oem)>(
                "SELECT created_by,created_by_oem_account_id FROM outsource_mails WHERE id=@handoffMailId", new { handoffMailId });
            Assert.Equal(((ulong?)null, (ulong?)oemAccountId), author);
        }

        // The mail and sent snapshot still exclude the portal-visible field while its value is yes.
        var unchangedPreview = await admin.PostAsync(mailsPath + "/preview", new { itemIds }, ct).Ok();
        Assert.DoesNotContain("是否转迅得电控", unchangedPreview["mails"]![0]!["htmlBody"]!.GetValue<string>(), StringComparison.Ordinal);
        var noticeId = (await admin.PostAsync(mailsPath + "/send", new
        {
            itemIds, fingerprint = unchangedPreview["fingerprint"]!.GetValue<string>(), requestKey = "handoff-second-notice",
        }, ct).Ok())["mails"]![0]!.Id();
        using (var attachment = await admin.Http.GetAsync($"{mailsPath}/{noticeId}/attachment", ct))
        {
            var sheet = await SheetAsync(attachment, ct);
            Assert.DoesNotContain("是否转迅得电控", sheet, StringComparison.Ordinal);
        }

        var transport = new CaptureTransport { RefusedAddress = "handoff_to@example.invalid" };
        var smtp = new AppOptions
        {
            Smtp = new() { Host = "smtp.example.invalid", Port = 465, Username = "u", Password = "p", From = "noreply@example.invalid" },
        };
        var audit = host.Service<AuditService>();
        var worker = new OutsourceMailWorker(host.Service<IDbContextFactory<YfDbContext>>(),
            new SmtpSettingsService(host.Service<AppDb>(), smtp, audit), transport, host.Service<OutsourceMailSignal>(), smtp, audit,
            NullLogger<OutsourceMailWorker>.Instance);
        Assert.Equal(3, await worker.RunOnceAsync(ct));
        var failed = Assert.Single((await admin.GetAsync(mailsPath + "?status=FAILED", ct).Ok())["list"]!.AsArray())!;
        Assert.Equal(handoffMailId, failed.Id());
        await using (var conn = await host.OpenAsync(ct))
        {
            var sent = await conn.ExecuteScalarAsync<string>("SELECT last_sent_snapshot FROM outsource_items WHERE id=@id", new { id });
            Assert.DoesNotContain("electricalHandoff", sent, StringComparison.Ordinal);
            Assert.Equal("SENT", await conn.ExecuteScalarAsync<string>("SELECT send_status FROM outsource_items WHERE id=@id", new { id }));
            await conn.ExecuteAsync("UPDATE outsource_items SET status='CANCELLED' WHERE id=@id", new { id });
        }
        // Requeue the failed handoff with its original contents and recipients.
        var retried = await admin.PostAsync($"{mailsPath}/{handoffMailId}/retry", null, ct).Ok();
        Assert.Equal("QUEUED", retried["status"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(mail["recipients"], retried["recipients"]));
        Assert.True(JsonNode.DeepEquals(mail["subject"], retried["subject"]));

        var withdrawn = await vendor.PostAsync(path, new { expected = true, handoff = false, signerName = "撤回签名" }, ct).Ok();
        Assert.Equal("CANCELLED", withdrawn["status"]!.GetValue<string>());
        Assert.False(withdrawn["electricalHandoff"]!.GetValue<bool>());
        Assert.Equal("否", withdrawn["values"]!["electricalHandoff"]!.GetValue<string>());
        var withdrawnRow = (await admin.GetAsync(itemsPath + "?keyword=HANDOFF-1", ct).Ok())["list"]![0]!;
        Assert.Equal("SENT", withdrawnRow["sendStatus"]!.GetValue<string>());
        Assert.Equal("撤回签名", withdrawnRow["electricalHandoffSigner"]!.GetValue<string>());
        Assert.True(withdrawnRow["electricalHandoffAt"]!.GetValue<DateTime>() >= turnedAt);

        var records = (await admin.GetAsync(mailsPath, ct).Ok())["list"]!.AsArray();
        var withdrawMail = Assert.Single(records, x => x!["kind"]!.GetValue<string>() == "ELECTRICAL_HANDOFF_WITHDRAW")!;
        Assert.Equal("【撤回转迅得电控】外协电控厂商 工令 HANDOFF-1", withdrawMail["subject"]!.GetValue<string>());
        Assert.Equal("外协电控厂商（签名：撤回签名）", withdrawMail["createdByName"]!.GetValue<string>());
        Assert.Null(withdrawMail["attachmentName"]);
        Assert.True(JsonNode.DeepEquals(mail["recipients"], withdrawMail["recipients"]));
        Assert.Contains(records, x => x!.Id() == originalNoticeId);

        transport.RefusedAddress = null;
        var originalMessage = Assert.Single(transport.Messages, x => x.Subject == "【转迅得电控】外协电控厂商 工令 HANDOFF-1");
        transport.Messages.Clear();
        Assert.Equal(2, await worker.RunOnceAsync(ct));
        var message = Assert.Single(transport.Messages, x => x.Subject == "【转迅得电控】外协电控厂商 工令 HANDOFF-1");
        Assert.Equal(originalMessage.TextBody, message.TextBody);
        Assert.Equal(originalMessage.HtmlBody, message.HtmlBody);
        Assert.Equal("handoff_to@example.invalid", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("outsourcing@vendor.invalid", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Empty(message.Attachments);
        foreach (var value in new[] { "HANDOFF-1", "客户简称", "ST-201L", "OEMA", "外协电控厂商", "转交<签名>",
            turnedAt.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " (UTC+8)" })
            Assert.Contains(value, message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("客户电控全称", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("转交<签名>", message.HtmlBody, StringComparison.Ordinal);
        var withdrawal = Assert.Single(transport.Messages, x => x.Subject == "【撤回转迅得电控】外协电控厂商 工令 HANDOFF-1");
        Assert.Empty(withdrawal.Attachments);
        AssertHandoffLayout(message, "转交<签名>", turnedAt, "已由厂商签名转交迅得电控");
        AssertHandoffLayout(withdrawal, "撤回签名", withdrawnRow["electricalHandoffAt"]!.GetValue<DateTime>(), "签名撤回");

        // When the last eligible address is removed the item still changes, without creating a fifth mail.
        await admin.PutAsync(ccPath, new { entries = new[] { new { userId = noticeUser, modules = new[] { "OUTSOURCE_NOTICE" } } } }, ct).Ok();
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE outsource_items SET status='NORMAL' WHERE id=@id", new { id });
        var silent = await vendor.PostAsync(path, handoff with { signerName = "无收件人签名" }, ct).Ok();
        Assert.Equal("NORMAL", silent["status"]!.GetValue<string>());
        await using (var conn = await host.OpenAsync(ct))
        {
            var stored = await conn.QuerySingleAsync<(bool Handoff, string Signer, string SendStatus)>(
                "SELECT electrical_handoff,electrical_handoff_signer,send_status FROM outsource_items WHERE id=@id", new { id });
            Assert.Equal((true, "无收件人签名", "SENT"), stored);
            Assert.Equal(4, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_mails"));
            var changes = await conn.QueryAsync<(string Old, string New, string Source, ulong? Oem, string Signer)>("""
                SELECT old_value,new_value,source,actor_oem_account_id,signer_name
                FROM outsource_item_changes WHERE item_id=@id AND field='electricalHandoff' ORDER BY id
                """, new { id });
            Assert.Equal([("否", "是", "VENDOR", (ulong?)oemAccountId, "转交<签名>"),
                ("是", "否", "VENDOR", (ulong?)oemAccountId, "撤回签名"),
                ("否", "是", "VENDOR", (ulong?)oemAccountId, "无收件人签名")], changes);
            var audits = (await conn.QueryAsync<(string Action, ulong AccountId, string Detail)>("""
                SELECT action,actor_account_id,detail FROM audit_logs WHERE target_id=@targetId AND actor_realm='oem'
                  AND action IN ('OUTSOURCE_ELECTRICAL_HANDOFF','OUTSOURCE_ELECTRICAL_HANDOFF_WITHDRAW') ORDER BY id
                """, new { targetId = id.ToString() })).ToArray();
            Assert.Equal(["OUTSOURCE_ELECTRICAL_HANDOFF", "OUTSOURCE_ELECTRICAL_HANDOFF_WITHDRAW", "OUTSOURCE_ELECTRICAL_HANDOFF"],
                audits.Select(x => x.Action));
            Assert.All(audits, x => Assert.Equal(oemAccountId, x.AccountId));
            Assert.Equal([true, true, false], audits.Select(x => JsonNode.Parse(x.Detail)!["mailQueued"]!.GetValue<bool>()));
        }
    }

    private static void AssertHandoffLayout(MimeMessage message, string signer, DateTime occurredAt, string action)
    {
        var body = XElement.Parse(message.HtmlBody!);
        var paragraphs = body.Elements("p").Select(p => p.Value).ToArray();
        Assert.Equal("迅得电控同仁 您好：", paragraphs[0]);
        Assert.Contains(action, paragraphs[1], StringComparison.Ordinal);
        Assert.Equal("此邮件由系统发送。", paragraphs[^1]);

        string[] headers = ["工令", "客户", "机型", "外发类型", "外发厂商", "签名人", "操作时间"];
        var rows = body.Element("table")!.Elements("tr").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(headers, rows[0].Elements("th").Select(cell => cell.Value));
        var values = rows[1].Elements("td").Select(cell => cell.Value).ToArray();
        Assert.Equal(headers.Length, values.Length);
        Assert.Equal(signer, values[^2]);
        var time = occurredAt.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"{time} (UTC+8)", values[^1]);
        Assert.Contains(string.Join(" | ", headers), message.TextBody, StringComparison.Ordinal);
        Assert.Contains(string.Join(" | ", values), message.TextBody, StringComparison.Ordinal);
    }

    private sealed class CaptureTransport : IOutsourceMailTransport
    {
        public List<MimeMessage> Messages { get; } = [];
        public string? RefusedAddress { get; set; }

        public Task<OutsourceTransportResult> SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            var rejected = message.To.Mailboxes.Select(x => x.Address).Where(x => x == RefusedAddress).ToArray();
            return Task.FromResult(new OutsourceTransportResult(rejected));
        }
    }

    private static async Task<string> SheetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return await reader.ReadToEndAsync(ct);
    }

    private static string Cell(string text) => $"<t xml:space=\"preserve\">{text}</t>";
}
