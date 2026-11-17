using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

/// <summary>The outsourcing type is entered by staff: with the import file, and by purchasing afterwards (外协契约 → 外发类型字典与防呆).</summary>
public sealed class OutsourceItemTypeTests
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

    private sealed record Fixture(SchemaShapeTests.SchemaDatabaseScope Database, OutsourceItemService Items, OutsourceSettingsService Settings,
        StubIpms Ipms, OutsourceIpmsSyncService Sync, CurrentUser Planner, CurrentUser Buyer, OutsourceTestVendors.Vendor Dingya, OutsourceTestVendors.Vendor Yuankong,
        OutsourceTypeResponse Oema, OutsourceTypeResponse Odm);

    private static async Task<Fixture> CreateAsync(string name, CancellationToken ct)
    {
        var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync(name, ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (70,'生管',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (71,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:import');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 71,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:manage','outsource:settings');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9731,'ot_planner','unused','生管甲','planner@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9732,'ot_buyer','unused','采购乙','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9731,70),(9732,71);
            """, ct);
        var planner = new CurrentUser(9731, "ot_planner", "INTERNAL", null);
        var buyer = new CurrentUser(9732, "ot_buyer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var sync = new OutsourceIpmsSyncService(factory, audit, ipms, new AppOptions(), NullLogger<OutsourceIpmsSyncService>.Instance);
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var yuankong = await OutsourceTestVendors.CreateAsync(factory, "元控", ct);
        var oema = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        var odm = await settings.CreateTypeAsync(buyer, new("ODM-12345", "含售前售后", 2), ct);
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id]), ct);
        await settings.SetTypeVendorsAsync(buyer, odm.Id, new([yuankong.Id]), ct);
        return new(database, items, settings, ipms, sync, planner, buyer, dingya, yuankong, oema, odm);
    }

    private static OutsourceImportRequest Request(params (string No, string Vendor, string? Type)[] rows) =>
        new(rows.Select((row, index) => new OutsourceImportRow(index + 2, "2026-11-04", row.No, row.Vendor, row.Type)).ToArray());

    [Fact(Timeout = 180_000)]
    public async Task ImportRequiresAKnownTypeThatMatchesTheVendor()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_import", ct);
        await using var _ = f.Database;
        var old = await f.Settings.CreateTypeAsync(f.Buyer, new("OLD", "停用类型", 3), ct);
        await f.Settings.SetTypeVendorsAsync(f.Buyer, old.Id, new([f.Dingya.Id]), ct);
        await f.Settings.SetTypeStatusAsync(f.Buyer, old.Id, AccountStatuses.Disabled, ct);
        foreach (var no in new[] { "W1", "W2", "W3", "W4", "W5", "W6", "W7" }) f.Ipms.Add(no);
        var request = Request(
            ("W1", "鼎亚", "OEMA"), ("W2", "鼎亚", "oema【进料+制造】"), ("W3", "鼎亚", "  "), ("W4", "鼎亚", "ZZZ"),
            ("W5", "鼎亚", "ODM-12345"), ("W6", "鼎亚", "OLD"), ("W7", "鼎亚", null));

        var preview = await f.Items.PreviewImportAsync(f.Planner, request, ct);

        var messages = preview.Rows.ToDictionary(x => x.RowNumber, x => (x.Status, x.Message));
        Assert.Equal(("READY", null), messages[2]);
        Assert.Equal(("READY", null), messages[3]);
        Assert.Equal(("INVALID", "请填写外发类型"), messages[4]);
        Assert.Equal(("INVALID", "外发类型「ZZZ」不在字典中"), messages[5]);
        Assert.Equal(("INVALID", "ODM-12345【含售前售后】 不可配鼎亚"), messages[6]);
        Assert.Equal(("INVALID", "外发类型「OLD」不在字典中"), messages[7]);
        Assert.Equal(("INVALID", "请填写外发类型"), messages[8]);

        var imported = await f.Items.ImportAsync(f.Planner, request, ct);
        Assert.Equal((2, 0, 5), (imported.Created, imported.Skipped, imported.Failed));
        var rows = (await f.Items.ListAsync(f.Buyer, new OutsourceItemQuery(null, null, null, null, null, null, null, false), 1, 20, 0, ct)).List;
        Assert.Equal(["W1", "W2"], rows.Select(x => x.WorkOrderNo).Order(StringComparer.Ordinal));
        Assert.All(rows, x => Assert.Equal(("OEMA", f.Oema.Id, "OEMA【进料+制造】"), (x.TypeCode, x.TypeId, x.TypeLabel)));
    }

    [Fact(Timeout = 180_000)]
    public async Task SyncNeverChangesTheType()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_sync", ct);
        await using var _ = f.Database;
        f.Ipms.Add("W1");
        await f.Items.ImportAsync(f.Planner, Request(("W1", "鼎亚", "OEMA")), ct);
        // IPMS reports a new quantity: the type entered by staff stays as it is.
        f.Ipms.Orders["W1"] = f.Ipms.Orders["W1"] with { Quantity = 9 };

        await f.Sync.SyncNowAsync(f.Buyer, new(null), ct);

        var item = Assert.Single((await f.Items.ListAsync(f.Buyer, new OutsourceItemQuery(null, null, null, null, null, null, null, false), 1, 20, 0, ct)).List);
        Assert.Equal(("OEMA", 9), (item.TypeCode, item.Quantity));
        var history = await f.Items.ChangesAsync(f.Buyer, item.Id, ct);
        Assert.Equal(["quantity"], history.Select(x => x.Field));
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportNumbersTheRowsAndPurchasingMaintainsTheLatestDueDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_serial_due", ct);
        await using var _ = f.Database;
        foreach (var no in new[] { "W1", "W2", "W3" }) f.Ipms.Add(no);
        await f.Items.ImportAsync(f.Planner, Request(("W1", "鼎亚", "OEMA"), ("W2", "鼎亚", "OEMA")), ct);
        await f.Items.ImportAsync(f.Planner, Request(("W3", "鼎亚", "OEMA")), ct);

        var list = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List;
        // 序号 continues across imports, in file order.
        Assert.Equal([("W1", 1u), ("W2", 2u), ("W3", 3u)], list.OrderBy(x => x.SerialNo).Select(x => (x.WorkOrderNo, x.SerialNo)));
        // The latest due date starts as the original one.
        Assert.All(list, x => Assert.Equal(x.OriginalDueDate, x.LatestDueDate));

        var w1 = list.Single(x => x.WorkOrderNo == "W1");
        var edited = await f.Items.UpdateAsync(f.Buyer, w1.Id,
            new(w1.OutsourceDate.ToString("yyyy-MM-dd"), w1.VendorId, null, null, w1.Status, w1.UpdatedAt, "2027-02-01"), ct);
        Assert.Equal((new DateOnly(2027, 2, 1), w1.OriginalDueDate), (edited.LatestDueDate, edited.OriginalDueDate));
        Assert.Equal(["latestDueDate"], (await f.Items.ChangesAsync(f.Buyer, w1.Id, ct)).Select(x => x.Field));
        var bad = await Assert.ThrowsAsync<ApiException>(() => f.Items.UpdateAsync(f.Buyer, w1.Id,
            new(w1.OutsourceDate.ToString("yyyy-MM-dd"), w1.VendorId, null, null, w1.Status, edited.UpdatedAt, "明天"), ct));
        Assert.Equal(400, bad.Status);

        var w2 = list.Single(x => x.WorkOrderNo == "W2");
        var batch = await f.Items.BatchUpdateAsync(f.Buyer, new(new[] { new OutsourceItemVersion(w2.Id, w2.UpdatedAt) }, null, null, null, null, null, "2027-03-01"), ct);
        Assert.Equal(new DateOnly(2027, 3, 1), batch.Items.Single().LatestDueDate);

        // IPMS reports another original due date: nothing the purchaser maintained moves.
        f.Ipms.Orders["W1"] = f.Ipms.Orders["W1"] with { DueDate = new DateOnly(2030, 1, 1) };
        await f.Sync.SyncNowAsync(f.Buyer, new(null), ct);
        var after = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single(x => x.WorkOrderNo == "W1");
        Assert.Equal((new DateOnly(2027, 2, 1), w1.OriginalDueDate), (after.LatestDueDate, after.OriginalDueDate));
    }

    [Fact(Timeout = 180_000)]
    public async Task CellsThatDifferFromTheLastMailAreMarkedInTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_changed_cells", ct);
        await using var _ = f.Database;
        f.Ipms.Add("W1");
        await f.Items.ImportAsync(f.Planner, Request(("W1", "鼎亚", "OEMA")), ct);
        var item = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        Assert.Null(item.ChangedFields); // never mailed: nothing to compare with

        // Never mailed, but edited by hand: the edited fields are marked until the first mail.
        var edited = await f.Items.UpdateAsync(f.Buyer, item.Id,
            new(null, item.VendorId, null, "手填备注", item.Status, item.UpdatedAt, null), ct);
        Assert.Equal(["remark"], edited.ChangedFields!);
        // The edit needs no date; the row says who imported it and who edited it last, and when.
        Assert.Equal(item.OutsourceDate, edited.OutsourceDate);
        Assert.False(string.IsNullOrEmpty(edited.ImportedBy));
        Assert.Null(item.ModifiedBy);
        Assert.False(string.IsNullOrEmpty(edited.ModifiedBy));
        Assert.NotNull(edited.ModifiedAt);
        Assert.Equal(["remark"], Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List).ChangedFields!);

        // BOM时间 and 发图 are filled in by hand (never from IPMS): a date sets them, an empty text clears, null keeps.
        Assert.Null(edited.BomOn);
        edited = await f.Items.UpdateAsync(f.Buyer, item.Id,
            new(null, item.VendorId, null, "手填备注", item.Status, edited.UpdatedAt, null, "2026-11-20", "2026-11-25"), ct);
        Assert.Equal((new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25)), (edited.BomOn, edited.DrawingReleasedOn));
        Assert.Equal(["bomOn", "drawingReleasedOn", "remark"], edited.ChangedFields!.Order());
        edited = (await f.Items.BatchUpdateAsync(f.Buyer, new([new(item.Id, edited.UpdatedAt)], null, null, null, null, null, null, "", null), ct)).Items.Single();
        Assert.Equal((null, new DateOnly(2026, 11, 25)), (edited.BomOn, edited.DrawingReleasedOn));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Items.UpdateAsync(f.Buyer, item.Id,
            new(null, item.VendorId, null, "手填备注", item.Status, edited.UpdatedAt, null, "11/20", null), ct))).Status);
        item = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List) with { ChangedFields = null };

        var snapshot = OutsourceWorkbook.Snapshot(item);
        snapshot["quantity"] = "99";
        snapshot["remark"] = "旧备注";
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await f.Database.ExecuteAsync($"UPDATE outsource_items SET send_status='PENDING_NOTICE', notified_vendor_id=vendor_id, last_sent_snapshot='{json}'", ct);

        var marked = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        Assert.Equal(["quantity", "remark"], marked.ChangedFields!.Order());

        // The export marks the same cells (style 4 is the yellow fill).
        var export = await f.Items.ExportAsync(f.Buyer, All(), ct);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(export.Content));
        using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(await sheet.ReadToEndAsync(ct), "s=\"4\"").Count);
    }

    [Fact(Timeout = 180_000)]
    public async Task APurchaserCanDeleteARowThatWasAlreadyMailedAndTheAuditKeepsItsSendStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_delete_sent", ct);
        await using var _ = f.Database;
        f.Ipms.Add("W1");
        await f.Items.ImportAsync(f.Planner, Request(("W1", "鼎亚", "OEMA")), ct);
        await f.Database.ExecuteAsync("UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id", ct);
        var item = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Items.BatchDeleteAsync(f.Planner, new([new(item.Id, item.UpdatedAt)]), ct))).Status);

        await f.Items.BatchDeleteAsync(f.Buyer, new([new(item.Id, item.UpdatedAt)]), ct);

        Assert.Empty((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        await using var connection = await f.Database.Database.OpenAsync(ct);
        var detail = await connection.QuerySingleAsync<string>("SELECT detail FROM audit_logs WHERE action='OUTSOURCE_ITEM_DELETE'");
        Assert.True(detail.Contains("sendStatus", StringComparison.OrdinalIgnoreCase) && detail.Contains("SENT", StringComparison.Ordinal), detail);
    }

    /// <summary>The IPMS 工单查询 list, reduced to customer names, for the 客户简称 sync.</summary>
    private sealed class CustomerMes(params (string? Full, string? Short)[] rows) : IMesSource
    {
        public (string? Full, string? Short)[] Rows { get; set; } = rows;

        public Task<MesTestResult> TestAsync(MesConnection connection, CancellationToken ct) => throw new NotSupportedException();

        public Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();

        public Task<MesCustomerPage> FetchCustomersPageAsync(int page, int pageSize, CancellationToken ct) =>
            Task.FromResult(new MesCustomerPage(Rows.Length,
                Rows.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new MesCustomerRow(x.Full, x.Short)).ToArray()));
    }

    [Fact]
    public void ShortNamesTakeTheMostFrequentNameThatIsNotTheFullNameAgain()
    {
        var names = OutsourceSettingsService.ShortNames(new (string?, string?)[]
        {
            ("庆鼎精密电子(淮安)有限公司", "庆鼎"), ("庆鼎精密电子(淮安)有限公司", "庆鼎精密电子(淮安)有限公司"),
            ("庆鼎精密电子(淮安)有限公司", "庆鼎精密电子(淮安)有限公司"), ("庆鼎精密电子(淮安)有限公司", "庆鼎"),
            ("超颖电子电路股份有限公司", "超穎（黃石）"), ("超颖电子电路股份有限公司", "超穎"), ("超颖电子电路股份有限公司", "超穎"),
            ("深圳市泰雪科技有限公司", "深圳市泰雪科技有限公司"), ("", "空"), (null, null), ("无简称", " "),
            // IPMS writes one customer with a full-width and a half-width bracket, or in other letter case: still one customer.
            ("宏恒勝電子科技（淮安）有限公司", "宏恒勝"), ("宏恒勝電子科技（淮安)有限公司", "宏恒勝"),
            ("Tripod Vietnam (Bienhoa) Electronic", "健鼎（越南）一厂"), ("TRIPOD VIETNAM (BIENHOA) ELECTRONIC", "TRIPOD VIETNAM (BIENHOA) ELECTRONIC"),
        }.Select(x => new MesCustomerRow(x.Item1, x.Item2)));
        Assert.Equal(4, names.Count);
        Assert.Equal("宏恒勝", names["宏恒勝電子科技（淮安）有限公司"]);
        Assert.Equal("健鼎（越南）一厂", names["Tripod Vietnam (Bienhoa) Electronic"]);
        Assert.Equal("庆鼎", names["庆鼎精密电子(淮安)有限公司"]);
        Assert.Equal("超穎", names["超颖电子电路股份有限公司"]);
    }

    [Fact(Timeout = 180_000)]
    public async Task CustomerShortNamesSyncFromIpmsAndShowInTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_customer_alias", ct);
        await using var _ = f.Database;
        // IPMS spells the item's customer "超穎(泰國)" with full-width brackets here; it is the same customer.
        var mes = new CustomerMes(("超穎（泰國）", "超穎泰國"), ("超穎(泰國)", "超穎(泰國)"), ("超穎(泰國)", "超穎泰國"),
            ("超穎(泰國)", "泰国厂"), ("其他客户有限公司", "其他"), ("超穎（泰國)", "超穎泰國"));
        var settings = new OutsourceSettingsService(EfTestSupport.DbContextFactory(f.Database.Options), new AuditService([]), mes);
        f.Ipms.Add("W1");
        await f.Items.ImportAsync(f.Planner, Request(("W1", "鼎亚", "OEMA")), ct);
        var before = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        Assert.Equal(("超穎(泰國)", "超穎(泰國)"), (before.Customer, before.CustomerFullName));

        // Only purchasing with the settings permission syncs.
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => settings.SyncCustomerAliasesAsync(f.Planner, ct))).Status);
        var synced = await settings.SyncCustomerAliasesAsync(f.Buyer, ct);
        Assert.Equal((6, 2, 2, 0, 0), (synced.Rows, synced.Customers, synced.Added, synced.Updated, synced.KeptManual));
        var alias = synced.Aliases.Single(x => x.FullName != "其他客户有限公司");
        Assert.Equal(("超穎泰國", false), (alias.ShortName, alias.Manual));

        // The list, its keyword search and the export show the short name; the full name stays available.
        var item = Assert.Single((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List);
        Assert.Equal(("超穎泰國", "超穎(泰國)"), (item.Customer, item.CustomerFullName));
        var search = new OutsourceItemQuery(null, null, null, null, null, null, "穎泰", false);
        Assert.Single((await f.Items.ListAsync(f.Buyer, search, 1, 20, 0, ct)).List);
        var export = await f.Items.ExportAsync(f.Buyer, All(), ct);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(export.Content)))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/sharedStrings.xml")?.Open() ?? zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            Assert.Contains("超穎泰國", await reader.ReadToEndAsync(ct), StringComparison.Ordinal);
        }

        // A short name set by hand survives later syncs; clearing it hands the row back to IPMS.
        var manual = await settings.SetCustomerAliasAsync(f.Buyer, alias.Id, new(" 超颖泰国 "), ct);
        Assert.Equal(("超颖泰国", true), (manual.ShortName, manual.Manual));
        mes.Rows = [("超穎(泰國)", "超穎泰國廠"), ("超穎(泰國)", "超穎泰國廠"), ("其他客户有限公司", "其他")];
        synced = await settings.SyncCustomerAliasesAsync(f.Buyer, ct);
        Assert.Equal((0, 0, 1), (synced.Added, synced.Updated, synced.KeptManual));
        Assert.Equal("超颖泰国", synced.Aliases.Single(x => x.Id == alias.Id).ShortName);
        await settings.SetCustomerAliasAsync(f.Buyer, alias.Id, new(""), ct);
        synced = await settings.SyncCustomerAliasesAsync(f.Buyer, ct);
        Assert.Equal(("超穎泰國廠", false), synced.Aliases.Where(x => x.Id == alias.Id).Select(x => (x.ShortName, x.Manual)).Single());
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            settings.SetCustomerAliasAsync(f.Buyer, alias.Id, new(new string('长', 65)), ct))).Status);
        Assert.Equal(2, (await settings.CustomerAliasesAsync(f.Planner, ct)).Length);
    }

    private static OutsourceItemQuery All() => new(null, null, null, null, null, null, null, false);

    private static async Task<OutsourceItemResponse> ImportOneAsync(Fixture f, string no, string vendor, string type, CancellationToken ct)
    {
        f.Ipms.Add(no);
        await f.Items.ImportAsync(f.Planner, Request((no, vendor, type)), ct);
        return (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single(x => x.WorkOrderNo == no);
    }

    private static async Task<OutsourceTypeResponse> NewTypeAsync(Fixture f, string code, OutsourceTestVendors.Vendor vendor, CancellationToken ct)
    {
        var type = await f.Settings.CreateTypeAsync(f.Buyer, new(code, "测试类型", 9), ct);
        await f.Settings.SetTypeVendorsAsync(f.Buyer, type.Id, new([vendor.Id]), ct);
        return type;
    }

    [Fact(Timeout = 180_000)]
    public async Task BuyerCanChangeTheTypeButNotToOneTheVendorCannotTake()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_edit", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);
        var oemb = await NewTypeAsync(f, "OEMB", f.Dingya, ct);

        var changed = await f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Dingya.Id, oemb.Id, null, "NORMAL", item.UpdatedAt), ct);
        Assert.Equal(("OEMB", oemb.Id, "OEMB【测试类型】"), (changed.TypeCode, changed.TypeId, changed.TypeLabel));
        var history = Assert.Single(await f.Items.ChangesAsync(f.Buyer, item.Id, ct));
        Assert.Equal(("typeCode", "OEMA【进料+制造】", "OEMB【测试类型】", "EDIT"), (history.Field, history.OldValue, history.NewValue, history.Source));

        var refused = await Assert.ThrowsAsync<ApiException>(() =>
            f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Dingya.Id, f.Odm.Id, null, "NORMAL", changed.UpdatedAt), ct));
        Assert.Equal((400, "工令 W1：ODM-12345【含售前售后】 不可配鼎亚"), (refused.Status, refused.Message));
        Assert.Equal("OEMB", (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single().TypeCode);
    }

    [Fact(Timeout = 180_000)]
    public async Task ChangingTheTypeAndVendorTogetherIsCheckedAgainstTheNewPair()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_pair", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);

        // Neither OEMA with 元控 nor ODM-12345 with 鼎亚 is allowed, but the new pair 元控 + ODM-12345 is.
        var moved = await f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Yuankong.Id, f.Odm.Id, null, "NORMAL", item.UpdatedAt), ct);
        Assert.Equal(("元控", "ODM-12345"), (moved.VendorName, moved.TypeCode));
        Assert.Equal(OutsourceMatchStates.Ok, moved.MatchState);
    }

    [Fact(Timeout = 180_000)]
    public async Task BatchChangesAreRejectedWholeWhenAnyItemWouldBreakTheRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_batch", ct);
        await using var _ = f.Database;
        await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);
        await ImportOneAsync(f, "W2", "元控", "ODM-12345", ct);
        var rows = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List;
        var versions = rows.Select(x => new OutsourceItemVersion(x.Id, x.UpdatedAt)).ToArray();

        // Only the type: W2's vendor cannot take OEMA.
        var typeOnly = await Assert.ThrowsAsync<ApiException>(() => f.Items.BatchUpdateAsync(f.Buyer, new(versions, null, null, f.Oema.Id, null, "PAUSED"), ct));
        Assert.Equal(400, typeOnly.Status);
        Assert.Contains("W2", typeOnly.Message, StringComparison.Ordinal);
        // Only the vendor: W2 (ODM-12345) cannot go to 鼎亚.
        var vendorOnly = await Assert.ThrowsAsync<ApiException>(() => f.Items.BatchUpdateAsync(f.Buyer, new(versions, null, f.Dingya.Id, null, null, "PAUSED"), ct));
        Assert.Equal(400, vendorOnly.Status);
        Assert.Contains("W2", vendorOnly.Message, StringComparison.Ordinal);
        Assert.All((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List, x => Assert.Equal("NORMAL", x.Status));

        // A batch whose items all fit the new type works.
        var w1 = rows.Where(x => x.WorkOrderNo == "W1").Select(x => new OutsourceItemVersion(x.Id, x.UpdatedAt)).ToArray();
        var ok = await f.Items.BatchUpdateAsync(f.Buyer, new(w1, null, null, f.Oema.Id, null, "PAUSED"), ct);
        Assert.Equal("PAUSED", Assert.Single(ok.Items).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task SentItemsWaitForNoticeAfterATypeChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_sent", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);
        var oemb = await NewTypeAsync(f, "OEMB", f.Dingya, ct);
        await f.Database.ExecuteAsync($"UPDATE outsource_items SET send_status='SENT' WHERE id={item.Id}", ct);
        var sent = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single();

        var changed = await f.Items.UpdateAsync(f.Buyer, sent.Id, new("2026-11-04", f.Dingya.Id, oemb.Id, null, "NORMAL", sent.UpdatedAt), ct);

        Assert.Equal("PENDING_NOTICE", changed.SendStatus);
    }

    [Fact(Timeout = 180_000)]
    public async Task ItemsWithADisabledTypeStillAcceptOtherEditsButNeedAValidTypeToMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_disabled", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);
        var oemb = await NewTypeAsync(f, "OEMB", f.Dingya, ct);
        await f.Settings.SetTypeStatusAsync(f.Buyer, f.Oema.Id, AccountStatuses.Disabled, ct);
        var current = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single();
        Assert.Equal(OutsourceMatchStates.UnknownType, current.MatchState);

        // A remark edit does not touch the type or the vendor, so it is allowed.
        var remarked = await f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Dingya.Id, null, "加急", "NORMAL", current.UpdatedAt), ct);
        Assert.Equal("加急", remarked.Remark);

        // Moving to another vendor needs a valid type.
        var refused = await Assert.ThrowsAsync<ApiException>(() =>
            f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Yuankong.Id, null, "加急", "NORMAL", remarked.UpdatedAt), ct));
        Assert.Equal(400, refused.Status);
        Assert.Contains("外发类型不在字典中", refused.Message, StringComparison.Ordinal);

        // Choosing an enabled type repairs the record.
        var repaired = await f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Dingya.Id, oemb.Id, "加急", "NORMAL", remarked.UpdatedAt), ct);
        Assert.Equal(OutsourceMatchStates.Ok, repaired.MatchState);
    }

    [Fact(Timeout = 180_000)]
    public async Task ClosedItemsCannotChangeTheType()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_closed", ct);
        await using var _ = f.Database;
        var item = await ImportOneAsync(f, "W1", "鼎亚", "OEMA", ct);
        var oemb = await NewTypeAsync(f, "OEMB", f.Dingya, ct);
        await f.Database.ExecuteAsync($"UPDATE outsource_items SET closed_at=UTC_TIMESTAMP(3) WHERE id={item.Id}", ct);
        var closed = (await f.Items.ListAsync(f.Buyer, new OutsourceItemQuery(null, null, null, null, null, null, null, true), 1, 20, 0, ct)).List.Single();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            f.Items.UpdateAsync(f.Buyer, item.Id, new("2026-11-04", f.Dingya.Id, oemb.Id, null, "NORMAL", closed.UpdatedAt), ct));

        Assert.Equal((409, "已完成的记录不能修改，请先重新开启"), (error.Status, error.Message));
    }

    [Fact(Timeout = 180_000)]
    public async Task PreviewShowsTheTypeLabelForReadyRowsAndImportNeedsNoTypeFromIpms()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_type_preview", ct);
        await using var _ = f.Database;
        f.Ipms.Add("W1"); // IPMS never provides a type
        var preview = await f.Items.PreviewImportAsync(f.Planner, Request(("W1", "元控", "odm-12345")), ct);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(("READY", "ODM-12345【含售前售后】"), (row.Status, row.TypeLabel));
        var imported = await f.Items.ImportAsync(f.Planner, Request(("W1", "元控", "odm-12345")), ct);
        Assert.Equal(1, imported.Created);
    }
}
