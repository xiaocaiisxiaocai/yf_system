using System.Net.Sockets;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>Vendor mails of the outsourcing list (外协清单契约 → 发送): preview, confirm, withdrawal, highlighting and delivery.</summary>
public sealed class OutsourceMailTests
{
    private sealed class StubIpms : IIpmsClient
    {
        public Dictionary<string, IpmsWorkOrder> Orders { get; } = new(StringComparer.Ordinal);
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
        {
            IReadOnlyDictionary<string, IpmsWorkOrder> found = workOrderNos.Where(Orders.ContainsKey)
                .ToDictionary(no => no, no => Orders[no], StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    /// <summary>Records messages instead of talking to a server; refuses configured addresses or fails the connection.</summary>
    private sealed class FakeTransport : IOutsourceMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public HashSet<string> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Offline { get; set; }

        public Task<OutsourceTransportResult> SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
        {
            if (Offline) throw new SocketException((int)SocketError.ConnectionRefused);
            Sent.Add(message);
            var rejected = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(x => x.Address).Where(Refuse.Contains).ToArray();
            return Task.FromResult(new OutsourceTransportResult(rejected));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task PurchasingPreviewsConfirmsAndTheWorkerDeliversOneMailPerVendor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_mail", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (90,'外协采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (91,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 90,id FROM permissions WHERE code LIKE 'outsource%';
            INSERT INTO role_permissions(role_id,permission_id) SELECT 91,id FROM permissions WHERE code IN ('outsource','outsource:view');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9911,'om_buyer','unused','采购','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9912,'om_viewer','unused','主管','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9913,'om_planner','unused','生管','planner@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9911,90),(9912,91),(9913,91);
            """, ct);
        var buyer = new CurrentUser(9911, "om_buyer", "INTERNAL", null);
        var viewer = new CurrentUser(9912, "om_viewer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var signal = new OutsourceMailSignal();
        var mails = new OutsourceMailService(factory, audit, signal);

        var dingya = await settings.CreateVendorAsync(buyer, new("鼎亚", null, 1), ct);
        var duopu = await settings.CreateVendorAsync(buyer, new("多普", null, 2), ct);
        await settings.CreateContactAsync(buyer, dingya.Id, new(null, "d1@vendor.invalid", 1), ct);
        await settings.CreateContactAsync(buyer, dingya.Id, new("窗口", "d2@vendor.invalid", 2), ct);
        await settings.CreateContactAsync(buyer, duopu.Id, new(null, "p1@vendor.invalid", 1), ct);
        var oema = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id, duopu.Id]), ct);
        await settings.SetDefaultCcAsync(buyer, new([9913]), ct);
        foreach (var (no, customer) in new[] { ("A1", "客户甲"), ("B1", "客户乙"), ("C1", "客户甲") })
            ipms.Orders[no] = new(no, customer, "ST-201L", null, null, null, null, null, null, 1, new DateOnly(2026, 12, 31), false);
        await items.ImportAsync(buyer, new([new(2, "2026-11-04", "A1", "鼎亚", "OEMA"), new(3, "2026-11-04", "B1", "鼎亚", "OEMA"), new(4, "2026-11-04", "C1", "多普", "OEMA")]), ct);
        var list = (await items.ListAsync(buyer, Query(), 1, 20, 0, ct)).List;
        ulong Id(string no) => list.Single(x => x.WorkOrderNo == no).Id;
        ulong[] all = [Id("A1"), Id("B1"), Id("C1")];

        // Preview: one mail per vendor, vendor recipients and the default copy list decided by the server.
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => mails.PreviewAsync(viewer, new(all), ct))).Status);
        var preview = await mails.PreviewAsync(buyer, new(all), ct);
        Assert.Equal(["NOTICE:" + dingya.Id, "NOTICE:" + duopu.Id], preview.Mails.Select(x => x.Key));
        var toDingya = preview.Mails[0];
        Assert.Equal(["d1@vendor.invalid", "d2@vendor.invalid"], toDingya.To.Select(x => x.Email));
        Assert.Equal(["planner@example.invalid"], toDingya.DefaultCc.Select(x => x.Email));
        Assert.Equal((2, 2), (toDingya.ItemIds.Length, toDingya.CustomerCount));
        Assert.StartsWith("【外发通知】鼎亚 ", toDingya.Subject, StringComparison.Ordinal);
        Assert.Contains("客户乙", toDingya.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("C1", toDingya.HtmlBody, StringComparison.Ordinal);

        // Confirm: a stale fingerprint is refused; extra copies apply to one mail only; a repeated key is idempotent.
        var stale = await Assert.ThrowsAsync<ApiException>(() => mails.SendAsync(buyer, new(all, "0000", "key-stale-0001", null), ct));
        Assert.Equal(40903, stale.Code);
        OutsourceExtraCc[] extra = [new(toDingya.Key, [9912], ["customer@client.invalid", "D1@vendor.invalid"])];
        var sent = await mails.SendAsync(buyer, new(all, preview.Fingerprint, "key-first-0001", extra), ct);
        Assert.Equal(2, sent.Mails.Length);
        Assert.All(sent.Mails, x => Assert.Equal("QUEUED", x.Status));
        var dingyaMail = sent.Mails.Single(x => x.VendorId == dingya.Id);
        Assert.Equal(["d1@vendor.invalid", "d2@vendor.invalid"], dingyaMail.Recipients.Where(x => x.Kind == "TO").Select(x => x.Email));
        Assert.Equal(["planner@example.invalid", "viewer@example.invalid", "customer@client.invalid"],
            dingyaMail.Recipients.Where(x => x.Kind == "CC").Select(x => x.Email));
        Assert.Equal(["planner@example.invalid"], sent.Mails.Single(x => x.VendorId == duopu.Id).Recipients.Where(x => x.Kind == "CC").Select(x => x.Email));
        Assert.Equal(2, (await mails.SendAsync(buyer, new(all, preview.Fingerprint, "key-first-0001", extra), ct)).Mails.Length);
        await using (var connection = await database.Database.OpenAsync(ct))
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_mails"));
        list = (await items.ListAsync(buyer, Query(), 1, 20, 0, ct)).List;
        Assert.All(list, x => Assert.Equal("SENT", x.SendStatus));

        // Delivery: one message with every recipient; refused copies make it PARTIAL, all vendor addresses refused FAILED.
        var transport = new FakeTransport();
        transport.Refuse.Add("customer@client.invalid");
        transport.Refuse.Add("p1@vendor.invalid");
        var smtpOptions = new AppOptions { Smtp = new() { Host = "smtp.example.invalid", Port = 465, Username = "u", Password = "p", From = "noreply@example.invalid" } };
        var worker = new OutsourceMailWorker(factory, new SmtpSettingsService(database.Database, smtpOptions, audit), transport, signal,
            smtpOptions, audit, NullLogger<OutsourceMailWorker>.Instance);
        Assert.Equal(2, await worker.RunOnceAsync(ct));
        var message = transport.Sent.Single(x => x.Subject!.Contains("鼎亚", StringComparison.Ordinal));
        Assert.Equal((2, 3), (message.To.Count, message.Cc.Count));
        Assert.Equal(dingyaMail.AttachmentName, Assert.Single(message.Attachments).ContentDisposition!.FileName);
        var records = (await mails.ListAsync(viewer, null, null, 1, 20, 0, ct)).List;
        var partial = records.Single(x => x.VendorId == dingya.Id);
        Assert.Equal("PARTIAL", partial.Status);
        Assert.True(partial.Recipients.Single(x => x.Email == "customer@client.invalid").Rejected);
        var failed = records.Single(x => x.VendorId == duopu.Id);
        Assert.Equal(("FAILED", "厂商收件地址全部被拒收"), (failed.Status, failed.LastError));
        Assert.Equal(0, await worker.RunOnceAsync(ct));
        var attachment = await mails.AttachmentAsync(viewer, partial.Id, ct);
        Assert.Equal(partial.AttachmentName, attachment.Name);

        // Retry a failed mail; a connection failure keeps it queued for a later attempt.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => mails.RetryAsync(buyer, partial.Id, ct))).Status);
        transport.Offline = true;
        Assert.Equal("QUEUED", (await mails.RetryAsync(buyer, failed.Id, ct)).Status);
        await worker.RunOnceAsync(ct);
        var waiting = (await mails.ListAsync(viewer, duopu.Id, null, 1, 20, 0, ct)).List.Single();
        Assert.Equal(("QUEUED", 1, "无法连接 SMTP 服务器"), (waiting.Status, waiting.Attempts, waiting.LastError));

        // Changes after sending: the changed cell is yellow next time; moving an item adds a withdrawal for the old vendor.
        var a = list.Single(x => x.WorkOrderNo == "A1");
        await items.UpdateAsync(buyer, a.Id, new("2026-11-04", dingya.Id, null, "加急", "NORMAL", a.UpdatedAt), ct);
        var b = list.Single(x => x.WorkOrderNo == "B1");
        await items.UpdateAsync(buyer, b.Id, new("2026-11-04", duopu.Id, null, null, "NORMAL", b.UpdatedAt), ct);
        var again = await mails.PreviewAsync(buyer, new([a.Id, b.Id]), ct);
        Assert.Equal(["NOTICE:" + dingya.Id, "NOTICE:" + duopu.Id, "WITHDRAW:" + dingya.Id], again.Mails.Select(x => x.Key));
        Assert.Contains("background:#ffff00;\">加急", again.Mails[0].HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("#ffff00", again.Mails[1].HtmlBody, StringComparison.Ordinal);
        var withdrawal = again.Mails[2];
        Assert.StartsWith("【外发撤回】鼎亚 ", withdrawal.Subject, StringComparison.Ordinal);
        Assert.Equal(["B1"], withdrawal.WorkOrderNos);
        Assert.DoesNotContain("多普", withdrawal.HtmlBody, StringComparison.Ordinal);
        var second = await mails.SendAsync(buyer, new([a.Id, b.Id], again.Fingerprint, "key-second-0001", null), ct);
        Assert.Null(second.Mails.Single(x => x.Kind == "WITHDRAW").AttachmentName);
        // Sent once to the new vendor, a later preview no longer withdraws from the old one.
        Assert.DoesNotContain((await mails.PreviewAsync(buyer, new([b.Id]), ct)).Mails, x => x.Kind == "WITHDRAW");

        // Items that would be refused by the type/vendor guard cannot be mailed.
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id]), ct);
        var blocked = await Assert.ThrowsAsync<ApiException>(() => mails.PreviewAsync(buyer, new([Id("C1")]), ct));
        Assert.Contains("外发类型与厂商不符", blocked.Message, StringComparison.Ordinal);
    }

    private static OutsourceItemQuery Query() => new(null, null, null, null, null, null, null, false);
}
