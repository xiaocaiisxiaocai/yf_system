using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Common;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemTransferOutsourceLinkTests
{
    private const string Transfers = "/api/v1/oem/transfers";
    private const string SendableItems = "/api/v1/oem/sendable-items";

    [Fact(Timeout = 180_000)]
    public async Task InternalCreateDerivesVendorFromItem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-DERIVE", ct);

        var detail = await world.Sender.PostAsync(Transfers, new { outsourceItemId = itemId }, ct).Ok();
        Assert.Equal(world.CompanyId, detail["summary"]!.Id("companyId"));
        Assert.Equal(itemId, detail["summary"]!.Id("outsourceItemId"));
        Assert.Equal("WO-DERIVE", detail["summary"]!["workOrderNo"]!.GetValue<string>());

        await using var connection = await host.OpenAsync(ct);
        var audit = JsonNode.Parse(await connection.QuerySingleAsync<string>(
            "SELECT detail FROM audit_logs WHERE action='OEM_TRANSFER_CREATE' AND target_id=@targetId",
            new { targetId = TransferId(detail).ToString(System.Globalization.CultureInfo.InvariantCulture) }))!;
        Assert.Equal(itemId, audit.Id("outsourceItemId"));
        Assert.Equal("WO-DERIVE", audit["workOrderNo"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalCreateRequiresItem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);

        var response = await world.Sender.PostAsync(Transfers, new { oemCompanyId = world.CompanyId }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("请选择外协清单中的工令", response.Json!["message"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalCreateRejectsCompletedItem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-COMPLETED", ct, closed: true);

        await world.Sender.PostAsync(Transfers, new { oemCompanyId = world.CompanyId, outsourceItemId = itemId }, ct)
            .Status(HttpStatusCode.BadRequest);
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalCreateRejectsConflictingCompany()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherCompanyId = await CreateCompanyAsync(world.Admin, "另一厂商", ct);
        await world.Admin.PostAsync($"/api/v1/oem/companies/{otherCompanyId}/accounts",
            new { employeeNo = "other_recipient", password = "Other#2026" }, ct).Ok();
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-CONFLICT", ct);

        await world.Sender.PostAsync(Transfers, new { oemCompanyId = otherCompanyId, outsourceItemId = itemId }, ct)
            .Status(HttpStatusCode.BadRequest);
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorCreateRejectsOtherCompanyItem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherCompanyId = await CreateCompanyAsync(world.Admin, "另一厂商", ct);
        var otherItemId = await host.CreateOutsourceItemAsync(otherCompanyId, "WO-OTHER", ct);
        var ownItemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-OWN", ct);

        await world.Vendor.PostAsync(Transfers, new { outsourceItemId = otherItemId }, ct).Status(HttpStatusCode.BadRequest);
        var detail = await world.Vendor.PostAsync(Transfers, new { outsourceItemId = ownItemId }, ct).Ok();
        Assert.Equal(world.CompanyId, detail["summary"]!.Id("companyId"));
        Assert.Equal(ownItemId, detail["summary"]!.Id("outsourceItemId"));
        Assert.Equal("WO-OWN", detail["summary"]!["workOrderNo"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task ListSearchMatchesWorkOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-SEARCHABLE", ct);
        var otherItemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-OTHER", ct);
        var target = await world.Sender.PostAsync(Transfers,
            new { title = "图纸", oemCompanyId = world.CompanyId, outsourceItemId = itemId }, ct).Ok();
        var other = await world.Sender.PostAsync(Transfers,
            new { title = "图纸", oemCompanyId = world.CompanyId, outsourceItemId = otherItemId }, ct).Ok();
        foreach (var draft in new[] { target, other })
        {
            var transferId = TransferId(draft);
            await host.UploadAsync(world.Sender, transferId, "drawing.pdf", OemTestHost.Pdf("work order search"), ct);
            await world.Sender.PostAsync($"{Transfers}/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        }

        var page = await world.Sender.GetAsync(Transfers + "?keyword=SEARCHABLE", ct).Ok();
        var row = Assert.Single(page["list"]!.AsArray())!;
        Assert.Equal(TransferId(target), row.Id());
        Assert.Equal(itemId, row.Id("outsourceItemId"));
        Assert.Equal("WO-SEARCHABLE", row["workOrderNo"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsListsOnlyOpenItemsAndExplainsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var noAccount = await CreateCompanyAsync(world.Admin, "无账号厂商", ct);
        var disabled = await CreateCompanyAsync(world.Admin, "停用厂商", ct);
        await world.Admin.PutAsync($"/api/v1/oem/companies/{disabled}/status", new { status = "DISABLED" }, ct).Ok();
        var available = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-AVAILABLE", ct, customer: "测试客户");
        var unavailable = await host.CreateOutsourceItemAsync(noAccount, "WO-NO-ACCOUNT", ct);
        var closed = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-CLOSED", ct, closed: true);
        var disabledItem = await host.CreateOutsourceItemAsync(disabled, "WO-DISABLED", ct);

        await world.Viewer.GetAsync(SendableLookup("WO-"), ct).Status(HttpStatusCode.Forbidden);
        var rows = (await world.Sender.GetAsync(SendableLookup("WO-"), ct).Ok()).AsArray();
        var listedIds = rows.Select(row => row!.Id()).ToArray();
        Assert.DoesNotContain(closed, listedIds);
        Assert.DoesNotContain(disabledItem, listedIds);
        var ready = Assert.Single(rows, row => row!.Id() == available)!;
        Assert.Equal("WO-AVAILABLE", ready["workOrderNo"]!.GetValue<string>());
        Assert.Equal("测试客户", ready["customer"]!.GetValue<string>());
        Assert.Equal("精工代工", ready["vendorName"]!.GetValue<string>());
        Assert.Null(ready["disabledReason"]);
        var blocked = Assert.Single(rows, row => row!.Id() == unavailable)!;
        Assert.Equal(OemRecipientPolicy.MissingAccountMessage, blocked["disabledReason"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorSendableItemsAreOwnCompanyOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherCompanyId = await CreateCompanyAsync(world.Admin, "另一厂商", ct);
        var own = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-VENDOR", ct, customer: "测试客户");
        var closed = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-VENDOR-CLOSED", ct, closed: true);
        var other = await host.CreateOutsourceItemAsync(otherCompanyId, "WO-VENDOR-OTHER", ct);

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=1 WHERE field_key='customer'");
        var rows = (await world.Vendor.GetAsync(SendableLookup("WO-VENDOR"), ct).Ok()).AsArray();
        var listedIds = rows.Select(row => row!.Id()).ToArray();
        Assert.DoesNotContain(closed, listedIds);
        Assert.DoesNotContain(other, listedIds);
        var item = Assert.Single(rows, row => row!.Id() == own)!;
        Assert.Equal("WO-VENDOR", item["workOrderNo"]!.GetValue<string>());
        Assert.Equal("测试客户", item["customer"]!.GetValue<string>());
        Assert.Null(item["disabledReason"]);

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='customer'");
        rows = (await world.Vendor.GetAsync(SendableLookup("WO-VENDOR"), ct).Ok()).AsArray();
        Assert.Null(Assert.Single(rows, row => row!.Id() == own)!["customer"]);
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsRequireWorkOrderText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-OPEN", ct);

        foreach (var url in new[] { SendableItems, SendableLookup(""), SendableLookup(" \t ") })
        {
            Assert.Empty((await world.Sender.GetAsync(url, ct).Ok()).AsArray());
            Assert.Empty((await world.Vendor.GetAsync(url, ct).Ok()).AsArray());
        }
        await world.Viewer.GetAsync(SendableItems, ct).Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsMatchContainsAndPutExactFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        foreach (var number in new[] { "Z-WO-SEARCH", "WO-OTHER", "WO-SEARCH-2", "A-WO-SEARCH", "WO-SEARCH" })
            await host.CreateOutsourceItemAsync(world.CompanyId, number, ct);

        var rows = (await world.Sender.GetAsync(SendableLookup(" wo-search "), ct).Ok()).AsArray();

        Assert.Equal(["WO-SEARCH", "A-WO-SEARCH", "WO-SEARCH-2", "Z-WO-SEARCH"], WorkOrderNumbers(rows));
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsEscapeLikeWildcards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-PLAIN", ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-%-LITERAL", ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-_-LITERAL", ct);

        foreach (var symbol in new[] { "%", "_" })
        {
            var rows = (await world.Sender.GetAsync(SendableLookup(symbol), ct).Ok()).AsArray();
            Assert.Equal($"WO-{symbol}-LITERAL", Assert.Single(WorkOrderNumbers(rows)));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsAreCappedAtTwenty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var numbers = Enumerable.Range(1, 25).Select(i => $"{i:D2}-WO-LIMIT").ToArray();
        foreach (var number in numbers.Reverse())
            await host.CreateOutsourceItemAsync(world.CompanyId, number, ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-LIMIT", ct);

        var url = SendableLookup("WO-LIMIT");
        var expected = new[] { "WO-LIMIT" }.Concat(numbers.Take(19)).ToArray();
        Assert.Equal(expected, WorkOrderNumbers((await world.Sender.GetAsync(url, ct).Ok()).AsArray()));
        Assert.Equal(expected, WorkOrderNumbers((await world.Vendor.GetAsync(url, ct).Ok()).AsArray()));
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorSendableLookupStaysOwnCompany()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherCompany = await CreateCompanyAsync(world.Admin, "另一厂商", ct);
        await host.CreateOutsourceItemAsync(otherCompany, "WO-HIDDEN", ct);
        await host.CreateOutsourceItemAsync(world.CompanyId, "WO-HIDDEN-OWN", ct);

        var rows = (await world.Vendor.GetAsync(SendableLookup("wo-hidden"), ct).Ok()).AsArray();
        Assert.Equal("WO-HIDDEN-OWN", Assert.Single(WorkOrderNumbers(rows)));
    }

    [Fact(Timeout = 180_000)]
    public async Task SendableItemsShowTheCustomerShortNameLikeTheWorkOrderList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var item = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-ALIAS", ct, customer: "某某科技股份有限公司");
        await using (var connection = await host.OpenAsync(ct))
        {
            await connection.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=1 WHERE field_key='customer'");
            await connection.ExecuteAsync("INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at) VALUES('某某科技股份有限公司','某某',1,UTC_TIMESTAMP(3))");
        }

        foreach (var client in new[] { world.Vendor, world.Sender })
        {
            var row = Assert.Single((await client.GetAsync(SendableLookup("WO-ALIAS"), ct).Ok()).AsArray(), x => x!.Id() == item)!;
            Assert.Equal("某某", row["customer"]!.GetValue<string>());
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task SendRejectsWorkOrderCompletedAfterDraftCreation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-CLOSE-BEFORE-SEND", ct);

        var internalDraft = await world.Sender.PostAsync(Transfers, new { title = "内发草稿", outsourceItemId = itemId }, ct).Ok();
        var internalId = TransferId(internalDraft);
        await host.UploadAsync(world.Sender, internalId, "drawing.pdf", OemTestHost.Pdf("completed check"), ct);
        internalDraft = await world.Sender.GetAsync($"{Transfers}/{internalId}", ct).Ok();

        var vendorDraft = await world.Vendor.PostAsync(Transfers, new { title = "厂商草稿", outsourceItemId = itemId }, ct).Ok();
        var vendorId = TransferId(vendorDraft);
        await host.UploadAsync(world.Vendor, vendorId, "return.pdf", OemTestHost.Pdf("vendor completed check"), ct);
        vendorDraft = await world.Vendor.GetAsync($"{Transfers}/{vendorId}", ct).Ok();

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_items SET closed_at=UTC_TIMESTAMP(3) WHERE id=@itemId", new { itemId });

        var internalRejected = await world.Sender.PostAsync($"{Transfers}/{internalId}/send", new { version = Version(internalDraft) }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("所选工令已完成，不能发送传递单", internalRejected.Json!["message"]!.GetValue<string>());
        var internalAfter = await world.Sender.GetAsync($"{Transfers}/{internalId}", ct).Ok();
        Assert.Equal("DRAFT", internalAfter["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal(Version(internalDraft), Version(internalAfter));

        var vendorRejected = await world.Vendor.VendorSendAsync(vendorId, Version(vendorDraft), ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("所选工令已完成，不能发送传递单", vendorRejected.Json!["message"]!.GetValue<string>());
        var vendorAfter = await world.Vendor.GetAsync($"{Transfers}/{vendorId}", ct).Ok();
        Assert.Equal("DRAFT", vendorAfter["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal(Version(vendorDraft), Version(vendorAfter));
    }

    [Fact(Timeout = 180_000)]
    public async Task SendRejectsWorkOrderReassignedToAnotherVendorAfterDraftCreation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherCompanyId = await CreateCompanyAsync(world.Admin, "改派厂商", ct);
        var itemId = await host.CreateOutsourceItemAsync(world.CompanyId, "WO-REASSIGN-BEFORE-SEND", ct);

        var internalDraft = await world.Sender.PostAsync(Transfers, new { title = "内发改派", outsourceItemId = itemId }, ct).Ok();
        var internalId = TransferId(internalDraft);
        await host.UploadAsync(world.Sender, internalId, "drawing.pdf", OemTestHost.Pdf("reassigned check"), ct);
        internalDraft = await world.Sender.GetAsync($"{Transfers}/{internalId}", ct).Ok();

        var vendorDraft = await world.Vendor.PostAsync(Transfers, new { title = "厂商改派", outsourceItemId = itemId }, ct).Ok();
        var vendorId = TransferId(vendorDraft);
        await host.UploadAsync(world.Vendor, vendorId, "return.pdf", OemTestHost.Pdf("vendor reassigned check"), ct);
        vendorDraft = await world.Vendor.GetAsync($"{Transfers}/{vendorId}", ct).Ok();

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_items SET vendor_id=@otherCompanyId WHERE id=@itemId", new { otherCompanyId, itemId });

        var internalRejected = await world.Sender.PostAsync($"{Transfers}/{internalId}/send", new { version = Version(internalDraft) }, ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("所选工令的外发厂商已变更，不能发送传递单", internalRejected.Json!["message"]!.GetValue<string>());
        var internalAfter = await world.Sender.GetAsync($"{Transfers}/{internalId}", ct).Ok();
        Assert.Equal("DRAFT", internalAfter["summary"]!["lifecycleStatus"]!.GetValue<string>());

        var vendorRejected = await world.Vendor.VendorSendAsync(vendorId, Version(vendorDraft), ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Equal("所选工令的外发厂商已变更，不能发送传递单", vendorRejected.Json!["message"]!.GetValue<string>());
        var vendorAfter = await world.Vendor.GetAsync($"{Transfers}/{vendorId}", ct).Ok();
        Assert.Equal("DRAFT", vendorAfter["summary"]!["lifecycleStatus"]!.GetValue<string>());
    }

    private static string SendableLookup(string text) => $"{SendableItems}?workOrderNo={Uri.EscapeDataString(text)}";

    private static string[] WorkOrderNumbers(JsonArray rows) =>
        rows.Select(row => row!["workOrderNo"]!.GetValue<string>()).ToArray();

    private static async Task<ulong> CreateCompanyAsync(ApiClient admin, string name, CancellationToken ct) =>
        (await admin.PostAsync("/api/v1/oem/companies", new { name }, ct).Ok()).Id();
}
