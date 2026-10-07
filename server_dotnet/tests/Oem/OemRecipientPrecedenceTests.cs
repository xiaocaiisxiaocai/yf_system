using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Common;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// When the target vendor is disabled and also has no enabled account, the vendor-disabled reason
/// wins: re-enabling accounts alone would not help, so the sender must be told the vendor is off.
/// </summary>
public sealed class OemRecipientPrecedenceTests
{
    [Fact(Timeout = 180_000)]
    public async Task DisabledVendorWithoutEnabledAccountsReportsTheVendorAsDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var companyId = (await world.Admin.PostAsync("/api/v1/oem/companies", new { name = "停用且无账号厂商" }, ct).Ok()).Id();
        var accountId = (await world.Admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo = "precedence_only", realName = "唯一账号", email = "precedence_only@example.invalid", password = "Recipient#2026",
        }, ct).Ok()).Id();
        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Ok();
        var transferId = TransferId(draft);
        await host.UploadAsync(world.Sender, transferId, "precedence.pdf", OemTestHost.Pdf("precedence"), ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();

        // Disable the vendor first (its last account may only go while it is disabled), then its only account.
        await world.Admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "DISABLED" }, ct).Ok();
        await world.Admin.PutAsync($"/api/v1/oem/accounts/{accountId}/status", new { status = "DISABLED" }, ct).Ok();

        var send = await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("目标厂商已停用", Message(send));
        Assert.NotEqual(OemRecipientPolicy.MissingAccountMessage, Message(send));

        var create = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("目标厂商不存在或已停用", Message(create));

        // A disabled vendor is not offered as a target at all (so no account hint is shown for it).
        var options = (await world.Sender.GetAsync("/api/v1/oem/company-options", ct).Ok()).AsArray();
        Assert.DoesNotContain(options, option => option!.Id() == companyId);

        var unchanged = await world.Sender.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();
        Assert.Equal("DRAFT", unchanged["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal(Version(draft), Version(unchanged));
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_transfers WHERE oem_company_id=@companyId", new { companyId }));

        // Re-enabling only the vendor moves the reason on to the missing account.
        await world.Admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "ACTIVE" }, ct).Ok();
        send = await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal(OemRecipientPolicy.MissingAccountMessage, Message(send));
    }

    private static string Message(ApiResponse response) => JsonNode.Parse(response.Body)!["message"]!.GetValue<string>();
}
