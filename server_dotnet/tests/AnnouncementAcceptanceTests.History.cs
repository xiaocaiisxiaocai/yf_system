using System.Net;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed partial class AnnouncementAcceptanceTests
{
    [Fact]
    public async Task AnnouncementReferencesProtectReadersAuthorsAndSelectedDirectories()
    {
        await w.EnsureAsync(Ct);
        var org = await w.Host.CreateOrgPathAsync("公告留档", Ct);
        await w.Admin.DeleteAsync($"/api/v1/admin/departments/{org.Section}", Ct).Ok();
        var company = (await w.Admin.PostAsync("/api/v1/oem/companies", new { name = "公告留档厂商" }, Ct).Ok()).Id();
        var id = await Publish(w.Author, "Announcement history", "SELECTED", "SELECTED",
            [org.Department, w.Department], [company, w.Company]);
        await using var conn = await w.Host.OpenAsync(Ct);
        // Deliberately align independently assigned IDs across the internal and OEM account tables.
        await conn.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
                VALUES(980001,'announcement_other_realm','unused','同号账号','','INTERNAL','ACTIVE',0);
            INSERT INTO oem_accounts(id,employee_no,password_hash,real_name,email,oem_company_id,status,
                must_change_password,created_at,updated_at)
                SELECT 980001,'announcement_oem_twin',password_hash,'厂商同号账号','',oem_company_id,'ACTIVE',
                       0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3) FROM oem_accounts WHERE employee_no='ann_vendor_a';
            """);
        var twin = await w.Host.LoginOemAsync("announcement_oem_twin", "Vendor#2026", Ct);
        await twin.PostAsync($"{External}/{id}/read", null, Ct).Ok();
        await w.Vendor.PostAsync($"{External}/{id}/read", null, Ct).Ok();
        await w.SectionReader.PostAsync($"{Internal}/{id}/read", null, Ct).Ok();
        await w.Author.PostAsync($"{Manage}/{id}/withdraw", null, Ct).Ok();
        var author = await conn.QuerySingleAsync<ulong>("SELECT id FROM users WHERE employee_no='ann_author'");
        var reader = await conn.QuerySingleAsync<ulong>("SELECT id FROM users WHERE employee_no='ann_section'");
        var oemReader = await conn.QuerySingleAsync<ulong>("SELECT id FROM oem_accounts WHERE employee_no='ann_vendor_a'");
        foreach (var path in new[]
        {
            $"admin/users/{author}", $"admin/users/{reader}", $"oem/accounts/{oemReader}",
            $"admin/departments/{org.Department}", $"oem/companies/{company}",
        })
        {
            var rejected = await w.Admin.DeleteAsync("/api/v1/" + path, Ct).Status(HttpStatusCode.BadRequest);
            Assert.Contains("公告", rejected.Json!["message"]!.GetValue<string>());
        }

        // Deleting the internal twin does not remove the OEM twin's read history.
        await w.Admin.DeleteAsync("/api/v1/admin/users/980001", Ct).Ok();
        Assert.Contains(await Reads(w.Author, id), row => row!["accountName"]!.GetValue<string>() == "announcement_oem_twin");
        var unused = (await w.Admin.PostAsync("/api/v1/oem/companies", new { name = "无指定公告厂商" }, Ct).Ok()).Id();
        await w.Admin.DeleteAsync($"/api/v1/oem/companies/{unused}", Ct).Ok();
    }
}
