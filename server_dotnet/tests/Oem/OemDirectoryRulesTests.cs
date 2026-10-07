using System.Net;
using Dapper;
using Yf.Api.Modules.Oem.Admin;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Directory invariants: an active vendor keeps an active account; a vendor with in-flight transfers stays enabled.</summary>
public sealed class OemDirectoryRulesTests
{
    [Fact(Timeout = 180_000)]
    public async Task LastActiveAccountOfAnActiveVendorCannotBeDisabledOrDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "唯一账号厂商" }, ct).Ok()).Id();
        var first = await CreateAccountAsync(admin, companyId, "last_one", ct);

        var disable = await admin.PutAsync($"/api/v1/oem/accounts/{first}/status", new { status = "DISABLED" }, ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Equal(OemDirectoryService.LastActiveAccountMessage, disable.Json!["message"]!.GetValue<string>());
        var delete = await admin.DeleteAsync($"/api/v1/oem/accounts/{first}", ct).Status(HttpStatusCode.Conflict, 40901);
        Assert.Equal(OemDirectoryService.LastActiveAccountMessage, delete.Json!["message"]!.GetValue<string>());
        Assert.Equal("ACTIVE", await StatusOfAsync(host, first, ct));

        // With a second active account either one may go, but never the last one standing.
        var second = await CreateAccountAsync(admin, companyId, "last_two", ct);
        await admin.PutAsync($"/api/v1/oem/accounts/{first}/status", new { status = "DISABLED" }, ct).Ok();
        await admin.PutAsync($"/api/v1/oem/accounts/{second}/status", new { status = "DISABLED" }, ct).Status(HttpStatusCode.Conflict, 40901);
        await admin.DeleteAsync($"/api/v1/oem/accounts/{second}", ct).Status(HttpStatusCode.Conflict, 40901);
        // A disabled account is not counted and can always be deleted.
        await admin.DeleteAsync($"/api/v1/oem/accounts/{first}", ct).Ok();
        var third = await CreateAccountAsync(admin, companyId, "last_three", ct);
        await admin.DeleteAsync($"/api/v1/oem/accounts/{second}", ct).Ok();
        Assert.Equal("ACTIVE", await StatusOfAsync(host, third, ct));

        // A disabled vendor is exempt: its last account may be disabled and deleted.
        await admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "DISABLED" }, ct).Ok();
        await admin.PutAsync($"/api/v1/oem/accounts/{third}/status", new { status = "DISABLED" }, ct).Ok();
        await admin.DeleteAsync($"/api/v1/oem/accounts/{third}", ct).Ok();
        await admin.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Ok();
    }

    [Fact(Timeout = 240_000)]
    public async Task VendorWithInFlightTransfersInEitherDirectionCannotBeDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);

        // A draft is not in flight.
        var outbound = await CreateOutboundAsync(world, "进行中出站", ct);
        var outboundId = TransferId(outbound);
        await host.UploadAsync(world.Sender, outboundId, "out.pdf", OemTestHost.Pdf("in flight out"), ct);
        outbound = await world.Sender.GetAsync($"/api/v1/oem/transfers/{outboundId}", ct).Ok();
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "进行中入站" }, ct).Ok();
        var inboundId = TransferId(inbound);
        await host.UploadAsync(world.Vendor, inboundId, "in.pdf", OemTestHost.Pdf("in flight in"), ct);
        inbound = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{inboundId}", ct).Ok();

        outbound = await world.Sender.PostAsync($"/api/v1/oem/transfers/{outboundId}/send", new { version = Version(outbound) }, ct).Ok();
        Assert.Equal("SEALED", outbound["summary"]!["lifecycleStatus"]!.GetValue<string>());
        var refused = await world.Admin.PutAsync($"/api/v1/oem/companies/{world.CompanyId}/status", new { status = "DISABLED" }, ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Contains("1 个进行中的传递单", refused.Json!["message"]!.GetValue<string>());

        inbound = await world.Vendor.PostAsync($"/api/v1/oem/transfers/{inboundId}/send", new { version = Version(inbound) }, ct).Ok();
        Assert.Equal("SEALED", inbound["summary"]!["lifecycleStatus"]!.GetValue<string>());
        refused = await world.Admin.PutAsync($"/api/v1/oem/companies/{world.CompanyId}/status", new { status = "DISABLED" }, ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Contains("2 个进行中的传递单", refused.Json!["message"]!.GetValue<string>());
        Assert.Contains("终止", refused.Json!["message"]!.GetValue<string>());
        await using (var conn = await host.OpenAsync(ct))
            Assert.Equal("ACTIVE", await conn.ExecuteScalarAsync<string>("SELECT status FROM oem_companies WHERE id=@id", new { id = world.CompanyId }));
        // The refusal leaves the vendor's sessions intact.
        await world.Vendor.GetAsync("/api/v1/oem/auth/me", ct).Ok();

        await world.Admin.PostAsync($"/api/v1/oem/transfers/{outboundId}/cancel", new { reason = "停止合作", version = Version(outbound) }, ct).Ok();
        refused = await world.Admin.PutAsync($"/api/v1/oem/companies/{world.CompanyId}/status", new { status = "DISABLED" }, ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Contains("1 个进行中的传递单", refused.Json!["message"]!.GetValue<string>());
        inbound = await world.Admin.GetAsync($"/api/v1/oem/transfers/{inboundId}", ct).Ok();
        await world.Admin.PostAsync($"/api/v1/oem/transfers/{inboundId}/cancel", new { reason = "停止合作", version = Version(inbound) }, ct).Ok();

        await world.Admin.PutAsync($"/api/v1/oem/companies/{world.CompanyId}/status", new { status = "DISABLED" }, ct).Ok();
        await world.Vendor.GetAsync("/api/v1/oem/auth/me", ct).Status(HttpStatusCode.Unauthorized);
    }

    [Fact(Timeout = 180_000)]
    public async Task TransfersAreCreatedAndUpdatedWithoutARetentionTemplateField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var effective = (await world.Admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray()
            .Where(item => item!["status"]!.GetValue<string>() == "ACTIVE").Min(item => item!.Id());

        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new { title = "无策略字段", oemCompanyId = world.CompanyId }, ct).Ok();
        Assert.Equal(effective, draft["retention"]!["templateId"]!.GetValue<ulong>());
        draft = await world.Sender.PutAsync($"/api/v1/oem/transfers/{TransferId(draft)}",
            new { title = "无策略字段（改）", version = Version(draft) }, ct).Ok();
        Assert.Equal("无策略字段（改）", draft["summary"]!["title"]!.GetValue<string>());
        Assert.Equal(effective, draft["retention"]!["templateId"]!.GetValue<ulong>());

        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传无策略字段" }, ct).Ok();
        Assert.Equal(effective, inbound["retention"]!["templateId"]!.GetValue<ulong>());
    }

    private static async Task<ulong> CreateAccountAsync(ApiClient admin, ulong companyId, string employeeNo, CancellationToken ct) =>
        (await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo, realName = "规则账号", email = employeeNo + "@example.invalid", password = "Account#2026",
        }, ct).Ok()).Id();

    private static async Task<string> StatusOfAsync(OemTestHost host, ulong accountId, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>("SELECT status FROM oem_accounts WHERE id=@accountId", new { accountId }) ?? "";
    }
}
