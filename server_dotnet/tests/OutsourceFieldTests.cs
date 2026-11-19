using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>Configurable columns: who fills each one in and what vendors see (外协清单契约 → 栏位设定, 2026-11-19).</summary>
public sealed class OutsourceFieldTests
{
    private sealed class StubIpms : IIpmsClient
    {
        public Dictionary<string, IpmsWorkOrder> Orders { get; } = new(StringComparer.Ordinal);
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
        {
            IReadOnlyDictionary<string, IpmsWorkOrder> found = workOrderNos.Where(Orders.ContainsKey)
                .ToDictionary(no => no, no => Orders[no], StringComparer.Ordinal);
            return Task.FromResult(found);
        }

        public void Add(string no) => Orders[no] = new(
            no, "超穎(泰國)", "ST-201L", "4.王家琦", "机构甲", "电控乙", new DateOnly(2026, 9, 30),
            1, new DateOnly(2026, 12, 31), false);
    }

    private sealed record Fixture(SchemaShapeTests.SchemaDatabaseScope Database, OutsourceItemService Items, OutsourceFieldService Fields,
        StubIpms Ipms, CurrentUser Planner, CurrentUser Buyer, CurrentUser Mechanic, CurrentUser Admin, OutsourceTestVendors.Vendor Dingya);

    private static async Task<Fixture> CreateAsync(string name, CancellationToken ct)
    {
        var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync(name, ct);
        await database.InitializeBusinessFixtureAsync(ct);
        // 生管 imports; 采购 and 机构 both edit the list; only 采购 maintains the settings; only the system administrator the fields.
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (70,'生管',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (71,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (72,'机构',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:import');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 71,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:manage','outsource:settings');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 72,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:manage');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9741,'of_planner','unused','生管甲','planner@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9742,'of_buyer','unused','采购乙','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9743,'of_mechanic','unused','机构丙','mechanic@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9740,'of_admin','unused','管理员','admin@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9741,70),(9742,71),(9743,72);
            INSERT INTO user_roles(user_id,role_id) SELECT 9740,id FROM roles WHERE is_built_in=1 AND name='系统管理员';
            """, ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var buyer = new CurrentUser(9742, "of_buyer", "INTERNAL", null);
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var oema = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id]), ct);
        return new(database, new OutsourceItemService(factory, audit, ipms), new OutsourceFieldService(factory, audit), ipms,
            new CurrentUser(9741, "of_planner", "INTERNAL", null), buyer, new CurrentUser(9743, "of_mechanic", "INTERNAL", null),
            new CurrentUser(9740, "of_admin", "INTERNAL", null), dingya);
    }

    private static OutsourceItemQuery All() => new(null, null, null, null, null, null, null, false);

    private static async Task<OutsourceItemResponse> ImportOneAsync(Fixture f, string no, CancellationToken ct)
    {
        f.Ipms.Add(no);
        await f.Items.ImportAsync(f.Planner, new([new OutsourceImportRow(2, "2026-11-04", no, "鼎亚", "OEMA")]), ct);
        return (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List.Single(x => x.WorkOrderNo == no);
    }

    private static async Task<int> StatusOfAsync(Func<Task> action) => (await Assert.ThrowsAsync<ApiException>(action)).Status;

    [Fact(Timeout = 180_000)]
    public async Task TheMigrationKeepsTodaysColumnsAndAddsAnInternalRobotVColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_seed", ct);
        await using var _ = f.Database;

        var settings = await f.Fields.SettingsAsync(f.Admin, ct);
        Assert.Equal(["outsourceDate", "status", "workOrderNo", "customer", "machineModel", "hasRobotV", "type", "vendor",
                "mechanicalSection", "mechanicalOwner", "electricalOwner", "workOrderIssuedOn", "bomOn", "drawingReleasedOn",
                "originalDueDate", "latestDueDate", "quantity", "remark"],
            settings.Fields.Select(x => x.Key));
        var robotV = settings.Fields.Single(x => x.Key == "hasRobotV");
        Assert.Equal(("是否有RobotV", "BOOLEAN", false, false, "ALL"), (robotV.Label, robotV.DataType, robotV.VendorVisible, robotV.VendorEditable, robotV.EditorScope));
        // Vendors keep seeing every built-in column; the IPMS columns are never filled in by hand.
        Assert.All(settings.Fields.Where(x => x.BuiltIn), x => Assert.True(x.VendorVisible));
        Assert.Equal("NONE", settings.Fields.Single(x => x.Key == "customer").EditorScope);
        Assert.Contains(settings.Roles, x => x.Name == "采购");

        // Everyone who could edit before still can (editor scope ALL), so the layout offers the same columns.
        var layout = await f.Fields.LayoutAsync(f.Buyer, ct);
        Assert.Equal(["outsourceDate", "status", "hasRobotV", "type", "vendor", "bomOn", "drawingReleasedOn", "latestDueDate", "remark"],
            layout.Fields.Where(x => x.CanEdit).Select(x => x.Key));
        Assert.Empty((await f.Fields.LayoutAsync(f.Planner, ct)).Fields.Where(x => x.CanEdit));
        // Field settings are their own permission.
        Assert.Equal(403, await StatusOfAsync(() => f.Fields.SettingsAsync(f.Buyer, ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task TheSettingsPageEnforcesWhatEachKindOfColumnMayChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_rules", ct);
        await using var _ = f.Database;
        var fields = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.ToDictionary(x => x.Key);

        var created = await f.Fields.CreateAsync(f.Admin, new("交付方式", "SELECT", ["自取", "送货"]), ct);
        var delivery = created.Fields.Single(x => x.Label == "交付方式");
        Assert.Equal((false, false, "ALL", "CUSTOM"), (delivery.VendorVisible, delivery.VendorEditable, delivery.EditorScope, delivery.Category));
        Assert.Equal(delivery.Key, created.Fields[^1].Key);
        Assert.Equal(409, await StatusOfAsync(() => f.Fields.CreateAsync(f.Admin, new("交付方式", "TEXT", null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.CreateAsync(f.Admin, new("选项", "SELECT", []), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.CreateAsync(f.Admin, new("文字", "TEXT", ["a"]), ct)));

        // Built-in labels and types are fixed; 工令 and 状态 always show; IPMS columns are never filled in by hand.
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["remark"].Id, new("说明", null, null, null, null, null, null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["workOrderNo"].Id, new(null, null, null, false, null, null, null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["customer"].Id, new(null, null, null, null, null, "ALL", null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["customer"].Id, new(null, null, null, null, true, null, null), ct)));
        // Vendors filling columns in is reserved: the switch cannot be turned on yet.
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["remark"].Id, new(null, null, null, null, true, null, null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["remark"].Id, new(null, null, null, null, null, "ROLES", []), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.SetStatusAsync(f.Admin, fields["remark"].Id, "DISABLED", ct)));

        var opened = (await f.Fields.UpdateAsync(f.Admin, delivery.Id, new("交付", null, ["自取", "送货", "快递"], true, null, "ROLES", [71]), ct))
            .Fields.Single(x => x.Id == delivery.Id);
        Assert.Equal(("交付", true, false, "ROLES"), (opened.Label, opened.VendorVisible, opened.VendorEditable, opened.EditorScope));
        Assert.Equal([71UL], opened.EditorRoleIds);
        var hidden = (await f.Fields.UpdateAsync(f.Admin, delivery.Id, new(null, null, null, false, null, null, null), ct)).Fields.Single(x => x.Id == delivery.Id);
        Assert.False(hidden.VendorVisible);

        // Pinned columns stay left; the rest follow the given order.
        var movable = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.Where(x => !x.Pinned).Select(x => x.Id).Reverse().ToArray();
        var ordered = await f.Fields.SetOrderAsync(f.Admin, new(movable), ct);
        Assert.Equal(["outsourceDate", "status", "workOrderNo", hidden.Key], ordered.Fields.Take(4).Select(x => x.Key));
        Assert.Equal(409, await StatusOfAsync(() => f.Fields.SetOrderAsync(f.Admin, new(movable[1..]), ct)));

        // A type with values can no longer change, and an option in use cannot be removed.
        var item = await ImportOneAsync(f, "W1", ct);
        await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, null, item.Status, item.UpdatedAt,
            Values: new() { [hidden.Key] = "快递" }), ct);
        Assert.Equal(409, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, delivery.Id, new(null, "TEXT", null, null, null, null, null), ct)));
        Assert.Equal(409, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, delivery.Id, new(null, null, ["自取", "送货"], null, null, null, null), ct)));

        // A disabled column disappears from the list but keeps its values.
        await f.Fields.SetStatusAsync(f.Admin, delivery.Id, "DISABLED", ct);
        Assert.DoesNotContain((await f.Fields.LayoutAsync(f.Buyer, ct)).Fields, x => x.Key == hidden.Key);
        await using (var conn = await f.Database.Database.OpenAsync(ct))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM outsource_item_values WHERE field_id={delivery.Id}"));
    }

    [Fact(Timeout = 180_000)]
    public async Task EachColumnIsFilledInOnlyByTheStaffItIsSetUpFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_editors", ct);
        await using var _ = f.Database;
        var fields = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.ToDictionary(x => x.Key);
        // 备注 only by 机构; 是否有RobotV by nobody inside the company.
        await f.Fields.UpdateAsync(f.Admin, fields["remark"].Id, new(null, null, null, null, null, "ROLES", [72]), ct);
        await f.Fields.UpdateAsync(f.Admin, fields["hasRobotV"].Id, new(null, null, null, null, null, "NONE", null), ct);
        var item = await ImportOneAsync(f, "W1", ct);

        // 采购 may still save the dialog with an unchanged 备注, but not change it, nor 是否有RobotV.
        var saved = await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, null, "PAUSED", item.UpdatedAt), ct);
        Assert.Equal("PAUSED", saved.Status);
        var denied = await Assert.ThrowsAsync<ApiException>(() => f.Items.UpdateAsync(f.Buyer, item.Id,
            new(null, item.VendorId, null, "采购的备注", saved.Status, saved.UpdatedAt), ct));
        Assert.Equal((403, "无权修改栏位「备注」"), (denied.Status, denied.Message));
        Assert.Equal(403, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Buyer,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "true" }), ct)));

        // 机构 fills in 备注; the administrator fills in anything open to staff.
        saved = await f.Items.UpdateAsync(f.Mechanic, item.Id, new(null, item.VendorId, null, "机构的备注", saved.Status, saved.UpdatedAt), ct);
        Assert.Equal("机构的备注", saved.Remark);
        saved = (await f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "是" }), ct)).Items.Single();
        Assert.Equal("true", saved.CustomValues!["hasRobotV"]);
        Assert.Equal(400, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "也许" }), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["customer"] = "x" }), ct)));

        // 发图 only by named people: 机构丙. Only accounts that can edit the list may be named.
        var settings = await f.Fields.SettingsAsync(f.Admin, ct);
        Assert.Equal([9740UL, 9742UL, 9743UL], settings.Users!.Select(x => x.Id).Order());
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["drawingReleasedOn"].Id,
            new(null, null, null, null, null, "USERS", null, [9741]), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["drawingReleasedOn"].Id,
            new(null, null, null, null, null, "USERS", null, []), ct)));
        var named = (await f.Fields.UpdateAsync(f.Admin, fields["drawingReleasedOn"].Id,
            new(null, null, null, null, null, "USERS", null, [9743, 9742]), ct)).Fields.Single(x => x.Key == "drawingReleasedOn");
        Assert.Equal("USERS", named.EditorScope);
        Assert.Equal([9742UL, 9743UL], named.EditorUserIds!);
        named = (await f.Fields.UpdateAsync(f.Admin, fields["drawingReleasedOn"].Id,
            new(null, null, null, null, null, "USERS", null, [9743]), ct)).Fields.Single(x => x.Key == "drawingReleasedOn");
        Assert.Equal([9743UL], named.EditorUserIds!);
        Assert.Equal(403, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Buyer,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, DrawingReleasedOn: "2026-12-01"), ct)));
        saved = (await f.Items.BatchUpdateAsync(f.Mechanic,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, DrawingReleasedOn: "2026-12-01"), ct)).Items.Single();
        Assert.Equal(new DateOnly(2026, 12, 1), saved.DrawingReleasedOn);
        // Back to everyone: the named people are dropped.
        named = (await f.Fields.UpdateAsync(f.Admin, fields["drawingReleasedOn"].Id,
            new(null, null, null, null, null, "ALL", null, null), ct)).Fields.Single(x => x.Key == "drawingReleasedOn");
        Assert.Empty(named.EditorUserIds!);

        var history = await f.Items.ChangesAsync(f.Buyer, item.Id, ct);
        var robotV = history.Single(x => x.Field == "hasRobotV");
        Assert.Equal(("是否有RobotV", null, "是", "EDIT"), (robotV.Label, robotV.OldValue, robotV.NewValue, robotV.Source));
        var layout = await f.Fields.LayoutAsync(f.Buyer, ct);
        Assert.False(layout.Fields.Single(x => x.Key == "remark").CanEdit);
        Assert.True((await f.Fields.LayoutAsync(f.Mechanic, ct)).Fields.Single(x => x.Key == "remark").CanEdit);
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyColumnsVendorsSeeAskForANewNoticeOrReachTheMail()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_vendor", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", ct);
        await f.Database.ExecuteAsync("UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id", ct);
        item = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single();

        // 是否有RobotV is internal by default: filling it in keeps the item sent.
        item = await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, null, item.Status, item.UpdatedAt,
            Values: new() { ["hasRobotV"] = "true" }), ct);
        Assert.Equal("SENT", item.SendStatus);

        var fields = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.ToDictionary(x => x.Key);
        await f.Fields.UpdateAsync(f.Admin, fields["hasRobotV"].Id, new(null, null, null, true, null, null, null), ct);
        await f.Fields.UpdateAsync(f.Admin, fields["remark"].Id, new(null, null, null, false, null, null, null), ct);
        item = await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, "只给内部看", item.Status, item.UpdatedAt), ct);
        Assert.Equal("SENT", item.SendStatus);
        item = await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, "只给内部看", item.Status, item.UpdatedAt,
            Values: new() { ["hasRobotV"] = "false" }), ct);
        Assert.Equal("PENDING_NOTICE", item.SendStatus);

        // The attachment and the "as last sent" snapshot hold only what vendors see.
        await using var context = await EfTestSupport.DbContextFactory(f.Database.Options).CreateDbContextAsync(ct);
        var layout = await OutsourceFieldLayout.LoadAsync(context, ct);
        var vendorFields = layout.WorkbookFields(vendor: true).Select(x => x.Key).ToArray();
        Assert.Contains("hasRobotV", vendorFields);
        Assert.DoesNotContain("remark", vendorFields);
        var snapshot = OutsourceWorkbook.Snapshot(item, layout.WorkbookFields(vendor: true));
        Assert.Equal("否", snapshot["hasRobotV"]);
        Assert.False(snapshot.ContainsKey("remark"));
        Assert.Contains("remark", layout.WorkbookFields(vendor: false).Select(x => x.Key));
    }
}
