using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Admin;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Reproduces the directory/list discrepancy using synthetic vendors and real HTTP requests.</summary>
public sealed class OemDirectoryVisibilityTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherCompanyDraftOrAbandonedTransferDoesNotBlockDeletingAnEmptyCompany(bool abandon)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);
        var draft = await CreateDraftAsync(world.Sender, world.JunCompany, ct);
        var transferId = TransferId(draft);
        if (abandon)
            await world.Sender.DeleteAsync($"/api/v1/oem/transfers/{transferId}?version={Version(draft)}", ct).Ok();

        var visible = (await world.Admin.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray();
        if (abandon) Assert.Equal(world.JunCompany, Assert.Single(visible)!["companyId"]!.GetValue<ulong>());
        else Assert.Empty(visible);

        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Ok();
        await world.Admin.DeleteAsync($"/api/v1/oem/companies/{world.YuanCompany}", ct).Ok();
        var remainingAccount = Assert.Single((await world.Admin.GetAsync(
            $"/api/v1/oem/companies/{world.JunCompany}/accounts", ct).Ok()).AsArray())!;
        Assert.Equal("JC001", remainingAccount["employeeNo"]!.GetValue<string>());
        Assert.Equal(transferId, TransferId(await world.Sender.GetAsync($"/api/v1/oem/transfers/{transferId}", ct).Ok()));
        await using var connection = await host.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_companies WHERE id=@id", new { id = world.YuanCompany }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_transfers WHERE oem_company_id=@id", new { id = world.JunCompany }));
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentPolicyBlocksHiddenEmptyDraftAndContinuesBlockingAfterAbandonment(bool vendorAuthored)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);
        var junDraft = await CreateDraftAsync(world.Sender, world.JunCompany, ct);
        await world.Sender.DeleteAsync($"/api/v1/oem/transfers/{TransferId(junDraft)}?version={Version(junDraft)}", ct).Ok();

        var author = world.Sender;
        if (vendorAuthored)
        {
            author = await host.LoginOemAsync("YK001", "Vendor#2026", ct);
            await author.PutAsync("/api/v1/oem/auth/password",
                new { oldPassword = "Vendor#2026", newPassword = "Vendor#2027x" }, ct).Ok();
            author = await host.LoginOemAsync("YK001", "Vendor#2027x", ct);
        }
        var yuanDraft = await CreateDraftAsync(author, world.YuanCompany, ct);
        var yuanTransferId = TransferId(yuanDraft);

        // Exactly the screenshot: no list filters, one visible abandoned transfer to 骏创.
        var listed = await world.Admin.GetAsync("/api/v1/oem/transfers", ct).Ok();
        Assert.Equal(1UL, listed["total"]!.GetValue<ulong>());
        var visible = Assert.Single(listed["list"]!.AsArray())!;
        Assert.Equal(world.JunCompany, visible["companyId"]!.GetValue<ulong>());
        Assert.Equal("ABANDONED", visible["lifecycleStatus"]!.GetValue<string>());
        Assert.Empty((await world.Admin.GetAsync($"/api/v1/oem/transfers?companyId={world.YuanCompany}", ct).Ok())["list"]!.AsArray());
        await world.Admin.GetAsync($"/api/v1/oem/transfers/{yuanTransferId}", ct).Status(HttpStatusCode.NotFound);

        var rejected = await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Equal(OemDirectoryService.LastActiveAccountMessage, rejected.Json!["message"]!.GetValue<string>());
        await using (var connection = await host.OpenAsync(ct))
        {
            var state = await connection.QuerySingleAsync<(ulong CompanyId, string Status, DateTime? SentAt, int Files)>("""
                SELECT oem_company_id CompanyId,lifecycle_status Status,sent_at SentAt,
                    (SELECT COUNT(*) FROM oem_transfer_files WHERE transfer_id=t.id) Files
                FROM oem_transfers t WHERE id=@id
                """, new { id = yuanTransferId });
            Assert.Equal((world.YuanCompany, "DRAFT", (DateTime?)null, 0), state);
            Assert.Equal(world.YuanCompany, await connection.ExecuteScalarAsync<ulong>(
                "SELECT oem_company_id FROM oem_accounts WHERE id=@id", new { id = world.YuanAccount }));
        }

        // Deleting the unsent, zero-file draft retains ABANDONED history; it does not remove the restriction.
        await author.DeleteAsync($"/api/v1/oem/transfers/{yuanTransferId}?version={Version(yuanDraft)}", ct).Ok();
        Assert.Equal("ABANDONED", (await world.Admin.GetAsync($"/api/v1/oem/transfers/{yuanTransferId}", ct).Ok())
            ["summary"]!["lifecycleStatus"]!.GetValue<string>());
        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Status(HttpStatusCode.Conflict, 40901);

        await world.Admin.PutAsync($"/api/v1/oem/companies/{world.YuanCompany}/status", new { status = "DISABLED" }, ct).Ok();
        // If the vendor authored it, its sender reference still prevents account deletion.
        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct)
            .Status(vendorAuthored ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        await world.Admin.DeleteAsync($"/api/v1/oem/companies/{world.YuanCompany}", ct).Status(HttpStatusCode.BadRequest);
    }

    private sealed record World(ApiClient Admin, ApiClient Sender, ulong JunCompany, ulong YuanCompany, ulong YuanAccount);

    private static async Task<World> SetupAsync(OemTestHost host, CancellationToken ct)
    {
        var admin = await host.LoginAdminAsync(ct);
        var jun = (await admin.PostAsync("/api/v1/oem/companies", new { name = "骏创" }, ct).Ok()).Id();
        var yuan = (await admin.PostAsync("/api/v1/oem/companies", new { name = "元控" }, ct).Ok()).Id();
        // Reverse account creation order so company ids and account ids are not interchangeable.
        var yuanAccount = (await admin.PostAsync($"/api/v1/oem/companies/{yuan}/accounts",
            new { employeeNo = "YK001", password = "Vendor#2026" }, ct).Ok()).Id();
        await admin.PostAsync($"/api/v1/oem/companies/{jun}/accounts",
            new { employeeNo = "JC001", password = "Vendor#2026" }, ct).Ok();
        Assert.NotEqual(yuan, yuanAccount);
        var org = await host.CreateOrgPathAsync("删除复现", ct);
        await host.CreateInternalUserAsync("draft_author", "Sender#2026x", ["oem:transfer_create"], org.Section, ct);
        return new(admin, await host.LoginInternalAsync("draft_author", "Sender#2026x", ct), jun, yuan, yuanAccount);
    }

    private static Task<JsonNode> CreateDraftAsync(ApiClient author, ulong companyId, CancellationToken ct) =>
        author.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Ok();
}
