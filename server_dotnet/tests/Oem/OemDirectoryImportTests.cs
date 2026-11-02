using Dapper;
using System.Text.Json.Nodes;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemDirectoryImportTests
{
    [Fact(Timeout = 180_000)]
    public async Task InternalAndRobotAccountsImportCreatesNewRowsSkipsConflictsAndNeverReturnsPasswords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var (_, _, sectionId) = await host.CreateOrgPathAsync("Excel导入", ct);
        var internalRoles = (await admin.GetAsync("/api/v1/admin/user-role-options", ct).Ok()).AsArray();
        var internalRoleId = internalRoles[0]!["id"]!.GetValue<ulong>();
        var internalImport = await admin.PostAsync("/api/v1/admin/users/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, employeeNo = "excel_internal_1", realName = "导入员工", email = "internal-import@example.invalid", departmentId = sectionId, roleId = internalRoleId, initialPassword = "Import#2026" },
                new { rowNumber = 3, employeeNo = "admin", realName = "现有管理员", email = "admin@example.invalid", departmentId = sectionId, roleId = internalRoleId, initialPassword = "" },
                new { rowNumber = 4, employeeNo = "excel_duplicate", realName = "重复一", email = "duplicate1@example.invalid", departmentId = sectionId, roleId = internalRoleId, initialPassword = "Import#2026" },
                new { rowNumber = 5, employeeNo = "EXCEL_DUPLICATE", realName = "重复二", email = "duplicate2@example.invalid", departmentId = sectionId, roleId = internalRoleId, initialPassword = "Import#2026" },
            }
        }, ct).Ok();
        Assert.Equal((1, 3, 0), Counts(internalImport));
        Assert.DoesNotContain("initialPassword", internalImport.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var supplier = await admin.PostAsync("/api/v1/admin/suppliers", new { name = "Robot Excel导入厂商", remark = (string?)null }, ct).Ok();
        var supplierId = supplier["id"]!.GetValue<ulong>();
        await admin.PostAsync($"/api/v1/admin/suppliers/{supplierId}/contacts", new
        { name = "Robot联系人已存在", email = "robot-contact-existing@example.invalid", sortNo = 0 }, ct).Ok();
        var contactsImport = await admin.PostAsync($"/api/v1/admin/suppliers/{supplierId}/contacts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, name = "Robot联系人已存在", email = "robot-contact-existing@example.invalid", sortNo = 0 },
                new { rowNumber = 3, name = "Robot联系人甲", email = "robot-contact-a@example.invalid", sortNo = 1 },
                new { rowNumber = 4, name = "Robot联系人乙", email = "robot-contact-b@example.invalid", sortNo = 2 },
            }
        }, ct).Ok();
        Assert.Equal((2, 1, 0), Counts(contactsImport));
        Assert.Equal(3, (await admin.GetAsync($"/api/v1/admin/suppliers/{supplierId}/contacts", ct).Ok()).AsArray().Count);
        var supplierRole = await admin.PostAsync("/api/v1/admin/roles", new
        {
            name = "Excel导入供应商角色", description = "Excel 导入测试",
        }, ct).Ok();
        var supplierRoleId = supplierRole["id"]!.GetValue<ulong>();
        await using (var permissionConn = await host.OpenAsync(ct))
        {
            var permissionIds = (await permissionConn.QueryAsync<ulong>(
                "SELECT id FROM permissions WHERE code IN ('dashboard','project:list') ORDER BY id")).ToArray();
            await admin.PutAsync($"/api/v1/admin/roles/{supplierRoleId}/permissions", new { permissionIds }, ct).Ok();
        }
        await admin.PostAsync($"/api/v1/admin/suppliers/{supplierId}/accounts", new
        {
            employeeNo = "robot_existing", password = "Import#2026", roleId = supplierRoleId,
        }, ct).Ok();
        var supplierImport = await admin.PostAsync($"/api/v1/admin/suppliers/{supplierId}/accounts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, employeeNo = "robot_new", roleId = supplierRoleId, initialPassword = "Import#2026" },
                new { rowNumber = 3, employeeNo = "robot_existing", roleId = supplierRoleId, initialPassword = "" },
                new { rowNumber = 4, employeeNo = "robot_duplicate", roleId = supplierRoleId, initialPassword = "Import#2026" },
                new { rowNumber = 5, employeeNo = "ROBOT_DUPLICATE", roleId = supplierRoleId, initialPassword = "Import#2026" },
            }
        }, ct).Ok();
        Assert.Equal((1, 3, 0), Counts(supplierImport));
        Assert.DoesNotContain("initialPassword", supplierImport.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT must_change_password FROM users WHERE employee_no='excel_internal_1'"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT must_change_password FROM users WHERE employee_no='robot_new'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='excel_duplicate'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE employee_no='robot_duplicate'"));
    }

    [Fact(Timeout = 180_000)]
    public async Task OemCompanyImportIsCreateOnlySkipsNamesAndReturnsRowResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        await admin.PostAsync("/api/v1/oem/companies", new
        {
            name = "Excel既有厂商", remark = "不覆盖",
        }, ct).Ok();

        var imported = await admin.PostAsync("/api/v1/oem/companies/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, name = "Excel新厂商", remark = "备注" },
                new { rowNumber = 3, name = "Excel既有厂商", remark = "不应覆盖" },
                new { rowNumber = 4, name = "文件内重复", remark = "" },
                new { rowNumber = 5, name = "文件内重复", remark = "" },
                new { rowNumber = 6, name = "备注错误", remark = new string('x', 513) },
            }
        }, ct).Ok();
        Assert.Equal((1, 3, 1), Counts(imported));

        await using var conn = await host.OpenAsync(ct);
        Assert.Equal("备注", await conn.ExecuteScalarAsync<string>("SELECT remark FROM oem_companies WHERE name='Excel新厂商'"));
        Assert.Equal("不覆盖", await conn.ExecuteScalarAsync<string>("SELECT remark FROM oem_companies WHERE name='Excel既有厂商'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE name='文件内重复'"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE name='Excel新厂商'"));
        var company = await admin.GetAsync("/api/v1/oem/companies", ct).Ok();
        var companyId = company["list"]!.AsArray().Single(row => row!["name"]!.GetValue<string>() == "Excel新厂商")!["id"]!.GetValue<ulong>();
        var accountImport = await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, companyName = "Excel新厂商", employeeNo = "oem_import_1", initialPassword = "Import#2026" },
                new { rowNumber = 3, companyName = "Excel新厂商", employeeNo = "oem_import_2", initialPassword = "Import#2026" },
            }
        }, ct).Ok();
        Assert.Equal((2, 0, 0), Counts(accountImport));
        Assert.DoesNotContain("initialPassword", accountImport.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT must_change_password FROM oem_accounts WHERE employee_no='oem_import_1'"));

        await admin.PostAsync($"/api/v1/oem/companies/{companyId}/contacts", new
        { name = "OEM联系人已存在", phone = "13800000000", email = "oem-contact-existing@example.invalid", sortNo = 0 }, ct).Ok();
        var contactImport = await admin.PostAsync($"/api/v1/oem/companies/{companyId}/contacts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, companyName = "Excel新厂商", name = "OEM联系人已存在", phone = "13800000000", email = "oem-contact-existing@example.invalid", sortNo = 0 },
                new { rowNumber = 3, companyName = "Excel新厂商", name = "OEM联系人甲", phone = "13800000001", email = "oem-contact-a@example.invalid", sortNo = 1 },
                new { rowNumber = 4, companyName = "Excel新厂商", name = "OEM联系人乙", phone = "13800000002", email = "oem-contact-b@example.invalid", sortNo = 2 },
            }
        }, ct).Ok();
        Assert.Equal((2, 1, 0), Counts(contactImport));
        Assert.Equal(3, (await admin.GetAsync($"/api/v1/oem/companies/{companyId}/contacts", ct).Ok()).AsArray().Count);
        var contactId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM oem_company_contacts WHERE name='OEM联系人甲'");
        await admin.PutAsync($"/api/v1/oem/company-contacts/{contactId}", new { name = "OEM联系人甲更新", email = "updated@example.invalid", sortNo = 4 }, ct).Ok();
        await admin.PutAsync($"/api/v1/oem/company-contacts/{contactId}/status", new { status = "DISABLED" }, ct).Ok();
        Assert.Equal("DISABLED", await conn.ExecuteScalarAsync<string>("SELECT status FROM oem_company_contacts WHERE id=@contactId", new { contactId }));
        Assert.Equal("updated@example.invalid", await conn.ExecuteScalarAsync<string>("SELECT email FROM oem_company_contacts WHERE id=@contactId", new { contactId }));
    }

    private static (int Created, int Skipped, int Failed) Counts(JsonNode result) =>
        (result["created"]!.GetValue<int>(), result["skipped"]!.GetValue<int>(), result["failed"]!.GetValue<int>());
}
