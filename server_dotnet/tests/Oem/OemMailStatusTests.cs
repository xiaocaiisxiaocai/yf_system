using System.Net;
using Dapper;

namespace Yf.Api.Tests.Oem;

public sealed class OemMailStatusTests
{
    [Fact(Timeout = 180_000)]
    public async Task SharedSmtpStatusScopesBusinessQueueAndAuditWithoutCrossingNotifyPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        await using (var connection = await host.OpenAsync(ct))
        {
            await connection.ExecuteAsync("""
                INSERT INTO email_outbox(event_type,recipient_email,subject,body,status,retry_count)
                VALUES('MESSAGE_CREATED','robot@example.invalid','s','b','PENDING',0),
                    ('OEM_TRANSFER_RELEASED','vendor@example.invalid','s','b','PENDING',0);
                INSERT INTO audit_logs(action,target_type,detail,created_at)
                VALUES('EMAIL_RETRY','email_outbox',JSON_OBJECT('recipient','robot@example.invalid','targetName','private-title'),UTC_TIMESTAMP(3)),
                    ('OEM_EMAIL_RETRY','email_outbox',JSON_OBJECT('recipient','vendor@example.invalid','targetName','private-title'),UTC_TIMESTAMP(3));
                """);
        }
        var robot = await admin.GetAsync("/api/v1/admin/system/mail-status", ct).Ok();
        var oem = await admin.GetAsync("/api/v1/admin/system/mail-status?scope=oem", ct).Ok();
        var all = await admin.GetAsync("/api/v1/admin/system/mail-status?scope=all", ct).Ok();
        Assert.Equal(1, robot["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal(1, oem["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal(2, all["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal("EMAIL_RETRY", Assert.Single(robot["recent"]!.AsArray())!["action"]!.GetValue<string>());
        Assert.Equal("OEM_EMAIL_RETRY", Assert.Single(oem["recent"]!.AsArray())!["action"]!.GetValue<string>());
        Assert.Equal(2, all["recent"]!.AsArray().Count);
        await admin.PostAsync("/api/v1/oem/companies", new { name = "没有联络人的厂商" }, ct).Ok();
        var missingContacts = await admin.GetAsync("/api/v1/admin/system/mail-status?scope=oem", ct).Ok();
        Assert.Equal(1UL, missingContacts["missingOemContactCompanyCount"]!.GetValue<ulong>());
        Assert.DoesNotContain("private-title", all.ToJsonString());
        Assert.DoesNotContain("vendor@example.invalid", all.ToJsonString());
        await host.CreateInternalUserAsync("smtp_only", "Mail#2026", ["config:manage"], null, ct);
        var operatorClient = await host.LoginInternalAsync("smtp_only", "Mail#2026", ct);
        await operatorClient.GetAsync("/api/v1/admin/system/mail-status", ct).Ok();
        await operatorClient.GetAsync("/api/v1/admin/system/mail-status?scope=oem", ct).Status(HttpStatusCode.Forbidden);
        await operatorClient.GetAsync("/api/v1/admin/system/mail-status?scope=all", ct).Status(HttpStatusCode.Forbidden);
        await admin.GetAsync("/api/v1/admin/system/mail-status?scope=invalid", ct).Status(HttpStatusCode.BadRequest);
    }
}
