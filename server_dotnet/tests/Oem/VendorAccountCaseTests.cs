using System.Net;

namespace Yf.Api.Tests.Oem;

public sealed class VendorAccountCaseTests
{
    [Fact(Timeout = 180_000)]
    public async Task RobotAccountNamesAreCaseInsensitiveAcrossVendorsImportsAndLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var firstSupplier = (await admin.PostAsync("/api/v1/admin/suppliers",
            new { name = "Robot 大小写厂商甲", remark = (string?)null }, ct).Ok()).Id();
        var secondSupplier = (await admin.PostAsync("/api/v1/admin/suppliers",
            new { name = "Robot 大小写厂商乙", remark = (string?)null }, ct).Ok()).Id();
        var supplierRoleId = await CreateSupplierRoleAsync(admin, ct);

        const string password = "RobotCase#2026x";
        var account = await admin.PostAsync($"/api/v1/admin/suppliers/{firstSupplier}/accounts",
            new { employeeNo = "RobotCase01", password, roleId = supplierRoleId }, ct).Ok();

        await admin.PostAsync($"/api/v1/admin/suppliers/{firstSupplier}/accounts",
            new { employeeNo = "robotcase01", password, roleId = supplierRoleId }, ct).Status(HttpStatusCode.Conflict);
        await admin.PostAsync($"/api/v1/admin/suppliers/{secondSupplier}/accounts",
            new { employeeNo = "ROBOTCASE01", password, roleId = supplierRoleId }, ct).Status(HttpStatusCode.Conflict);
        await admin.PostAsync($"/api/v1/admin/suppliers/{secondSupplier}/accounts",
            new { employeeNo = "ADMIN", password, roleId = supplierRoleId }, ct).Status(HttpStatusCode.Conflict);

        var login = await host.Anonymous().PostAsync("/api/v1/auth/login",
            new { employeeNo = "rObOtCaSe01", password }, ct).Ok();
        Assert.Equal(account.Id(), login["user"]!.Id());

        var imported = await admin.PostAsync($"/api/v1/admin/suppliers/{secondSupplier}/accounts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, employeeNo = "RobotImport01", roleId = supplierRoleId, initialPassword = password },
                new { rowNumber = 3, employeeNo = "robotimport01", roleId = supplierRoleId, initialPassword = password },
            },
        }, ct).Ok();
        Assert.Equal(0, imported["created"]!.GetValue<int>());
        Assert.Equal(2, imported["skipped"]!.GetValue<int>());
        Assert.Equal(0, imported["failed"]!.GetValue<int>());
        Assert.All(imported["rows"]!.AsArray(), row => Assert.Equal("SKIPPED", row!["status"]!.GetValue<string>()));
        Assert.Empty((await admin.GetAsync($"/api/v1/admin/suppliers/{secondSupplier}/accounts", ct).Ok()).AsArray());
    }

    [Fact(Timeout = 180_000)]
    public async Task OemAccountNamesAreCaseInsensitiveAcrossVendorsImportsAndLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var firstCompany = (await admin.PostAsync("/api/v1/oem/companies",
            new { name = "ODM/OEMA 大小写厂商甲" }, ct).Ok()).Id();
        var secondCompany = (await admin.PostAsync("/api/v1/oem/companies",
            new { name = "ODM/OEMA 大小写厂商乙" }, ct).Ok()).Id();

        const string password = "OemCase#2026x";
        var account = await admin.PostAsync($"/api/v1/oem/companies/{firstCompany}/accounts",
            new { employeeNo = "OemCase01", password }, ct).Ok();

        await admin.PostAsync($"/api/v1/oem/companies/{firstCompany}/accounts",
            new { employeeNo = "oemcase01", password }, ct).Status(HttpStatusCode.Conflict);
        await admin.PostAsync($"/api/v1/oem/companies/{secondCompany}/accounts",
            new { employeeNo = "OEMCASE01", password }, ct).Status(HttpStatusCode.Conflict);

        var login = await host.Anonymous().PostAsync("/api/v1/oem/auth/login",
            new { employeeNo = "oEmCaSe01", password }, ct).Ok();
        Assert.Equal(account.Id(), login["account"]!.Id());

        var imported = await admin.PostAsync($"/api/v1/oem/companies/{secondCompany}/accounts/import", new
        {
            rows = new[]
            {
                new { rowNumber = 2, companyName = "ODM/OEMA 大小写厂商乙", employeeNo = "OemImport01", initialPassword = password },
                new { rowNumber = 3, companyName = "ODM/OEMA 大小写厂商乙", employeeNo = "oemimport01", initialPassword = password },
            },
        }, ct).Ok();
        Assert.Equal(0, imported["created"]!.GetValue<int>());
        Assert.Equal(2, imported["skipped"]!.GetValue<int>());
        Assert.Equal(0, imported["failed"]!.GetValue<int>());
        Assert.All(imported["rows"]!.AsArray(), row => Assert.Equal("SKIPPED", row!["status"]!.GetValue<string>()));
        Assert.Empty((await admin.GetAsync($"/api/v1/oem/companies/{secondCompany}/accounts", ct).Ok()).AsArray());
    }

    private static async Task<ulong> CreateSupplierRoleAsync(ApiClient admin, CancellationToken ct)
    {
        var role = await admin.PostAsync("/api/v1/admin/roles", new
        {
            name = "大小写测试供应商角色",
            description = "仅包含供应商账号可用权限",
        }, ct).Ok();
        var allowedCodes = new HashSet<string>(["dashboard", "project:list"], StringComparer.Ordinal);
        var permissionIds = (await admin.GetAsync("/api/v1/permissions", ct).Ok()).AsArray()
            .Where(permission => allowedCodes.Contains(permission!["code"]!.GetValue<string>()))
            .Select(permission => permission!.Id()).ToArray();
        Assert.Equal(allowedCodes.Count, permissionIds.Length);
        await admin.PutAsync($"/api/v1/admin/roles/{role.Id()}/permissions", new { permissionIds }, ct).Ok();
        return role.Id();
    }
}
