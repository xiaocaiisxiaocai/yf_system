using System.Net;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class PermissionCatalogSecurityTests
{
    [Fact(Timeout = 240_000)]
    public async Task CatalogRequiresRoleManagementAndRevocationAppliesToAnExistingSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        await host.Anonymous().GetAsync("/api/v1/permissions", ct).Status(HttpStatusCode.Unauthorized);
        var admin = await host.LoginAdminAsync(ct);
        var catalog = (await admin.GetAsync("/api/v1/permissions", ct).Ok()).AsArray();
        Assert.Contains(catalog, row => row!["code"]!.GetValue<string>() == "role:manage");
        Assert.All(catalog, row => Assert.True(row!["grantable"]!.GetValue<bool>()));

        const string password = "Catalog#2026";
        await host.CreateInternalUserAsync("catalog_reader", password, ["dashboard"], null, ct);
        var reader = await host.LoginInternalAsync("catalog_reader", password, ct);
        await reader.GetAsync("/api/v1/permissions", ct).Status(HttpStatusCode.Forbidden);

        var managerId = await host.CreateInternalUserAsync("catalog_manager", password, ["role:manage"], null, ct);
        var manager = await host.LoginInternalAsync("catalog_manager", password, ct);
        var managedCatalog = (await manager.GetAsync("/api/v1/permissions", ct).Ok()).AsArray();
        Assert.Equal(catalog.Count, managedCatalog.Count);
        Assert.True(managedCatalog.Single(row => row!["code"]!.GetValue<string>() == "role:manage")!["grantable"]!.GetValue<bool>());
        Assert.False(managedCatalog.Single(row => row!["code"]!.GetValue<string>() == "user:manage")!["grantable"]!.GetValue<bool>());

        await using var connection = await host.OpenAsync(ct);
        var roleId = await connection.ExecuteScalarAsync<ulong>("SELECT role_id FROM user_roles WHERE user_id=@managerId", new { managerId });
        await admin.PutAsync($"/api/v1/admin/roles/{roleId}/permissions", new { permissionIds = Array.Empty<ulong>() }, ct).Ok();
        await manager.GetAsync("/api/v1/permissions", ct).Status(HttpStatusCode.Forbidden);

        await host.CreateSupplierUserAsync("catalog_supplier", password, ct);
        var supplier = await host.LoginInternalAsync("catalog_supplier", password, ct);
        await supplier.GetAsync("/api/v1/permissions", ct).Status(HttpStatusCode.Forbidden);
        var vendor = await host.CreateVendorAsync(admin, "目录隔离厂商", "catalog_vendor", ct);
        await vendor.GetAsync("/api/v1/permissions", ct).Status(HttpStatusCode.Forbidden);
    }
}
