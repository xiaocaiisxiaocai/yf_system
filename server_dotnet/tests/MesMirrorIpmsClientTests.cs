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
            """
            {"workOrder":"21091052","customerName":"Peng Shen (Thailand)","deviceModels":"SFC-1500","deviceNum":3,
             "bomChargeUser":"周晓淋","twodChargeUser":"周晓淋","applyEndTime":"2026-06-07","bomActualDate":"2026-06-29",
             "jjtActualDate":"2026-07-06","deliveryDate":"2027-01-08","customerEndDate":"2027-03-01","planEndDate":"2027-02-01"}
            """);

        // 机构课别 is not in MES (it comes from the owner's account) and neither is 电控执行人; the due date is MES 原交期.
        Assert.Equal(new IpmsWorkOrder("21091052", "Peng Shen (Thailand)", "SFC-1500", null, "周晓淋", null,
            new DateOnly(2026, 6, 7), new DateOnly(2026, 6, 29), new DateOnly(2026, 7, 6),
            3, new DateOnly(2027, 1, 8), false), order);
    }

    [Theory]
    [InlineData("""{"deviceNum":"2","deliveryDate":"2026-12-30 00:00:00"}""", 2, "2026-12-30")]
    [InlineData("""{"deviceNum":"x","deliveryDate":"garbage"}""", null, null)]
    [InlineData("""{"deviceNum":null,"deliveryDate":null}""", null, null)]
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

    [Theory]
    [InlineData("古才鴻", "古才鸿")]
    [InlineData("周曉淋", "周晓淋")]
    [InlineData("王小萍", "王小萍")]
    [InlineData("A-1 電控", "A-1 电控")]
    public void SimplifiesTraditionalNamesAndKeepsEverythingElse(string input, string expected) =>
        Assert.Equal(expected, ChineseVariants.Simplify(input));

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
                  '{"workOrder":"{{no}}","customerName":"客户{{no}}","deviceModels":"M-{{no}}","deviceNum":2,"deliveryDate":"2027-02-0{{no[1]}}"}')
                """, ct);

        var found = await client.LookupAsync(["A1", "A3", "NOPE"], ct);

        Assert.Equal(["A1", "A3"], found.Keys.Order());
        Assert.Equal(("客户A3", "M-A3", 2, new DateOnly(2027, 2, 3)), (found["A3"].Customer, found["A3"].MachineModel, found["A3"].Quantity, found["A3"].DueDate));

        // 机构课别 is the 课别 of the one active internal account named like the owner; no account, a company, a name used in
        // two 课别, or an account placed above 课别 level leave it empty.
        await database.ExecuteAsync("""
            INSERT INTO departments(id,name,parent_id,kind,sort_no,status,created_at,updated_at) VALUES
              (9001,'研发事业部',NULL,'DIVISION',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9002,'机构部',9001,'DEPARTMENT',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9003,'机构一课',9002,'SECTION',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9004,'机构二课',9002,'SECTION',2,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9901,'sec_a','unused','周晓淋','a@example.invalid','INTERNAL',NULL,9003,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9902,'sec_b','unused','重名','b@example.invalid','INTERNAL',NULL,9003,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9903,'sec_c','unused','重名','c@example.invalid','INTERNAL',NULL,9004,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9904,'sec_d','unused','部门级','d@example.invalid','INTERNAL',NULL,9002,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9905,'sec_e','unused','已停用','e@example.invalid','INTERNAL',NULL,9003,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9906,'sec_f','unused','古才鴻','f@example.invalid','INTERNAL',NULL,9004,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            """, ct);
        var owners = new[] { ("S1", "周晓淋"), ("S2", "重名"), ("S3", "部门级"), ("S4", "鼎亚研发"), ("S5", "已停用"), ("S6", "古才鸿") };
        foreach (var (no, owner) in owners)
            await database.ExecuteAsync($$"""
                INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json)
                VALUES('{{no}}','b',UTC_TIMESTAMP(3),'c','m','{"bomChargeUser":"{{owner}}"}')
                """, ct);
        var owned = await client.LookupAsync(owners.Select(x => x.Item1).ToArray(), ct);
        Assert.Equal(new (string, string?)[] { ("S1", "机构一课"), ("S2", null), ("S3", null), ("S4", null), ("S5", null), ("S6", "机构二课") }, // S6: IPMS writes 古才鸿, the account 古才鴻
            owners.Select(x => (x.Item1, owned[x.Item1].MechanicalSection)));
        Assert.Equal("周晓淋", owned["S1"].MechanicalOwner);
    }
}
