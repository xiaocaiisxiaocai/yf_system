using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;

namespace Yf.Api.Tests;

/// <summary>Each vendor's outsourcing mail addresses on the outsourcing settings page (外协清单契约 → 外协邮箱).</summary>
public sealed class OutsourceVendorEmailTests
{
    [Fact(Timeout = 180_000)]
    public async Task SettingsHoldersMaintainAVendorsAddressesApartFromItsContacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_vendor_emails", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (64,'外协设定',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (65,'外协管理',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 64,id FROM permissions WHERE code IN ('outsource','outsource:settings');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 65,id FROM permissions WHERE code IN ('outsource','outsource:manage','outsource:send');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9771,'ve_buyer','unused','采购甲','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9772,'ve_manager','unused','管理乙','manager@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9771,64),(9772,65);
            """, ct);
        var buyer = new CurrentUser(9771, "ve_buyer", "INTERNAL", null);
        var manager = new CurrentUser(9772, "ve_manager", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var service = new OutsourceVendorEmailService(factory, new AuditService([]));
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct, contacts: [("ODM联络人", "odm@vendor.invalid", "ACTIVE")]);
        var yuankong = await OutsourceTestVendors.CreateAsync(factory, "元控", ct);
        var retired = await OutsourceTestVendors.CreateAsync(factory, "停用厂", ct, status: "DISABLED");

        // Only outsource:settings reads or writes them: managing and sending the list is not enough.
        Assert.Equal(403, await StatusOfAsync(() => service.ListAsync(manager, ct)));
        Assert.Equal(403, await StatusOfAsync(() => service.CreateAsync(manager, new(dingya.Id, "x@vendor.invalid"), ct)));
        Assert.Equal(403, await StatusOfAsync(() => service.ImportAsync(manager, new([new(2, "鼎亚", "x@vendor.invalid")]), ct)));
        Assert.Empty(await service.ListAsync(buyer, ct)); // the ODM/OEMA contact is not an outsourcing address

        // Add, with the checks every address goes through.
        var first = await service.CreateAsync(buyer, new(dingya.Id, " buyer@vendor.invalid "), ct);
        Assert.Equal(("鼎亚", "buyer@vendor.invalid"), (first.VendorName, first.Email));
        await service.CreateAsync(buyer, new(yuankong.Id, "yk@vendor.invalid"), ct);
        Assert.Equal(409, await StatusOfAsync(() => service.CreateAsync(buyer, new(dingya.Id, "BUYER@vendor.invalid"), ct)));
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(dingya.Id, "not-an-address"), ct)));
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(dingya.Id, ""), ct)));
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(null, "a@vendor.invalid"), ct)));
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(987654, "a@vendor.invalid"), ct)));
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(retired.Id, "a@vendor.invalid"), ct)));
        // The same address may serve two vendors.
        await service.CreateAsync(buyer, new(yuankong.Id, "buyer@vendor.invalid"), ct);
        var listed = await service.ListAsync(buyer, ct);
        Assert.Equal(["buyer@vendor.invalid|元控", "buyer@vendor.invalid|鼎亚", "yk@vendor.invalid|元控"],
            listed.Select(x => $"{x.Email}|{x.VendorName}").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(listed.Select(x => x.VendorName).Order(StringComparer.Ordinal).ToArray(), listed.Select(x => x.VendorName).ToArray()); // grouped by vendor

        // Edit and delete.
        var renamed = await service.UpdateAsync(buyer, first.Id, new("new@vendor.invalid"), ct);
        Assert.Equal("new@vendor.invalid", renamed.Email);
        var ykId = listed.First(x => x.Email == "yk@vendor.invalid").Id;
        Assert.Equal(409, await StatusOfAsync(() => service.UpdateAsync(buyer, ykId, new("buyer@vendor.invalid"), ct)));
        await service.DeleteAsync(buyer, first.Id, ct);
        Assert.Equal(404, await StatusOfAsync(() => service.DeleteAsync(buyer, first.Id, ct)));
        Assert.DoesNotContain(await service.ListAsync(buyer, ct), x => x.Id == first.Id);

        // At most 50 per vendor.
        await using (var conn = await database.Database.OpenAsync(ct))
            await conn.ExecuteAsync("""
                INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at)
                SELECT @id, CONCAT('bulk', n.n, '@vendor.invalid'), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                FROM (SELECT a.N + b.N * 10 + 1 AS n FROM
                  (SELECT 0 N UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8 UNION SELECT 9) a,
                  (SELECT 0 N UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4) b) n
                WHERE n.n <= 50
                """, new { id = dingya.Id });
        Assert.Equal(400, await StatusOfAsync(() => service.CreateAsync(buyer, new(dingya.Id, "one-too-many@vendor.invalid"), ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task ExcelImportOnlyAddsAndNeverOverwrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_vendor_email_import", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES(64,'外协设定',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 64,id FROM permissions WHERE code IN ('outsource','outsource:settings');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9771,'ve_buyer','unused','采购甲','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9771,64);
            """, ct);
        var buyer = new CurrentUser(9771, "ve_buyer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var service = new OutsourceVendorEmailService(factory, new AuditService([]));
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        await OutsourceTestVendors.CreateAsync(factory, "元控", ct);
        await OutsourceTestVendors.CreateAsync(factory, "停用厂", ct, status: "DISABLED");
        await service.CreateAsync(buyer, new(dingya.Id, "have@vendor.invalid"), ct);

        var result = await service.ImportAsync(buyer, new([
            new(2, "鼎亚", "new1@vendor.invalid"),
            new(3, "鼎亚", "HAVE@vendor.invalid"),      // already there
            new(4, "元控", "twice@vendor.invalid"),
            new(5, "元控", "twice@vendor.invalid"),     // repeated in the file
            new(6, "不存在的厂商", "x@vendor.invalid"),
            new(7, "停用厂", "y@vendor.invalid"),
            new(8, "鼎亚", "bad-address"),
            new(9, " 元控 ", "ok2@vendor.invalid"),     // vendor name is trimmed
        ]), ct);
        Assert.Equal((2, 3, 3), (result.Created, result.Skipped, result.Failed));
        Assert.Equal(["CREATED", "SKIPPED", "SKIPPED", "SKIPPED", "FAILED", "FAILED", "FAILED", "CREATED"], result.Rows.Select(x => x.Status));
        Assert.Equal(["have@vendor.invalid", "new1@vendor.invalid", "ok2@vendor.invalid"],
            (await service.ListAsync(buyer, ct)).Select(x => x.Email).Order().ToArray());
        Assert.Equal(1, await ImportAuditsAsync(database, ct));

        // Nothing to do changes nothing and writes no audit.
        var again = await service.ImportAsync(buyer, new([new(2, "鼎亚", "new1@vendor.invalid")]), ct);
        Assert.Equal((0, 1, 0), (again.Created, again.Skipped, again.Failed));
        Assert.Equal(1, await ImportAuditsAsync(database, ct));
        Assert.Equal(400, await StatusOfAsync(() => service.ImportAsync(buyer, new([]), ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportLocksVendorsBeforeCheckingLimitAndDisabledStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_vendor_email_lock", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES(64,'外协设定',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 64,id FROM permissions WHERE code IN ('outsource','outsource:settings');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9781,'ve_lock_buyer','unused','采购甲','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9781,64);
            """, ct);
        database.Options.ConnectionString = new MySqlConnectionStringBuilder(database.Options.ConnectionString) { MaximumPoolSize = 8 }.ConnectionString;
        var buyer = new CurrentUser(9781, "ve_lock_buyer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var service = new OutsourceVendorEmailService(factory, new AuditService([]));
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var yuankong = await OutsourceTestVendors.CreateAsync(factory, "元控", ct);

        await using (var setup = await database.Database.OpenAsync(ct))
            await setup.ExecuteAsync("""
                INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at)
                SELECT @id, CONCAT('bulk', n.n, '@vendor.invalid'), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                FROM (SELECT a.N + b.N * 10 + 1 AS n FROM
                  (SELECT 0 N UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5 UNION SELECT 6 UNION SELECT 7 UNION SELECT 8 UNION SELECT 9) a,
                  (SELECT 0 N UNION SELECT 1 UNION SELECT 2 UNION SELECT 3 UNION SELECT 4) b) n
                WHERE n.n <= 49
                """, new { id = dingya.Id });

        var unpooled = new MySqlConnectionStringBuilder(database.Options.ConnectionString) { Pooling = false }.ConnectionString;
        await using var blocker = new MySqlConnection(unpooled);
        await blocker.OpenAsync(ct);

        // Concurrent insert fills the vendor to 50 while ImportAsync waits on the vendor lock.
        await using (var tx = await blocker.BeginTransactionAsync(ct))
        {
            await blocker.ExecuteScalarAsync<ulong>("SELECT id FROM oem_companies WHERE id=@id FOR UPDATE", new { id = dingya.Id }, tx);
            var importTask = service.ImportAsync(buyer, new([
                new(2, "鼎亚", "extra1@vendor.invalid"),
                new(3, "鼎亚", "extra2@vendor.invalid"),
            ]), ct);
            await LockWaitProbe.WaitForLockWaitAsync(database.Options.ConnectionString, ct, importTask);
            Assert.False(importTask.IsCompleted);

            await blocker.ExecuteAsync(
                "INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at) VALUES(@id,'bulk50@vendor.invalid',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))",
                new { id = dingya.Id }, tx);
            await tx.CommitAsync(ct);

            var result = await importTask;
            Assert.Equal((0, 0, 2), (result.Created, result.Skipped, result.Failed));
            Assert.All(result.Rows, row => Assert.Contains("50", row.Message ?? "", StringComparison.Ordinal));
        }
        Assert.Equal(50, await blocker.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_vendor_emails WHERE vendor_id=@id", new { id = dingya.Id }));

        // Concurrent vendor disable completes while ImportAsync waits on the vendor lock.
        await using (var tx = await blocker.BeginTransactionAsync(ct))
        {
            await blocker.ExecuteScalarAsync<ulong>("SELECT id FROM oem_companies WHERE id=@id FOR UPDATE", new { id = yuankong.Id }, tx);
            var disabledTask = service.ImportAsync(buyer, new([new(2, "元控", "after-disable@vendor.invalid")]), ct);
            await LockWaitProbe.WaitForLockWaitAsync(database.Options.ConnectionString, ct, disabledTask);
            Assert.False(disabledTask.IsCompleted);

            await blocker.ExecuteAsync("UPDATE oem_companies SET status='DISABLED' WHERE id=@id", new { id = yuankong.Id }, tx);
            await tx.CommitAsync(ct);

            var result = await disabledTask;
            Assert.Equal((0, 0, 1), (result.Created, result.Skipped, result.Failed));
            Assert.Contains("停用", result.Rows.Single().Message ?? "", StringComparison.Ordinal);
        }
        Assert.Equal(0, await blocker.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_vendor_emails WHERE vendor_id=@id", new { id = yuankong.Id }));
    }

    // The test database pools one connection: open it only for the query, never across a service call.
    private static async Task<int> ImportAuditsAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OUTSOURCE_VENDOR_EMAIL_IMPORT'");
    }

    private static async Task<int> StatusOfAsync(Func<Task> action)
    {
        try { await action(); return 200; }
        catch (ApiException error) { return error.Status; }
    }
}
