using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Common;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemRecipientAccountTests
{
    [Fact(Timeout = 180_000)]
    public async Task OptionsAndCreationRequireAnEnabledAccountOnTheTargetCompany()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var companyId = (await world.Admin.PostAsync("/api/v1/oem/companies", new { name = "待配置接收账号厂商" }, ct).Ok()).Id();

        // Another vendor's active account cannot make this company a valid recipient.
        await AssertOptionAsync(world.Sender, companyId, false, ct);
        var noAccount = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal(OemRecipientPolicy.MissingAccountMessage, JsonNode.Parse(noAccount.Body)!["message"]!.GetValue<string>());

        var accountId = await CreateAccountAsync(world.Admin, companyId, "recipient_one", ct);
        await world.Admin.PutAsync($"/api/v1/oem/accounts/{accountId}/status", new { status = "DISABLED" }, ct).Ok();
        await AssertOptionAsync(world.Sender, companyId, false, ct);
        await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Status(HttpStatusCode.BadRequest);
        await using (var conn = await host.OpenAsync(ct))
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_transfers WHERE oem_company_id=@companyId", new { companyId }));

        // A newly provisioned active account qualifies even before its first password change.
        await CreateAccountAsync(world.Admin, companyId, "recipient_two", ct);
        await AssertOptionAsync(world.Sender, companyId, true, ct);
        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Ok();
        Assert.Equal("DRAFT", draft["summary"]!["lifecycleStatus"]!.GetValue<string>());
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmissionRechecksAccountsAndPreservesTheDraftUntilRecipientIsRestored(bool deleteAccount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var companyId = (await world.Admin.PostAsync("/api/v1/oem/companies", new { name = "上传期间账号变化厂商" }, ct).Ok()).Id();
        var accountId = await CreateAccountAsync(world.Admin, companyId, "recipient_before", ct);
        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Ok();
        var transferId = TransferId(draft);
        await host.UploadAsync(world.Sender, transferId, "drawing.pdf", OemTestHost.Pdf("recipient prerequisite"), ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();

        if (deleteAccount) await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Ok();
        else await world.Admin.PutAsync($"/api/v1/oem/accounts/{accountId}/status", new { status = "DISABLED" }, ct).Ok();
        await AssertOptionAsync(world.Sender, companyId, false, ct);
        var rejected = await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal(OemRecipientPolicy.MissingAccountMessage, JsonNode.Parse(rejected.Body)!["message"]!.GetValue<string>());
        var unchanged = await world.Sender.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok();
        Assert.Equal("DRAFT", unchanged["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal(Version(draft), Version(unchanged));
        Assert.Null(unchanged["summary"]!["sentAt"]);
        Assert.Single(unchanged["files"]!.AsArray());
        Assert.Null(unchanged["approval"]);
        await using (var conn = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_flow_instances WHERE transfer_id=@transferId", new { transferId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_TRANSFER_SEND' AND target_id=@targetId",
                new { targetId = transferId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }

        if (deleteAccount) await CreateAccountAsync(world.Admin, companyId, "recipient_after", ct);
        else await world.Admin.PutAsync($"/api/v1/oem/accounts/{accountId}/status", new { status = "ACTIVE" }, ct).Ok();
        await AssertOptionAsync(world.Sender, companyId, true, ct);
        var submitted = await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(unchanged) }, ct).Ok();
        Assert.Equal("SEALED", submitted["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.NotNull(submitted["approval"]);
    }

    private static async Task<ulong> CreateAccountAsync(ApiClient admin, ulong companyId, string employeeNo, CancellationToken ct) =>
        (await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo, realName = "接收账号", email = employeeNo + "@example.invalid", password = "Recipient#2026",
        }, ct).Ok()).Id();

    private static async Task AssertOptionAsync(ApiClient sender, ulong companyId, bool canReceive, CancellationToken ct)
    {
        var options = (await sender.GetAsync("/api/v1/oem/company-options", ct).Ok()).AsArray();
        var target = Assert.Single(options, option => option!.Id() == companyId)!;
        Assert.Equal(canReceive, target["canReceive"]!.GetValue<bool>());
        if (canReceive) Assert.Null(target["unavailableReason"]);
        else Assert.Equal(OemRecipientPolicy.MissingAccountMessage, target["unavailableReason"]!.GetValue<string>());
    }
}
