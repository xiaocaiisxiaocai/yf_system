using System.IO.Compression;
using System.Net;
using Dapper;

namespace Yf.Api.Tests.Oem;

/// <summary>The outsourcing list in the ODM/OEMA vendor portal (外协清单契约 → 厂商查看).</summary>
public sealed class OemOutsourceViewTests
{
    [Fact(Timeout = 180_000)]
    public async Task VendorAccountsReadOnlyTheirOwnCompanysOutsourcingRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendor = await host.CreateVendorAsync(admin, "外协代工甲", "os_vendor_a", ct);
        var other = await host.CreateVendorAsync(admin, "外协代工乙", "os_vendor_b", ct);
        var companyA = await host.CompanyIdOfAsync("os_vendor_a", ct);
        var companyB = await host.CompanyIdOfAsync("os_vendor_b", ct);
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("""
                INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at) VALUES('超穎电子股份有限公司','超穎',1,UTC_TIMESTAMP(3));
                INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,customer,machine_model,status,remark,closed_at,created_by) VALUES
                  ('A2',2,'2026-11-04',@a,'超穎电子股份有限公司','ST-201L','PAUSED','内部也可见的备注',NULL,1),
                  ('A1',1,'2026-11-03',@a,'其他客户','ST-100','NORMAL',NULL,NULL,1),
                  ('A3',3,'2026-11-05',@a,'其他客户','ST-300','NORMAL',NULL,UTC_TIMESTAMP(3),1),
                  ('B1',4,'2026-11-04',@b,'超穎电子股份有限公司','ST-201L','NORMAL',NULL,NULL,1);
                """, new { a = companyA, b = companyB });
        }

        // 是否有RobotV is internal by default; 备注 is hidden on the field settings page here.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='remark'");

        // Only the vendor's own open records, by serial number, with the columns shown to vendors.
        var page = await vendor.GetAsync("/api/v1/oem/outsource-items", ct).Ok();
        Assert.Equal(2UL, page["total"]!.GetValue<ulong>());
        var list = page["list"]!.AsArray();
        Assert.Equal(["A1", "A2"], list.Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        var a2 = list[1]!;
        Assert.Equal(("PAUSED", "超穎", "暂停", "外协代工甲"), (a2["status"]!.GetValue<string>(),
            a2["values"]!["customer"]!.GetValue<string>(), a2["values"]!["status"]!.GetValue<string>(), a2["values"]!["vendor"]!.GetValue<string>()));
        var columns = page["columns"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToArray();
        Assert.Equal("serialNo", columns[0]);
        Assert.DoesNotContain("sendStatus", columns);
        Assert.DoesNotContain(columns, key => key is "importedBy" or "modifiedBy" or "hasRobotV" or "remark");
        Assert.Null(a2["values"]!["remark"]);

        // Keyword (work order, customer full or short name, model) and completed records on request.
        Assert.Equal(["A2"], (await vendor.GetAsync("/api/v1/oem/outsource-items?keyword=超穎", ct).Ok())["list"]!.AsArray()
            .Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        Assert.Equal(3UL, (await vendor.GetAsync("/api/v1/oem/outsource-items?includeClosed=true", ct).Ok())["total"]!.GetValue<ulong>());

        // The other vendor sees only its own record; internal staff use the outsourcing module, not this view.
        Assert.Equal(["B1"], (await other.GetAsync("/api/v1/oem/outsource-items", ct).Ok())["list"]!.AsArray()
            .Select(x => x!["values"]!["workOrderNo"]!.GetValue<string>()));
        await admin.GetAsync("/api/v1/oem/outsource-items", ct).Status(HttpStatusCode.Forbidden);

        // Export: the same filter over all pages, vendor columns only, as the vendor attachment.
        using var export = await vendor.Http.GetAsync("/api/v1/oem/outsource-items/export?keyword=ST", ct);
        export.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        using (var zip = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync(ct))))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync(ct);
            Assert.Contains(Cell("A1"), sheet, StringComparison.Ordinal);
            Assert.Contains(Cell("A2"), sheet, StringComparison.Ordinal);
            Assert.DoesNotContain(Cell("A3"), sheet, StringComparison.Ordinal);
            Assert.DoesNotContain(Cell("B1"), sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("内部也可见的备注", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("发送状态", sheet, StringComparison.Ordinal);
            Assert.Contains(Cell("超穎"), sheet, StringComparison.Ordinal);
        }
        await admin.GetAsync("/api/v1/oem/outsource-items/export", ct).Status(HttpStatusCode.Forbidden);

        // A disabled vendor loses access at once.
        await admin.PutAsync($"/api/v1/oem/companies/{companyA}/status", new { status = "DISABLED" }, ct).Ok();
        await vendor.GetAsync("/api/v1/oem/outsource-items", ct).Status(HttpStatusCode.Unauthorized);
    }

    private static string Cell(string text) => $"<t xml:space=\"preserve\">{text}</t>";
}
