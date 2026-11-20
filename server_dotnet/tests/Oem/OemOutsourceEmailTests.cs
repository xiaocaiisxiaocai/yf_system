using System.Net;

namespace Yf.Api.Tests.Oem;

/// <summary>Outsourcing mail addresses kept on the ODM/OEMA contact drawer, apart from the contacts (外协清单契约 → 外协邮箱).</summary>
public sealed class OemOutsourceEmailTests
{
    [Fact(Timeout = 180_000)]
    public async Task OutsourcingAddressesAreMaintainedApartFromTheContacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendor = await host.CreateVendorAsync(admin, "外协邮箱厂", "os_mail_vendor", ct);
        var companyId = await host.CompanyIdOfAsync("os_mail_vendor", ct);
        await admin.PostAsync($"/api/v1/oem/companies/{companyId}/contacts",
            new { name = "ODM窗口", email = "odm@example.invalid", sortNo = 0 }, ct).Ok();

        var url = $"/api/v1/oem/companies/{companyId}/outsource-emails";
        Assert.Empty((await admin.GetAsync(url, ct).Ok()).AsArray());
        var created = await admin.PostAsync(url, new { email = " buyer@example.invalid " }, ct).Ok();
        Assert.Equal("buyer@example.invalid", created["email"]!.GetValue<string>());
        await admin.PostAsync(url, new { email = "second@example.invalid" }, ct).Ok();
        await admin.PostAsync(url, new { email = "BUYER@example.invalid" }, ct).Status(HttpStatusCode.Conflict);
        await admin.PostAsync(url, new { email = "not-an-address" }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PostAsync(url, new { email = "" }, ct).Status(HttpStatusCode.BadRequest);

        // The contacts are untouched and the two lists stay apart.
        var contactEmails = (await admin.GetAsync($"/api/v1/oem/companies/{companyId}/contacts", ct).Ok()).AsArray()
            .Select(x => x!["email"]?.GetValue<string>()).ToArray();
        Assert.Contains("odm@example.invalid", contactEmails);
        Assert.DoesNotContain("buyer@example.invalid", contactEmails);
        var id = created.Id();
        await admin.PutAsync($"/api/v1/oem/outsource-emails/{id}", new { email = "second@example.invalid" }, ct).Status(HttpStatusCode.Conflict);
        await admin.PutAsync($"/api/v1/oem/outsource-emails/{id}", new { email = "changed@example.invalid" }, ct).Ok();
        await admin.DeleteAsync($"/api/v1/oem/outsource-emails/{id}", ct).Ok();
        Assert.Equal(["second@example.invalid"], (await admin.GetAsync(url, ct).Ok()).AsArray().Select(x => x!["email"]!.GetValue<string>()));

        // Like the contacts, the addresses need oem:company_manage; vendor accounts never reach them.
        await host.CreateInternalUserAsync("os_mail_viewer", "Viewer#2026", ["oem", "oem:directory_view"], null, ct);
        var viewer = await host.LoginInternalAsync("os_mail_viewer", "Viewer#2026", ct);
        await viewer.GetAsync(url, ct).Status(HttpStatusCode.Forbidden);
        await viewer.PostAsync(url, new { email = "x@example.invalid" }, ct).Status(HttpStatusCode.Forbidden);
        await vendor.GetAsync(url, ct).Status(HttpStatusCode.Forbidden);
    }
}
