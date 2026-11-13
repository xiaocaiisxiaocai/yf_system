using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

/// <summary>The outsourcing list reads IPMS data from the MES mirror, never from the network.</summary>
public sealed class MesMirrorIpmsClientTests
{
    [Fact]
    public void MapsTheConfirmedFieldsAndLeavesTheRestEmpty()
    {
        var order = MesMirrorIpmsClient.Map("21091052",
            """{"workOrder":"21091052","customerName":"Peng Shen (Thailand)","deviceModels":"SFC-1500","deviceNum":3,"customerEndDate":"2027-01-08"}""");

        Assert.Equal(new IpmsWorkOrder("21091052", "Peng Shen (Thailand)", "SFC-1500", null, null, null, null, null, null,
            3, new DateOnly(2027, 1, 8), false), order);
    }

    [Theory]
    [InlineData("""{"deviceNum":"2","customerEndDate":"2026-12-30 00:00:00"}""", 2, "2026-12-30")]
    [InlineData("""{"deviceNum":"x","customerEndDate":"garbage"}""", null, null)]
    [InlineData("""{"deviceNum":null,"customerEndDate":null}""", null, null)]
    [InlineData("""{}""", null, null)]
    public void ToleratesMissingOrMalformedValues(string json, int? quantity, string? due)
    {
        var order = MesMirrorIpmsClient.Map("W1", json);
        Assert.Equal((quantity, due is null ? (DateOnly?)null : DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture)),
            (order.Quantity, order.DueDate));
        Assert.False(order.Closed);
    }

    [Fact]
    public void FactoryKeepsTheFakeMode()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var fake = new AppOptions { Ipms = new IpmsOptions { Mode = "Fake", FakeDataPath = Path.Combine(Path.GetTempPath(), "fake.json") } };
        Assert.IsType<FakeIpmsClient>(IpmsClientFactory.Create(fake, provider));
    }

    [Fact(Timeout = 180_000)]
    public async Task LooksUpOnlyTheRequestedWorkOrdersFromTheMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_client", ct);
        await database.InitializeAsync(ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var options = new AppOptions { JwtSecret = "test-secret-for-the-ipms-password-0123456789" };
        var settings = new DbMesSettings(factory, options);
        var services = new ServiceCollection().AddSingleton<IDbContextFactory<YfDbContext>>(factory)
            .AddSingleton<IMesSettings>(settings).BuildServiceProvider();
        // With no mode configured, IPMS data comes from the MES module once the page has set the connection.
        var client = IpmsClientFactory.Create(options, services);
        Assert.IsType<MesMirrorIpmsClient>(client);
        Assert.False(await client.IsConfiguredAsync(ct));
        await database.ExecuteAsync($"INSERT INTO mes_mirror_state(id,base_url,account,protected_password) VALUES(1,'http://127.0.0.1:6001','a','{settings.Protect("p")}')", ct);
        Assert.True(await client.IsConfiguredAsync(ct));

        // Before anything was fetched, lookups fail instead of reporting every work order missing.
        var notYet = await Assert.ThrowsAsync<IpmsUnavailableException>(() => client.LookupAsync(["A1"], ct));
        Assert.Equal("尚未从 IPMS 抓取资料，请先在设定页抓取", notYet.Message);

        foreach (var no in new[] { "A1", "A2", "A3" })
            await database.ExecuteAsync($$"""
                INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json)
                VALUES('{{no}}','b',UTC_TIMESTAMP(3),'客户{{no}}','M-{{no}}',
                  '{"workOrder":"{{no}}","customerName":"客户{{no}}","deviceModels":"M-{{no}}","deviceNum":2,"customerEndDate":"2027-02-0{{no[1]}}"}')
                """, ct);

        var found = await client.LookupAsync(["A1", "A3", "NOPE"], ct);

        Assert.Equal(["A1", "A3"], found.Keys.Order());
        Assert.Equal(("客户A3", "M-A3", 2, new DateOnly(2027, 2, 3)), (found["A3"].Customer, found["A3"].MachineModel, found["A3"].Quantity, found["A3"].DueDate));
    }
}
