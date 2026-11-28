using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

/// <summary>One migrated database per class. Each scenario creates its own announcements.</summary>
public sealed class AnnouncementFixture : IAsyncDisposable
{
    private const string Password = "Announce#2026";
    internal static readonly string[] AllPermissions =
        ["announcement:publish", "announcement:withdraw", "announcement:pin", "announcement:reads"];
    private Task? setup;
    internal OemTestHost Host { get; private set; } = null!;
    internal ApiClient Admin { get; private set; } = null!;
    internal ApiClient Author { get; private set; } = null!;
    internal ApiClient Peer { get; private set; } = null!;
    internal ApiClient OtherStaff { get; private set; } = null!;
    internal ApiClient ThirdStaff { get; private set; } = null!;
    internal ApiClient SectionReader { get; private set; } = null!;
    internal ApiClient DepartmentReader { get; private set; } = null!;
    internal ApiClient PinManager { get; private set; } = null!;
    internal ApiClient ReadManager { get; private set; } = null!;
    internal ApiClient OtherAuthor { get; private set; } = null!;
    internal ApiClient Vendor { get; private set; } = null!;
    internal ApiClient OtherVendor { get; private set; } = null!;
    internal ulong Company { get; private set; }
    internal ulong EmptyCompany { get; private set; }
    internal ulong Department { get; private set; }
    internal ulong OtherDepartment { get; private set; }
    internal ulong ThirdDepartment { get; private set; }

    internal Task EnsureAsync(CancellationToken ct) => setup ??= SetupAsync(ct);

    private async Task SetupAsync(CancellationToken ct)
    {
        Host = await OemTestHost.StartAsync(ct);
        Admin = await Host.LoginAdminAsync(ct);
        var org = await Host.CreateOrgPathAsync("公告研发", ct);
        var other = await Host.CreateOrgPathAsync("公告品质", ct);
        var third = await Host.CreateOrgPathAsync("公告采购", ct);
        Department = org.Department;
        OtherDepartment = other.Department;
        ThirdDepartment = third.Department;
        Author = await Staff("ann_author", org.Section, AllPermissions, ct);
        Peer = await Staff("ann_peer", Department, AllPermissions, ct);
        DepartmentReader = await Staff("ann_dept", Department, [], ct);
        SectionReader = await Staff("ann_section", org.Section, [], ct);
        OtherStaff = await Staff("ann_quality", other.Section, [], ct);
        ThirdStaff = await Staff("ann_procure", third.Section, [], ct);
        OtherAuthor = await Staff("ann_author2", other.Section, AllPermissions, ct);
        PinManager = await Staff("ann_pin", Department, ["announcement:pin"], ct);
        ReadManager = await Staff("ann_detail", Department, ["announcement:reads"], ct);
        Vendor = await Host.CreateVendorAsync(Admin, "公告厂商甲", "ann_vendor_a", ct);
        OtherVendor = await Host.CreateVendorAsync(Admin, "公告厂商乙", "ann_vendor_b", ct);
        Company = await Host.CompanyIdOfAsync("ann_vendor_a", ct);
        EmptyCompany = (await Admin.PostAsync("/api/v1/oem/companies", new { name = "公告零账号厂商" }, ct).Ok()).Id();
    }

    internal async Task<ApiClient> Staff(string account, ulong org, string[] grants, CancellationToken ct)
    {
        var id = await Host.CreateInternalUserAsync(account, Password, grants, org, ct);
        await using var conn = await Host.OpenAsync(ct);
        await conn.ExecuteAsync("INSERT INTO user_data_scopes(user_id,domain,scope) VALUES (@id,'announcement','ORG')", new { id });
        return await Host.LoginInternalAsync(account, Password, ct);
    }

    internal async Task SetGrant(string account, string code, bool enable, CancellationToken ct)
    {
        await using var conn = await Host.OpenAsync(ct);
        var roleId = await conn.ExecuteScalarAsync<ulong>(
            "SELECT r.role_id FROM user_roles r JOIN users u ON u.id=r.user_id WHERE u.employee_no=@account", new { account });
        var permissionId = await conn.ExecuteScalarAsync<ulong>("SELECT id FROM permissions WHERE code=@code", new { code });
        var sql = enable ? "INSERT INTO role_permissions(role_id,permission_id) VALUES (@roleId,@permissionId)"
            : "DELETE FROM role_permissions WHERE role_id=@roleId AND permission_id=@permissionId";
        Assert.Equal(1, await conn.ExecuteAsync(sql, new { roleId, permissionId }));
    }

    internal async Task<ApiClient> AddVendorAccount(string account, CancellationToken ct)
    {
        await Admin.PostAsync($"/api/v1/oem/companies/{Company}/accounts", new { employeeNo = account, password = Password }, ct).Ok();
        await using var conn = await Host.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE oem_accounts SET must_change_password=0 WHERE employee_no=@account", new { account });
        return await Host.LoginOemAsync(account, Password, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
