using System.Net;
using System.Text.Json.Nodes;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests
{
    private const string Ordering = Manage + "/ordering";

    private static Task<JsonNode> OrderSnapshot(ApiClient client, bool pinned) =>
        client.GetAsync($"{Ordering}?pinned={pinned.ToString().ToLowerInvariant()}", Ct).Ok();

    private static Task<ApiResponse> ChangeOrder(ApiClient client, bool pinned, JsonNode baseline,
        params (ulong Id, int? Order)[] updates) =>
        client.PutAsync(Ordering, new
        {
            pinned,
            revision = baseline["revision"]!.GetValue<string>(),
            changes = updates.Select(update => new { id = update.Id, sortOrder = update.Order }),
        }, Ct);

    [Fact]
    public async Task RichContentSortingSpansReaderPagesAndRestoresNormalOrderAfterUnpin()
    {
        await w.EnsureAsync(Ct);
        const string prefix = "Rich cross page";
        var first = await Publish(w.Author, prefix + " A", companies: [w.Company]);
        var second = await Publish(w.Author, prefix + " B", companies: [w.Company]);
        var third = await Publish(w.Author, prefix + " C", companies: [w.Company]);
        var fourth = await Publish(w.Author, prefix + " D", companies: [w.Company]);
        await w.Vendor.PostAsync($"{External}/{first}/read", null, Ct).Ok();

        async Task AssertPages(params ulong[] expected)
        {
            foreach (var (reader, route) in new[] { (w.SectionReader, Internal), (w.Vendor, External) })
            {
                var combined = new List<ulong>();
                for (var page = 1; page <= 2; page++)
                {
                    var result = await reader.GetAsync(
                        $"{route}?keyword={Uri.EscapeDataString(prefix)}&page={page}&pageSize=2", Ct).Ok();
                    Assert.Equal(4, result["total"]!.GetValue<int>());
                    combined.AddRange(Ids(result));
                }
                Assert.Equal(expected, combined);
            }
        }

        await AssertPages(fourth, third, second, first);
        var normal = await OrderSnapshot(w.PinManager, false);
        await ChangeOrder(w.PinManager, false, normal, (first, 10), (third, 20)).Ok();
        await AssertPages(first, third, fourth, second);
        await w.PinManager.PostAsync($"{Manage}/{second}/pin", null, Ct).Ok();
        await w.PinManager.PostAsync($"{Manage}/{fourth}/pin", null, Ct).Ok();
        var pinned = await OrderSnapshot(w.PinManager, true);
        await ChangeOrder(w.PinManager, true, pinned, (second, 20), (fourth, 10)).Ok();
        await AssertPages(fourth, second, first, third);
        // A pinned copy of a previously ordered row keeps a separate position in each group.
        await w.PinManager.PostAsync($"{Manage}/{first}/pin", null, Ct).Ok();
        await ChangeOrder(w.PinManager, true, await OrderSnapshot(w.PinManager, true), (first, 5)).Ok();
        await AssertPages(first, fourth, second, third);
        await w.PinManager.PostAsync($"{Manage}/{first}/unpin", null, Ct).Ok();
        await AssertPages(fourth, second, first, third);
        var restored = (await OrderSnapshot(w.PinManager, false))["list"]!.AsArray().Single(row => row!.Id() == first)!;
        Assert.Equal(10, restored["sortOrder"]!.GetValue<int>());
        Assert.True((await w.Vendor.GetAsync($"{External}/{first}", Ct).Ok())["isRead"]!.GetValue<bool>());
        await Hidden(w.OtherVendor, External, first, prefix);
    }

    [Fact]
    public async Task RichContentConcurrentOrderingSavesRejectTheStaleRevisionAndOutOfScopeItems()
    {
        await w.EnsureAsync(Ct);
        const string prefix = "Rich order revision";
        var a = await Publish(w.Author, prefix + " A", companies: [w.Company]);
        var b = await Publish(w.Author, prefix + " B", companies: [w.Company]);
        var foreign = await Publish(w.OtherAuthor, prefix + " other", companies: [w.Company]);
        var baseline = await OrderSnapshot(w.PinManager, false);
        Assert.DoesNotContain(foreign, Ids(baseline));
        await w.ReadManager.GetAsync($"{Ordering}?pinned=false", Ct).Status(HttpStatusCode.Forbidden);
        await ChangeOrder(w.ReadManager, false, baseline, (a, 10)).Status(HttpStatusCode.Forbidden);
        await ChangeOrder(w.PinManager, false, baseline, (a, 10), (foreign, 30)).Status(HttpStatusCode.Forbidden);
        Assert.Equal(baseline["revision"]!.GetValue<string>(),
            (await OrderSnapshot(w.PinManager, false))["revision"]!.GetValue<string>());

        var results = await Task.WhenAll(
            ChangeOrder(w.PinManager, false, baseline, (a, 10)),
            ChangeOrder(w.Peer, false, baseline, (b, 5)));
        Assert.Single(results, response => response.Status == HttpStatusCode.OK);
        var conflict = Assert.Single(results, response => response.Status == HttpStatusCode.Conflict);
        Assert.Equal(40903, conflict.Code);
        var refresh = await OrderSnapshot(w.PinManager, false);
        Assert.NotEqual(baseline["revision"]!.GetValue<string>(), refresh["revision"]!.GetValue<string>());
        await ChangeOrder(w.PinManager, false, refresh, (a, 10), (b, 5)).Ok();
        // Equal numbers are broken by publication time, then clearing a number restores automatic order.
        await ChangeOrder(w.PinManager, false, await OrderSnapshot(w.PinManager, false), (b, 10)).Ok();
        Assert.Equal(new[] { b, a }, Ids(await List(w.SectionReader, Internal, prefix)));
        Assert.Equal(new[] { b, a, foreign }, Ids(await List(w.Vendor, External, prefix)));
        await ChangeOrder(w.PinManager, false, await OrderSnapshot(w.PinManager, false), (a, null)).Ok();
        Assert.Equal(new[] { b, foreign, a }, Ids(await List(w.Vendor, External, prefix)));
    }
}
