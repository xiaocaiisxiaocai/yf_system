using Yf.Api.Modules.Admin;

namespace Yf.Api.Tests;

public sealed class AdminSecurityTests
{
    [Fact]
    public void SupplierRoleAllowsOnlySupplierProjectPermissions()
    {
        Assert.True(RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员",
            ["dashboard", "project:list", "file:upload", "file:download", "file:preview", "message:create", "project:submit", "project:withdraw"]));
        Assert.False(RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员", ["project:list", "project:confirm"]));
        Assert.False(RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员", ["project:list", "role:manage"]));
        Assert.False(RoleService.IsSupplierPermissionSetAllowed(true, "供应商人员", ["project:list", "project:view_all"]));
    }

    [Fact]
    public void NonSupplierRolesKeepTheirConfiguredPermissionSet()
    {
        Assert.True(RoleService.IsSupplierPermissionSetAllowed(false, "项目管理员", ["role:manage"]));
        Assert.True(RoleService.IsSupplierPermissionSetAllowed(true, "系统管理员", ["role:manage"]));
    }

    [Theory]
    [InlineData("ACTIVE", 1, true)]
    [InlineData("ACTIVE", 2, false)]
    [InlineData("DISABLED", 1, false)]
    public void LastActiveAdministratorProtectionOnlyCountsAnActiveTarget(string targetStatus, int activeAdminCount, bool expected)
    {
        Assert.Equal(expected, UserService.RequiresLastActiveAdminProtection(targetStatus, activeAdminCount));
    }
}
