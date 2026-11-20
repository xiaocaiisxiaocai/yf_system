using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>BOM时间 and 发图 come only from their own Excel import (外协清单契约 → BOM时间与发图导入).</summary>
public sealed class OutsourceDateImportTests
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

    private static OutsourceItemQuery All() => new(null, null, null, null, null, null, null, true);

    private static OutsourceDateImportRequest Rows(params (string? No, string? Bom, string? Drawing)[] rows) =>
        new(rows.Select((row, index) => new OutsourceDateImportRow(index + 2, row.No, row.Bom, row.Drawing)).ToArray());

    [Fact(Timeout = 180_000)]
    public async Task DatesAreImportedByWorkOrderUnderTheirOwnPermissionAndNoLongerEdited()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_date_import", ct);
        await using var _ = database;
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (70,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (71,'设计',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code IN ('outsource','outsource:manage','outsource:settings');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 71,id FROM permissions WHERE code IN ('outsource','outsource:date_import');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9751,'od_buyer','unused','采购甲','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9752,'od_design','unused','设计乙','design@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9751,70),(9752,71);
            """, ct);
        var buyer = new CurrentUser(9751, "od_buyer", "INTERNAL", null);
        var designer = new CurrentUser(9752, "od_design", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var dates = new OutsourceDateImportService(factory, audit);
        var vendor = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.SetTypeVendorsAsync(buyer, type.Id, new([vendor.Id]), ct);
        foreach (var no in new[] { "W1", "W2", "W3" }) ipms.Add(no);
        await items.ImportAsync(buyer, new([.. new[] { "W1", "W2", "W3" }.Select((no, index) =>
            new OutsourceImportRow(index + 2, "2026-11-04", no, "鼎亚", "OEMA"))]), ct);
        // W2 was already mailed; W3 is completed.
        await database.ExecuteAsync("""
            UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id, bom_on='2026-11-01' WHERE work_order_no='W2';
            UPDATE outsource_items SET closed_at=UTC_TIMESTAMP(3) WHERE work_order_no='W3';
            """, ct);

        // Purchasing can no longer import them, nor fill them in on the list (the edit request has no such fields).
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => dates.PreviewAsync(buyer, Rows(("W1", "2026-11-20", null)), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => dates.ImportAsync(buyer, Rows(("W1", "2026-11-20", null)), ct))).Status);

        var request = Rows(
            ("W1", "2026-11-20", "2026-11-25"),
            ("w2", null, "2026-11-26"),      // case-insensitive work order; empty BOM keeps 2026-11-01
            ("W3", "2026-11-20", null),      // completed
            ("W9", "2026-11-20", null),      // not on the list
            ("W1", null, null),              // nothing filled in
            ("W4!", "2026-11-20", null),     // bad work order
            ("W5", "11/20", null));          // bad date
        var preview = await dates.PreviewAsync(designer, request, ct);
        Assert.Equal((2, 2, 3), (preview.Ready, preview.Skipped, preview.Invalid));
        Assert.Equal(["READY", "READY", "SKIPPED", "SKIPPED", "INVALID", "INVALID", "INVALID"], preview.Rows.Select(x => x.Status));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), (DateOnly?)null, new DateOnly(2026, 11, 26)),
            (preview.Rows[1].BomOnBefore, preview.Rows[1].BomOn, preview.Rows[1].DrawingReleasedOnBefore, preview.Rows[1].DrawingReleasedOn));

        var result = await dates.ImportAsync(designer, request, ct);
        Assert.Equal((2, 2, 3), (result.Created, result.Skipped, result.Failed));
        var list = (await items.ListAsync(buyer, All(), 1, 20, 0, ct)).List.ToDictionary(x => x.WorkOrderNo);
        Assert.Equal((new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25)), (list["W1"].BomOn, list["W1"].DrawingReleasedOn));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 26)), (list["W2"].BomOn, list["W2"].DrawingReleasedOn));
        Assert.Null(list["W3"].BomOn);
        // Vendors see 发图 by default, so the mailed record now waits for a notice; the unsent one stays unsent.
        Assert.Equal(("PENDING_NOTICE", "UNSENT"), (list["W2"].SendStatus, list["W1"].SendStatus));
        // Before the first mail the imported cells are marked like any manual edit.
        Assert.Equal(["bomOn", "drawingReleasedOn"], list["W1"].ChangedFields!.Order());
        Assert.Equal("设计乙", list["W1"].ModifiedBy);
        var history = await items.ChangesAsync(buyer, list["W2"].Id, ct);
        var change = Assert.Single(history);
        Assert.Equal(("drawingReleasedOn", null, "2026-11-26", "DATE_IMPORT"), (change.Field, change.OldValue, change.NewValue, change.Source));

        // Importing the same sheet again changes nothing.
        var again = await dates.ImportAsync(designer, Rows(("W1", "2026-11-20", "2026-11-25")), ct);
        Assert.Equal((0, 1), (again.Created, again.Skipped));
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OUTSOURCE_ITEM_DATE_IMPORT'"));
    }
}
