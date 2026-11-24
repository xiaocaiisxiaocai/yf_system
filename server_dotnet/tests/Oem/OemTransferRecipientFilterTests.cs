using Dapper;
using Yf.Api.Modules.Identity;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemTransferRecipientFilterTests
{
    [Fact(Timeout = 240_000)]
    public async Task ToMeFiltersVisibleTransfersByRecipientAndDirection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("来件测试", ct);
        const string password = "Recipient#2026";
        var userAId = await host.CreateInternalUserAsync("recipient_a", password,
            ["oem:transfer_view", "oem:transfer_create"], org.Section, ct);
        var userBId = await host.CreateInternalUserAsync("recipient_b", password, ["oem:transfer_view"], org.Section, ct);
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync("INSERT INTO user_data_scopes (user_id, domain, scope) VALUES (@userBId, @domain, @scope)",
                new { userBId, domain = DataScopeDomains.OemTransfer, scope = DataScopeValues.All });

        var userA = await host.LoginInternalAsync("recipient_a", password, ct);
        var userB = await host.LoginInternalAsync("recipient_b", password, ct);
        var vendor = await host.CreateVendorAsync(admin, "来件厂商", "recipient_vendor", ct);
        var companyId = await host.CompanyIdOfAsync("recipient_vendor", ct);
        var outsourceItemId = await host.CreateOutsourceItemAsync(companyId, "WO-RECIPIENT-FILTER", ct);
        const string listPath = "/api/v1/oem/transfers";
        async Task<ulong> Create(ApiClient sender, string title) =>
            TransferId(await sender.PostAsync(listPath, new { title, outsourceItemId }, ct).Ok());

        var toA = await Create(vendor, "发给 A");
        var toB = await Create(vendor, "发给 B");
        var sentByA = await Create(userA, "A 发给厂商");
        var pendingToVendor = await Create(userA, "待审批");

        // Seed send outcomes in the disposable database so this test exercises the list independently of approval and upload.
        await using (var db = await host.OpenAsync(ct))
        {
            await db.ExecuteAsync("UPDATE oem_transfers SET lifecycle_status='RELEASED',sent_at=UTC_TIMESTAMP(3),released_at=UTC_TIMESTAMP(3) WHERE id IN @ids",
                new { ids = new[] { toA, toB, sentByA } });
            await db.ExecuteAsync("UPDATE oem_transfers SET lifecycle_status='SEALED',sent_at=UTC_TIMESTAMP(3) WHERE id=@pendingToVendor",
                new { pendingToVendor });
            await db.ExecuteAsync("INSERT INTO oem_transfer_internal_recipients (transfer_id,user_id) VALUES (@toA,@userAId),(@toB,@userBId)",
                new { toA, userAId, toB, userBId });
        }

        async Task<ulong[]> ListIds(ApiClient viewer, string query = "") =>
            (await viewer.GetAsync(listPath + query, ct).Ok())["list"]!.AsArray().Select(row => row!.Id()).Order().ToArray();

        Assert.Equal([toA, sentByA, pendingToVendor], await ListIds(userA));
        Assert.Equal([toA], await ListIds(userA, "?toMe=true"));
        Assert.Empty(await ListIds(userA, "?mine=true&toMe=true"));

        Assert.Equal([toA, toB, sentByA, pendingToVendor], await ListIds(userB));
        Assert.Equal([toB], await ListIds(userB, "?toMe=true"));

        Assert.Equal([toA, toB, sentByA], await ListIds(vendor));
        Assert.Equal([sentByA], await ListIds(vendor, "?toMe=true"));
        Assert.Empty(await ListIds(vendor, "?mine=true&toMe=true"));
    }
}
