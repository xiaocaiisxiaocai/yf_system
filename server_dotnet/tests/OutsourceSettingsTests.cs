using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>Reference data of the outsourcing list (外协清单契约): vendors, contacts, type dictionary, mapping and default copy list.</summary>
public sealed class OutsourceSettingsTests
{
    [Fact(Timeout = 120_000)]
    public async Task PurchasingMaintainsVendorsTypesMappingAndDefaultCopyList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_settings", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (60,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (61,'外协设定',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 60,id FROM permissions WHERE code IN ('outsource','outsource:view');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 61,id FROM permissions WHERE code IN ('outsource','outsource:settings');
            INSERT INTO suppliers(id,name,status,created_at,updated_at) VALUES(9601,'外协隔离供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9611,'os_buyer','unused','采购甲','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9612,'os_viewer','unused','主管乙','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9613,'os_member','unused','成员丙','member@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9614,'os_nomail','unused','无邮箱丁','','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9615,'os_supplier','unused','供应商戊','supplier@example.invalid','SUPPLIER',9601,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9616,'os_disabled','unused','停用己','disabled@example.invalid','INTERNAL',NULL,'DISABLED',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9611,61),(9612,60),(9613,3),(9615,4);
            """, ct);
        var buyer = new CurrentUser(9611, "os_buyer", "INTERNAL", null);
        var viewer = new CurrentUser(9612, "os_viewer", "INTERNAL", null);
        var member = new CurrentUser(9613, "os_member", "INTERNAL", null);
        var supplier = new CurrentUser(9615, "os_supplier", "SUPPLIER", 9601);
        var service = new OutsourceSettingsService(EfTestSupport.DbContextFactory(database.Options), new AuditService([]));

        // Vendors and their mail contacts. A contact may have no name; the address is what matters.
        var dingya = await service.CreateVendorAsync(buyer, new(" 鼎亚 ", null, 1), ct);
        var yuankong = await service.CreateVendorAsync(buyer, new("元控", "单一来源", 2), ct);
        Assert.Equal("鼎亚", dingya.Name);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.CreateVendorAsync(buyer, new("鼎亚", null, 3), ct))).Status);
        var first = await service.CreateContactAsync(buyer, dingya.Id, new(null, " first@vendor.invalid ", 1), ct);
        await service.CreateContactAsync(buyer, dingya.Id, new("业务窗口", "second@vendor.invalid", 2), ct);
        Assert.Null(first.Name);
        Assert.Equal("first@vendor.invalid", first.Email);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            service.CreateContactAsync(buyer, dingya.Id, new(null, "FIRST@vendor.invalid", 3), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            service.CreateContactAsync(buyer, dingya.Id, new(null, "not-an-address", 3), ct))).Status);
        // The same address may serve another vendor.
        await service.CreateContactAsync(buyer, yuankong.Id, new(null, "first@vendor.invalid", 1), ct);
        await service.SetContactStatusAsync(buyer, first.Id, AccountStatuses.Disabled, ct);
        var vendors = await service.VendorsAsync(buyer, ct);
        Assert.Equal(["鼎亚", "元控"], vendors.Select(x => x.Name));
        Assert.Equal([AccountStatuses.Disabled, AccountStatuses.Active], vendors[0].Contacts.Select(x => x.Status));

        // Type dictionary and its vendor mapping (attachment 2).
        var oema = await service.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 4), ct);
        var odm = await service.CreateTypeAsync(buyer, new("ODM-12345", "含售前售后", 1), ct);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.CreateTypeAsync(buyer, new("OEM A", "空格", 5), ct))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.CreateTypeAsync(buyer, new("oema", "大小写", 5), ct))).Status);
        oema = await service.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id, dingya.Id]), ct);
        odm = await service.SetTypeVendorsAsync(buyer, odm.Id, new([yuankong.Id]), ct);
        Assert.Equal([dingya.Id], oema.VendorIds);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            service.SetTypeVendorsAsync(buyer, odm.Id, new([yuankong.Id, 999_999UL]), ct))).Status);
        Assert.Equal(["ODM-12345", "OEMA"], (await service.TypesAsync(buyer, ct)).Select(x => x.Code));
        await service.SetTypeStatusAsync(buyer, odm.Id, AccountStatuses.Disabled, ct);

        // Default copy list: active internal accounts with an email address only.
        Assert.Equal("所选人员没有填写邮箱，无法接收抄送", (await Assert.ThrowsAsync<ApiException>(() =>
            service.SetDefaultCcAsync(buyer, new([9614]), ct))).Message);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SetDefaultCcAsync(buyer, new([9615]), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SetDefaultCcAsync(buyer, new([9616]), ct))).Status);
        var cc = await service.SetDefaultCcAsync(buyer, new([9613, 9612]), ct);
        Assert.Equal([9613UL, 9612UL], cc.Select(x => x.UserId));
        var options = await service.CcUserOptionsAsync(buyer, "主管", ct);
        Assert.Equal([9612UL], options.Select(x => x.UserId));
        Assert.DoesNotContain(await service.CcUserOptionsAsync(buyer, null, ct), x => x.UserId is 9614 or 9615 or 9616);

        // Viewers read reference data but cannot change it; staff without outsource permissions and suppliers see nothing.
        Assert.Equal(2, (await service.VendorsAsync(viewer, ct)).Length);
        Assert.Equal(2, (await service.DefaultCcAsync(viewer, ct)).Length);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.CreateVendorAsync(viewer, new("合科", null, 5), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.VendorsAsync(member, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.TypesAsync(supplier, ct))).Status);

        // Deleting an internal account drops it from the copy list.
        await database.ExecuteAsync("DELETE FROM user_roles WHERE user_id=9613; DELETE FROM users WHERE id=9613;", ct);
        Assert.Equal([9612UL], (await service.DefaultCcAsync(buyer, ct)).Select(x => x.UserId));

        await using var connection = await database.Database.OpenAsync(ct);
        var actions = (await connection.QueryAsync<string>(
            "SELECT action FROM audit_logs WHERE action LIKE 'OUTSOURCE_%' ORDER BY id")).ToArray();
        Assert.Equal(
            ["OUTSOURCE_VENDOR_CREATE", "OUTSOURCE_VENDOR_CREATE", "OUTSOURCE_CONTACT_CREATE", "OUTSOURCE_CONTACT_CREATE",
             "OUTSOURCE_CONTACT_CREATE", "OUTSOURCE_CONTACT_STATUS", "OUTSOURCE_TYPE_CREATE", "OUTSOURCE_TYPE_CREATE",
             "OUTSOURCE_TYPE_VENDORS", "OUTSOURCE_TYPE_VENDORS", "OUTSOURCE_TYPE_STATUS", "OUTSOURCE_DEFAULT_CC"],
            actions);
    }

    [Fact]
    public async Task FakeIpmsReturnsKnownWorkOrdersAndReportsOutages()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("yf-ipms-");
        try
        {
            var path = Path.Combine(directory.FullName, "ipms.json");
            await File.WriteAllTextAsync(path, """
                { "workOrders": [
                  { "workOrderNo": "21089901", "customer": "超穎(泰國)", "machineModel": "ST-201L", "mechanicalSection": "4.王家琦",
                    "workOrderIssuedOn": "2026-09-30", "outsourceTypeCode": "ODM-234", "quantity": 1, "dueDate": "2026-12-31" },
                  { "workOrderNo": "21089961", "closed": true }
                ] }
                """, ct);
            var client = new FakeIpmsClient(path);
            var found = await client.LookupAsync(["21089901", "21089961", "99999999"], ct);
            Assert.Equal(["21089901", "21089961"], found.Keys.Order(StringComparer.Ordinal));
            var row = found["21089901"];
            Assert.Equal("超穎(泰國)", row.Customer);
            Assert.Equal(new DateOnly(2026, 12, 31), row.DueDate);
            Assert.Equal(1, row.Quantity);
            Assert.False(row.Closed);
            Assert.True(found["21089961"].Closed);

            await File.WriteAllTextAsync(path, """{ "unavailable": true, "workOrders": [] }""", ct);
            await Assert.ThrowsAsync<IpmsUnavailableException>(() => client.LookupAsync(["21089901"], ct));
            await File.WriteAllTextAsync(path, "{ not json", ct);
            await Assert.ThrowsAsync<IpmsUnavailableException>(() => client.LookupAsync(["21089901"], ct));
            File.Delete(path);
            await Assert.ThrowsAsync<IpmsUnavailableException>(() => client.LookupAsync(["21089901"], ct));
        }
        finally { directory.Delete(recursive: true); }

        var unconfigured = new UnconfiguredIpmsClient();
        Assert.False(unconfigured.IsConfigured);
        await Assert.ThrowsAsync<IpmsUnavailableException>(() => unconfigured.LookupAsync(["21089901"], ct));
        Assert.Throws<InvalidOperationException>(() => new IpmsOptions { Mode = "Fake", FakeDataPath = "relative.json" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new IpmsOptions { Mode = "Real" }.Validate());
        new IpmsOptions().Validate();
    }
}
