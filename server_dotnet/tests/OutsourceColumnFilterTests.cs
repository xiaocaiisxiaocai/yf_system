using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class OutsourceColumnFilterTests
{
    private const string Items = "/api/v1/outsource/items";

    [Fact(Timeout = 180_000)]
    public async Task ColumnFiltersMatchDisplayedValuesBeforePagingAndWorkbookExport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var company = (await admin.PostAsync("/api/v1/oem/companies", new { name = "筛选厂商" }, ct).Ok()).Id();
        var path = await host.CreateOrgPathAsync("筛选", ct);
        var leader = await host.CreateInternalUserAsync("filter_leader", "Leader#2026", [], path.Department, ct);
        var engineer = await host.CreateInternalUserAsync("filter_engineer", "Engineer#2026", [], path.Section, ct);
        await using (var db = await host.OpenAsync(ct))
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE departments SET leader_account_id=@leader WHERE id=@department;
                UPDATE users SET real_name='工程甲' WHERE id=@engineer;
                UPDATE users SET real_name='设计主管甲' WHERE id=@leader;
                INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at)
                VALUES('电子制造有限公司','现场简称',1,UTC_TIMESTAMP(3));
                INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,status,customer,
                    mechanical_owner,quantity,bom_on,remark,created_by,created_at,modified_by,modified_at)
                VALUES
                  ('COL-A',1,'2026-11-03',@company,'NORMAL','电子制造有限公司','工程甲',2,'2026-11-06','Rate 5%',@engineer,'2026-11-01 07:00:00',@leader,'2026-11-01 07:00:00'),
                  ('COL-B',2,'2026-11-03',@company,'NORMAL','电子制造有限公司','工程甲',2,'2026-11-06','Rate 5%',@engineer,'2026-11-02 07:59:59.999',@leader,'2026-11-02 07:59:59.999'),
                  ('COL-C',3,'2026-11-03',@company,'PAUSED','电子制造有限公司','工程甲',2,'2026-11-06','Rate 50',@engineer,'2026-11-02 08:00:00',@leader,'2026-11-02 08:00:00'),
                  ('COL-D',4,'2026-11-03',@company,'NORMAL','其他公司','无关联',12,'2026-11-06','Rate 50',@engineer,'2026-11-01 06:59:59.999',NULL,NULL),
                  ('COL-E',5,'2026-11-03',@company,'NORMAL','电子制造有限公司','无关联',2,'2026-12-06',NULL,NULL,'2026-11-01 12:00:00',NULL,NULL);
                INSERT INTO outsource_item_values(item_id,field_id,value)
                SELECT i.id,f.id,'true' FROM outsource_items i JOIN outsource_fields f ON f.field_key='hasRobotV'
                WHERE i.work_order_no IN ('COL-A','COL-B','COL-C');
                INSERT INTO outsource_fields(field_key,label,data_type,options)
                VALUES('cost','工费','NUMBER',NULL),('fixtureClass','工装类别','SELECT','["A","AA"]');
                INSERT INTO outsource_item_values(item_id,field_id,value)
                SELECT i.id,f.id,CASE
                    WHEN f.field_key='fixtureClass' THEN IF(i.work_order_no='COL-A','A','AA')
                    WHEN i.work_order_no='COL-A' THEN '2.00'
                    WHEN i.work_order_no='COL-B' THEN '2'
                    ELSE '12.00' END
                FROM outsource_items i CROSS JOIN outsource_fields f
                WHERE f.field_key IN ('cost','fixtureClass') AND i.work_order_no IN ('COL-A','COL-B','COL-C');
                """, new { company, department = path.Department, engineer, leader }, cancellationToken: ct));
        }

        static string Columns(object values) => "columnFilters=" + Uri.EscapeDataString(JsonSerializer.Serialize(values));
        async Task<string[]> Search(string query)
        {
            var result = await admin.GetAsync(Items + "?" + query, ct).Ok();
            return WorkOrders(result);
        }
        static string[] WorkOrders(JsonNode page) =>
            page["list"]!.AsArray().Select(row => row!["workOrderNo"]!.GetValue<string>()).ToArray();

        var matches = new[] { "COL-A", "COL-B" };
        Assert.Equal(matches, await Search(Columns(new { remark = "%" })));
        Assert.Equal(["COL-A", "COL-B", "COL-C", "COL-E"], await Search(Columns(new { quantity = "2" })));
        Assert.Equal(["COL-A", "COL-B", "COL-C"], await Search(Columns(new { supervisor = "主管甲" })));
        Assert.Equal(["COL-E"], await Search(Columns(new { bomOn = "2026-12-06" })));
        Assert.Equal(["COL-C"], await Search(Columns(new { serialNo = "3" })));
        Assert.Equal(matches, await Search(Columns(new { cost = "2.0" })));
        Assert.Equal(["COL-A"], await Search(Columns(new { fixtureClass = "A" })));

        // On the day daylight saving ends, the local date covers 25 hours. COL-E has no importer.
        const string localTime = "&columnFilterTimeZone=America%2FLos_Angeles";
        Assert.Equal(matches, await Search(Columns(new { importedAt = "2026-11-01" }) + localTime));
        Assert.Equal(matches, await Search(Columns(new { modifiedAt = "2026-11-01", modifiedBy = "主管甲" }) + localTime));

        var filter = Columns(new { customer = "现场简称", hasRobotV = "true", importedBy = "工程甲" })
            + "&status=NORMAL&workOrders=COL-&vendorId=" + company;
        for (var page = 1; page <= 2; page++)
        {
            var result = await admin.GetAsync($"{Items}?{filter}&page={page}&pageSize=1", ct).Ok();
            Assert.Equal(2UL, result["total"]!.GetValue<ulong>());
            Assert.Equal(matches[page - 1], Assert.Single(WorkOrders(result)));
        }
        var longBody = new
        {
            columnFilters = JsonSerializer.Serialize(new { customer = "现场简称", hasRobotV = "true", importedBy = "工程甲" }),
            workOrders = string.Join('\n', Enumerable.Repeat(new string('字', 48), 65).Prepend("COL-")),
            status = "NORMAL", vendorId = company.ToString(System.Globalization.CultureInfo.InvariantCulture),
            page = "2", pageSize = "1",
        };
        var jsonPage = await admin.PostAsync($"{Items}/query", longBody, ct).Ok();
        Assert.Equal(2UL, jsonPage["total"]!.GetValue<ulong>());
        Assert.Equal(matches[1], Assert.Single(WorkOrders(jsonPage)));
        using var getExport = await admin.Http.GetAsync($"{Items}/export?{filter}", ct);
        using var postExport = await admin.Http.PostAsJsonAsync($"{Items}/export", longBody, ct);
        foreach (var response in new[] { getExport, postExport })
        {
            response.EnsureSuccessStatusCode();
            using var workbook = new ZipArchive(await response.Content.ReadAsStreamAsync(ct));
            using var sheet = workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var worksheet = await XDocument.LoadAsync(sheet, LoadOptions.None, ct);
            var ns = worksheet.Root!.Name.Namespace;
            Assert.Equal(matches, worksheet.Descendants(ns + "t").Select(t => t.Value)
                .Where(text => text.StartsWith("COL-", StringComparison.Ordinal)));
        }

        var unknownColumn = Columns(new { obsoleteColumn = "unavailable" });
        await admin.GetAsync($"{Items}?{unknownColumn}", ct).Status(HttpStatusCode.BadRequest);
        await admin.GetAsync($"{Items}/export?{unknownColumn}", ct).Status(HttpStatusCode.BadRequest);
    }
}
