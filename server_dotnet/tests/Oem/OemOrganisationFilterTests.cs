using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemOrganisationFilterTests
{
    [Fact(Timeout = 240_000)]
    public async Task TransferOrganisationFilterUsesCurrentSenderSubtreeAndPreservesVisibilityAndPagination()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherOrg = await host.CreateOrgPathAsync("其他", ct);
        await host.CreateInternalUserAsync("dept_sender", "Sender#2026x", ["oem:transfer_create"], world.Org.Department, ct);
        await host.CreateInternalUserAsync("other_sender", "Sender#2026x", ["oem:transfer_create"], otherOrg.Section, ct);
        var departmentSender = await host.LoginInternalAsync("dept_sender", "Sender#2026x", ct);
        var otherSender = await host.LoginInternalAsync("other_sender", "Sender#2026x", ct);

        async Task<ulong> Create(ApiClient client, string title) => TransferId(await client.PostAsync("/api/v1/oem/transfers",
            new { title, oemCompanyId = world.CompanyId }, ct).Ok());
        var sectionFirst = await Create(world.Sender, "同课一");
        var sectionSecond = await Create(world.Sender, "同课二");
        var departmentDirect = await Create(departmentSender, "直属部门");
        var other = await Create(otherSender, "其他组织");
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync("UPDATE users SET department_id=@department WHERE employee_no='admin'", new { department = world.Org.Division });
        var noOrganisation = await Create(world.Admin, "没有组织");
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync("UPDATE users SET department_id=NULL WHERE employee_no='admin'");
        var inbound = TransferId(await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "厂商回传" }, ct).Ok());
        var privateDraft = await Create(world.Sender, "私人草稿");
        // This suite tests the read projection, not sending/approval. Keep one genuinely private draft.
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync("UPDATE oem_transfers SET lifecycle_status='RELEASED',sent_at=UTC_TIMESTAMP(3),released_at=UTC_TIMESTAMP(3) WHERE id IN @ids",
                new { ids = new[] { sectionFirst, sectionSecond, departmentDirect, other, noOrganisation, inbound } });

        static ulong[] Ids(JsonNode page) => page["list"]!.AsArray().Select(row => row!.Id()).Order().ToArray();
        async Task<JsonNode> Filter(ulong organisation, string extra = "") =>
            await world.Admin.GetAsync($"/api/v1/oem/transfers?departmentId={organisation}{extra}", ct).Ok();

        // A user who can only use OEM still has access to the shared organisation options.
        Assert.NotEmpty((await world.Viewer.GetAsync("/api/v1/departments", ct).Ok()).AsArray());
        // Staff holding only the view permission see none of these (they are other people's); administrators see all.
        Assert.Empty(Ids(await world.Viewer.GetAsync("/api/v1/oem/transfers", ct).Ok()));
        var all = Ids(await world.Admin.GetAsync("/api/v1/oem/transfers", ct).Ok());
        Assert.Equal(6, all.Length);
        Assert.Contains(inbound, all);
        Assert.Contains(noOrganisation, all);
        Assert.DoesNotContain(privateDraft, all);
        var subtree = new[] { sectionFirst, sectionSecond, departmentDirect }.Order().ToArray();
        Assert.Equal(subtree, Ids(await Filter(world.Org.Division)));
        Assert.Equal(subtree, Ids(await Filter(world.Org.Department)));
        Assert.Equal(new[] { sectionFirst, sectionSecond }.Order(), Ids(await Filter(world.Org.Section)));
        Assert.Equal([other], Ids(await Filter(otherOrg.Division)));
        Assert.Empty(Ids(await Filter(ulong.MaxValue)));

        var firstPage = await Filter(world.Org.Division, "&pageSize=1&page=1");
        var secondPage = await Filter(world.Org.Division, "&pageSize=1&page=2");
        Assert.Equal(3UL, firstPage["total"]!.GetValue<ulong>());
        Assert.Equal(3UL, secondPage["total"]!.GetValue<ulong>());
        Assert.Single(Ids(firstPage));
        Assert.Single(Ids(secondPage));
        Assert.NotEqual(Ids(firstPage)[0], Ids(secondPage)[0]);
        Assert.Equal([departmentDirect], Ids(await Filter(world.Org.Division, "&sender=dept_sender&direction=INTERNAL_TO_OEM")));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&direction=OEM_TO_INTERNAL")));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&companyId=999999")));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&status=ABANDONED")));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&scope=purged")));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&mine=true")));
        var tomorrow = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        Assert.Empty(Ids(await Filter(world.Org.Division, "&sentFrom=" + tomorrow)));
        // A create-only sender sees their own finished transfers (an unfinished one is never listed) but cannot gain access to other employees' transfers.
        Assert.Equal(new[] { sectionFirst, sectionSecond }.Order(),
            Ids(await world.Sender.GetAsync($"/api/v1/oem/transfers?departmentId={world.Org.Division}", ct).Ok()));

        // Membership changes affect the filter immediately; it is not a historical approval snapshot.
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync("UPDATE users SET department_id=@department WHERE id=@id", new { department = otherOrg.Section, id = world.SenderId });
        Assert.Empty(Ids(await Filter(world.Org.Section)));
        Assert.Equal([departmentDirect], Ids(await Filter(world.Org.Division)));
        Assert.Equal(new[] { sectionFirst, sectionSecond, other }.Order(), Ids(await Filter(otherOrg.Division)));

        foreach (var value in new[] { "0", "-1", "abc", "", "1&departmentId=2" })
            await world.Viewer.GetAsync("/api/v1/oem/transfers?departmentId=" + value, ct).Status(HttpStatusCode.BadRequest);
        foreach (var value in new[] { world.Org.Division.ToString(CultureInfo.InvariantCulture), ulong.MaxValue.ToString(CultureInfo.InvariantCulture), "abc", "" })
            await world.Vendor.GetAsync("/api/v1/oem/transfers?departmentId=" + value, ct).Status(HttpStatusCode.Forbidden);
    }
}
