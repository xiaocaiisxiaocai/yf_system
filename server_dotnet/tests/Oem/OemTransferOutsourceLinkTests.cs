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

        await world.Viewer.GetAsync(SendableItems, ct).Status(HttpStatusCode.Forbidden);
        var rows = (await world.Sender.GetAsync(SendableItems, ct).Ok()).AsArray();
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
        var other = await host.CreateOutsourceItemAsync(otherCompanyId, "WO-OTHER", ct);

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=1 WHERE field_key='customer'");
        var rows = (await world.Vendor.GetAsync(SendableItems, ct).Ok()).AsArray();
        var listedIds = rows.Select(row => row!.Id()).ToArray();
        Assert.DoesNotContain(closed, listedIds);
        Assert.DoesNotContain(other, listedIds);
        var item = Assert.Single(rows, row => row!.Id() == own)!;
        Assert.Equal("WO-VENDOR", item["workOrderNo"]!.GetValue<string>());
        Assert.Equal("测试客户", item["customer"]!.GetValue<string>());
        Assert.Null(item["disabledReason"]);

        await using (var connection = await host.OpenAsync(ct))
            await connection.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='customer'");
        rows = (await world.Vendor.GetAsync(SendableItems, ct).Ok()).AsArray();
        Assert.Null(Assert.Single(rows, row => row!.Id() == own)!["customer"]);
    }

    private static async Task<ulong> CreateCompanyAsync(ApiClient admin, string name, CancellationToken ct) =>
        (await admin.PostAsync("/api/v1/oem/companies", new { name }, ct).Ok()).Id();
}
