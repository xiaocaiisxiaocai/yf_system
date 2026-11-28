using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests(AnnouncementFixture w) : IClassFixture<AnnouncementFixture>
{
    private const string Manage = "/api/v1/announcements/manage";
    private const string Internal = "/api/v1/announcements";
    private const string External = "/api/v1/oem/announcements";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DefaultAudienceIncludesDepartmentAndSections()
    {
        await w.EnsureAsync(Ct);
        var options = await w.Author.GetAsync($"{Manage}/options", Ct).Ok();
        Assert.Equal(w.Department, options.Id("departmentId"));
        var id = await Publish(w.Author, "Default audience", companies: [w.Company]);
        await Visible(w.DepartmentReader, Internal, id, "Default audience");
        await Visible(w.SectionReader, Internal, id, "Default audience");
        await Visible(w.Vendor, External, id, "Default audience");
        await Hidden(w.OtherStaff, Internal, id, "Default audience");
        await Hidden(w.OtherVendor, External, id, "Default audience");
    }

    [Fact]
    public async Task InternalAndExternalSelectionsAreIndependent()
    {
        await w.EnsureAsync(Ct);
        var staffWide = await Publish(w.Author, "Internal wide", "ALL", companies: [w.Company]);
        await Visible(w.OtherStaff, Internal, staffWide, "Internal wide");
        await Hidden(w.OtherVendor, External, staffWide, "Internal wide");
        var vendorWide = await Publish(w.Author, "External wide", "SELECTED", "ALL", [w.OtherDepartment, w.ThirdDepartment]);
        await Visible(w.OtherStaff, Internal, vendorWide, "External wide");
        await Visible(w.ThirdStaff, Internal, vendorWide, "External wide");
        await Hidden(w.SectionReader, Internal, vendorWide, "External wide");
        await Visible(w.OtherVendor, External, vendorWide, "External wide");
    }

    [Fact]
    public async Task EmptySelectionsPreventPublicationAndZeroAccountCompanyIsNamed()
    {
        await w.EnsureAsync(Ct);
        var options = await w.Author.GetAsync($"{Manage}/options", Ct).Ok();
        var company = Assert.Single(options["companies"]!.AsArray(), c => c!.Id() == w.EmptyCompany)!;
        Assert.Equal("公告零账号厂商", company["name"]!.GetValue<string>());
        Assert.Equal(0, company["accountCount"]!.GetValue<int>());
        foreach (var invalid in new[] {
            Draft("No department", "SELECTED", "SELECTED", companies: [w.Company]),
            Draft("No company", "ALL", "SELECTED"), Draft("No external audience", "ALL", null) })
        {
            var draft = await w.Author.PostAsync(Manage, invalid, Ct).Ok();
            await w.Author.PostAsync($"{Manage}/{draft.Id()}/publish?version={draft.Id("version")}", null, Ct)
                .Status(HttpStatusCode.BadRequest);
            Assert.Equal("DRAFT", (await w.Author.GetAsync($"{Manage}/{draft.Id()}", Ct).Ok())["status"]!.GetValue<string>());
        }

        // Simulate a directory with no enabled company, then restore this class's shared fixture.
        var all = await w.Author.PostAsync(Manage, Draft("All with empty directory", "ALL", "ALL"), Ct).Ok();
        await using var conn = await w.Host.OpenAsync(Ct);
        var ids = (await conn.QueryAsync<ulong>("SELECT id FROM oem_companies WHERE status='ACTIVE'")).ToArray();
        try
        {
            await conn.ExecuteAsync("UPDATE oem_companies SET status='DISABLED' WHERE id IN @ids", new { ids });
            await w.Author.PostAsync($"{Manage}/{all.Id()}/publish?version={all.Id("version")}", null, Ct)
                .Status(HttpStatusCode.BadRequest);
        }
        finally
        {
            await conn.ExecuteAsync("UPDATE oem_companies SET status='ACTIVE' WHERE id IN @ids", new { ids });
        }
    }

    [Fact]
    public async Task SuccessfulOpensRecordAccountAndTimestampsOnce()
    {
        await w.EnsureAsync(Ct);
        var id = await Publish(w.Author, "Read isolation", companies: [w.Company]);
        await w.Author.GetAsync($"{Manage}/{id}", Ct).Ok();
        await Visible(w.Vendor, External, id, "Read isolation");
        Assert.Empty(await Reads(w.Author, id));
        var detail = await w.Vendor.GetAsync($"{External}/{id}", Ct).Ok();
        Assert.Null(detail["companies"]);
        Assert.Null(detail["departments"]);
        await Hidden(w.OtherVendor, External, id, "Read isolation");
        await w.OtherVendor.PostAsync($"{External}/{id}/read", null, Ct).Status(HttpStatusCode.NotFound);
        await Denied(w.OtherVendor.GetAsync($"{Manage}/{id}/reads", Ct));
        await w.Vendor.PostAsync($"{External}/{id}/read", null, Ct).Ok();
        var first = Assert.Single(await Reads(w.Author, id))!;
        Assert.Equal("ann_vendor_a", first["accountName"]!.GetValue<string>());
        var firstAt = first["firstReadAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal(firstAt, first["lastReadAt"]!.GetValue<DateTimeOffset>());
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => w.Vendor.PostAsync($"{External}/{id}/read", null, Ct).Ok()));
        var repeated = Assert.Single(await Reads(w.Author, id))!;
        Assert.Equal(firstAt, repeated["firstReadAt"]!.GetValue<DateTimeOffset>());
        Assert.True(repeated["lastReadAt"]!.GetValue<DateTimeOffset>() > firstAt);
        await w.SectionReader.GetAsync($"{Internal}/{id}", Ct).Ok();
        await w.SectionReader.PostAsync($"{Internal}/{id}/read", null, Ct).Ok();
        Assert.Equal(2, (await Reads(w.Author, id)).Count);
    }

    [Fact]
    public async Task InboxShowsOnlyAnExcerptToEligibleReadersWithoutRecordingAnOpen()
    {
        await w.EnsureAsync(Ct);
        var prefix = new string('甲', 159) + "📦";
        var body = prefix + "完整公告的后续内容";
        const string title = "Inline preview";
        var draft = await w.Author.PostAsync(Manage, new
        {
            title, body, internalScope = "OWN", externalScope = "SELECTED", companyIds = new[] { w.Company }
        }, Ct).Ok();
        var id = draft.Id();
        await w.Author.PostAsync($"{Manage}/{id}/publish?version={draft.Id("version")}", null, Ct).Ok();
        foreach (var (reader, route) in new[] { (w.SectionReader, Internal), (w.Vendor, External) })
        {
            var row = Assert.Single((await List(reader, route, title))["list"]!.AsArray())!;
            Assert.Equal(prefix + "…", row["preview"]!.GetValue<string>());
            Assert.Null(row["body"]);
            Assert.False(row["isRead"]!.GetValue<bool>());
            Assert.Equal(body, (await reader.GetAsync($"{route}/{id}", Ct).Ok())["body"]!.GetValue<string>());
        }
        await Hidden(w.OtherVendor, External, id, title);
        Assert.Empty(await Reads(w.Author, id));
    }

    [Fact]
    public async Task WithdrawalRequiresOriginalPublisherAndIndependentCurrentPermission()
    {
        await w.EnsureAsync(Ct);
        var author = await w.Staff("ann_revoke", w.Department, AnnouncementFixture.AllPermissions, Ct);
        var id = await Publish(author, "Author revoke", companies: [w.Company]);
        await Denied(w.Peer.PostAsync($"{Manage}/{id}/withdraw", null, Ct));
        await w.SetGrant("ann_revoke", "announcement:withdraw", false, Ct);
        await Denied(author.PostAsync($"{Manage}/{id}/withdraw", null, Ct));
        await w.SetGrant("ann_revoke", "announcement:publish", false, Ct);
        await w.SetGrant("ann_revoke", "announcement:withdraw", true, Ct);
        await Denied(author.PostAsync(Manage, Draft("No publish", "ALL", "ALL"), Ct));
        await author.PostAsync($"{Manage}/{id}/withdraw", null, Ct).Ok();
        await Hidden(w.Vendor, External, id, "Author revoke");
    }

    [Fact]
    public async Task PinAndReadDetailRightsAreIndependentAndScoped()
    {
        await w.EnsureAsync(Ct);
        var id = await Publish(w.Author, "Separate grants", companies: [w.Company]);
        var other = await Publish(w.OtherAuthor, "Other managed org", companies: [w.Company]);
        await w.PinManager.PostAsync($"{Manage}/{id}/pin", null, Ct).Ok();
        await w.PinManager.PostAsync($"{Manage}/{id}/unpin", null, Ct).Ok();
        await Denied(w.PinManager.GetAsync($"{Manage}/{id}/reads", Ct));
        await w.ReadManager.GetAsync($"{Manage}/{id}/reads", Ct).Ok();
        await Denied(w.ReadManager.PostAsync($"{Manage}/{id}/pin", null, Ct));
        await Denied(w.PinManager.PostAsync($"{Manage}/{other}/pin", null, Ct));
        await Denied(w.ReadManager.GetAsync($"{Manage}/{other}/reads", Ct));
        await w.Author.PostAsync($"{Manage}/{id}/withdraw", null, Ct).Ok();
        await w.ReadManager.GetAsync($"{Manage}/{id}/reads", Ct).Ok();
    }

    [Fact]
    public async Task MultiplePinsPreserveVisibilityAndOriginalReadState()
    {
        await w.EnsureAsync(Ct);
        const string keyword = "Pin ordering";
        var a = await Publish(w.Author, keyword + " A", companies: [w.Company]);
        var b = await Publish(w.Author, keyword + " B", companies: [w.Company]);
        var c = await Publish(w.Author, keyword + " C", companies: [w.Company]);
        await w.Vendor.GetAsync($"{External}/{a}", Ct).Ok();
        await w.Vendor.PostAsync($"{External}/{a}/read", null, Ct).Ok();
        await w.Author.PostAsync($"{Manage}/{b}/pin", null, Ct).Ok();
        await w.Author.PostAsync($"{Manage}/{a}/pin", null, Ct).Ok();
        Assert.Equal(new[] { a, b, c }, Ids(await List(w.Vendor, External, keyword)));
        await w.Author.PostAsync($"{Manage}/{a}/unpin", null, Ct).Ok();
        var unpinned = await List(w.Vendor, External, keyword);
        Assert.Equal(new[] { b, c, a }, Ids(unpinned));
        Assert.True(unpinned["list"]!.AsArray().Single(x => x!.Id() == a)!["isRead"]!.GetValue<bool>());
        await Hidden(w.OtherVendor, External, b, keyword);
    }

    [Fact]
    public async Task WithdrawalRemovesSearchUnreadAndDirectAccessAndFreezesReads()
    {
        await w.EnsureAsync(Ct);
        const string title = "Withdrawal from recipients";
        var id = await Publish(w.Author, title, "ALL", "ALL");
        await w.Author.PostAsync($"{Manage}/{id}/pin", null, Ct).Ok();
        await w.Vendor.GetAsync($"{External}/{id}", Ct).Ok();
        await w.Vendor.PostAsync($"{External}/{id}/read", null, Ct).Ok();
        var before = (await Reads(w.Author, id)).ToJsonString();
        var unread = (await List(w.OtherVendor, External, title))["unreadCount"]!.GetValue<int>();
        await w.Author.PostAsync($"{Manage}/{id}/withdraw", null, Ct).Ok();
        await Hidden(w.Vendor, External, id, title);
        await Hidden(w.OtherVendor, External, id, title);
        await Hidden(w.SectionReader, Internal, id, title);
        Assert.Equal(unread - 1, (await List(w.OtherVendor, External, title))["unreadCount"]!.GetValue<int>());
        await w.Vendor.PostAsync($"{External}/{id}/read", null, Ct).Status(HttpStatusCode.NotFound);
        Assert.Equal(before, (await Reads(w.Author, id)).ToJsonString());
        var archive = await w.Author.GetAsync($"{Manage}/{id}", Ct).Ok();
        Assert.Equal("WITHDRAWN", archive["status"]!.GetValue<string>());
        Assert.Null(archive["pinnedAt"]);
        Assert.NotNull(archive["withdrawnAt"]);
        Assert.NotNull(archive["withdrawnByName"]);
        Assert.Equal("Notice: " + title, archive["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task NewlyAddedCompanyAndAccountsSeeOnlyTheirLiveAudience()
    {
        await w.EnsureAsync(Ct);
        const string keyword = "Dynamic audience";
        var all = await Publish(w.Author, keyword + " all", "ALL", "ALL");
        var selected = await Publish(w.Author, keyword + " selected", companies: [w.Company]);
        var removed = await Publish(w.Author, keyword + " removed", "ALL", "ALL");
        await w.Author.PostAsync($"{Manage}/{removed}/withdraw", null, Ct).Ok();
        var newcomer = await w.Host.CreateVendorAsync(w.Admin, "公告后加入厂商", "ann_vendor_new", Ct);
        Assert.Equal(new[] { all }, Ids(await List(newcomer, External, keyword)));
        await Hidden(newcomer, External, selected, keyword);
        await Hidden(newcomer, External, removed, keyword);
        var account = await w.AddVendorAccount("ann_vendor_a2", Ct);
        Assert.Equal(new[] { selected, all }, Ids(await List(account, External, keyword)));
        await account.GetAsync($"{External}/{selected}", Ct).Ok();
        await account.PostAsync($"{External}/{selected}/read", null, Ct).Ok();
        await using (var conn = await w.Host.OpenAsync(Ct))
            await conn.ExecuteAsync("UPDATE oem_accounts SET status='DISABLED' WHERE employee_no='ann_vendor_a2'");
        await Denied(account.GetAsync($"{External}/{selected}", Ct));
        Assert.Equal("ann_vendor_a2", Assert.Single(await Reads(w.Author, selected))!["accountName"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorrectedCopyHasIndependentReadsAndReadersCannotManage()
    {
        await w.EnsureAsync(Ct);
        var original = await Publish(w.Author, "Original correction", companies: [w.Company]);
        await w.Vendor.GetAsync($"{External}/{original}", Ct).Ok();
        await w.Vendor.PostAsync($"{External}/{original}/read", null, Ct).Ok();
        await w.Author.PostAsync($"{Manage}/{original}/withdraw", null, Ct).Ok();
        var copy = await Publish(w.Author, "Reissued correction", companies: [w.Company]);
        Assert.NotEqual(original, copy);
        Assert.False((await w.Vendor.GetAsync($"{External}/{copy}", Ct).Ok())["isRead"]!.GetValue<bool>());
        Assert.Single(await Reads(w.Author, original));
        Assert.Empty(await Reads(w.Author, copy));
        foreach (var reader in new[] { w.SectionReader, w.Vendor })
        {
            await Denied(reader.PostAsync(Manage, Draft("Unauthorized", "ALL", "ALL"), Ct));
            await Denied(reader.PostAsync($"{Manage}/{copy}/publish", null, Ct));
            await Denied(reader.PostAsync($"{Manage}/{copy}/withdraw", null, Ct));
            await Denied(reader.GetAsync($"{Manage}/{copy}/reads", Ct));
        }
    }

    private static object Draft(string title, string internalScope, string? externalScope, ulong[]? departments = null,
        ulong[]? companies = null) => new
        { title, body = "Notice: " + title, internalScope, externalScope, departmentIds = departments ?? [], companyIds = companies ?? [] };

    private static async Task<ulong> Publish(ApiClient client, string title, string internalScope = "OWN",
        string externalScope = "SELECTED", ulong[]? departments = null, ulong[]? companies = null)
    {
        var draft = await client.PostAsync(Manage, Draft(title, internalScope, externalScope, departments, companies), Ct).Ok();
        var id = draft.Id();
        await client.PostAsync($"{Manage}/{id}/publish?version={draft.Id("version")}", null, Ct).Ok();
        return id;
    }

    private static Task<JsonNode> List(ApiClient client, string route, string keyword) =>
        client.GetAsync($"{route}?keyword={Uri.EscapeDataString(keyword)}&page=1&pageSize=100", Ct).Ok();

    private static ulong[] Ids(JsonNode page) => page["list"]!.AsArray().Select(row => row!.Id()).ToArray();

    private static async Task Visible(ApiClient client, string route, ulong id, string keyword)
    {
        Assert.Contains(id, Ids(await List(client, route, keyword)));
        Assert.Equal(id, (await client.GetAsync($"{route}/{id}", Ct).Ok()).Id());
    }

    private static async Task Hidden(ApiClient client, string route, ulong id, string keyword)
    {
        Assert.DoesNotContain(id, Ids(await List(client, route, keyword)));
        await client.GetAsync($"{route}/{id}", Ct).Status(HttpStatusCode.NotFound);
    }

    private static async Task<JsonArray> Reads(ApiClient manager, ulong id) =>
        (await manager.GetAsync($"{Manage}/{id}/reads", Ct).Ok())["list"]!.AsArray();

    private static async Task Denied(Task<ApiResponse> request)
    {
        var result = await request;
        Assert.True(result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
            $"Expected rejected access, got {(int)result.Status}: {result.Body}");
    }
}
