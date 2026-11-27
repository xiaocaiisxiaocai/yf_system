using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>Daily and manual IPMS synchronization of the outsourcing list (外协清单契约 → IPMS 同步).</summary>
public sealed class OutsourceSyncTests
{
    private sealed class StubIpms : IIpmsClient
    {
        public Dictionary<string, IpmsWorkOrder> Orders { get; } = new(StringComparer.Ordinal);
        public bool Unavailable { get; set; }
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
        {
            if (Unavailable) throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试");
            IReadOnlyDictionary<string, IpmsWorkOrder> found = workOrderNos.Where(Orders.ContainsKey)
                .ToDictionary(no => no, no => Orders[no], StringComparer.Ordinal);
            return Task.FromResult(found);
        }

        public void Add(string no) => Orders[no] = new(
            no, "超穎(泰國)", "ST-201L", "机构甲", "电控乙", new DateOnly(2026, 9, 30),
            1, new DateOnly(2026, 12, 31), false);
    }

    [Theory]
    [InlineData("02:00", null, "2026-11-09T17:59:00Z", false)] // 01:59 business time
    [InlineData("02:00", null, "2026-11-09T18:00:00Z", true)]  // 02:00 business time
    [InlineData("02:00", "2026-11-10", "2026-11-09T20:00:00Z", false)] // already ran today
    [InlineData("02:00", "2026-11-09", "2026-11-09T20:00:00Z", true)]
    [InlineData("23:30", "2026-11-09", "2026-11-10T15:00:00Z", false)] // 23:00 business time
    public void ScheduledRunIsDueOncePerBusinessDayAfterTheConfiguredTime(string time, string? scheduledFor, string utcNow, bool due)
    {
        var last = scheduledFor is null ? (DateOnly?)null : DateOnly.Parse(scheduledFor, System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTime.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(due, OutsourceIpmsSyncService.IsDue(time, last, now, out _));
    }

    [Fact(Timeout = 180_000)]
    public async Task SyncRefreshesOpenItemsAndOnlyFlagsSentOnesForNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_sync", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (80,'外协采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (81,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 80,id FROM permissions WHERE code LIKE 'outsource%';
            INSERT INTO role_permissions(role_id,permission_id) SELECT 81,id FROM permissions WHERE code IN ('outsource');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9811,'os_sync_buyer','unused','采购','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9812,'os_sync_viewer','unused','主管','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9811,80),(9812,81);
            """, ct);
        var buyer = new CurrentUser(9811, "os_sync_buyer", "INTERNAL", null);
        var viewer = new CurrentUser(9812, "os_sync_viewer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var sync = new OutsourceIpmsSyncService(factory, audit, ipms, new AppOptions(), NullLogger<OutsourceIpmsSyncService>.Instance);

        var vendor = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.UpdateTypeSettingsAsync(buyer, type, ct, vendorIds: [vendor.Id]);
        foreach (var no in new[] { "A1", "B1", "C1", "D1", "E1" }) ipms.Add(no);
        var imported = await items.ImportAsync(buyer, new(new[] { "A1", "B1", "C1", "D1", "E1", "F1" }
            .Select((no, index) => new OutsourceImportRow(index + 2, "2026-11-04", no, "鼎亚", "OEMA")).ToArray()), ct);
        Assert.Equal((6, 0, 0), (imported.Created, imported.Skipped, imported.Failed));
        var pendingImport = imported.Rows.Single(x => x.Key == "F1");
        Assert.Equal(("CREATED", "IPMS 暂无资料，可先导入，后续同步补齐"), (pendingImport.Status, pendingImport.Message));
        var repeated = await items.ImportAsync(buyer, new([new(2, "2026-11-05", "F1", "鼎亚", "OEMA")]), ct);
        Assert.Equal((0, 1, 0), (repeated.Created, repeated.Skipped, repeated.Failed));
        await database.ExecuteAsync("UPDATE outsource_items SET send_status='SENT' WHERE work_order_no IN ('A1','E1')", ct);
        OutsourceItemResponse Row(IEnumerable<OutsourceItemResponse> rows, string no) => rows.Single(x => x.WorkOrderNo == no);
        var before = (await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List;
        var pending = Row(before, "F1");
        Assert.Equal((vendor.Id, type.Id, new DateOnly(2026, 11, 4), "NORMAL", "UNSENT"),
            (pending.VendorId, pending.TypeId, pending.OutsourceDate, pending.Status, pending.SendStatus));
        Assert.Null(pending.Customer);
        Assert.Null(pending.MachineModel);
        Assert.Null(pending.MechanicalOwner);
        Assert.Null(pending.ElectricalOwner);
        Assert.Null(pending.WorkOrderIssuedOn);
        Assert.Null(pending.Quantity);
        Assert.Null(pending.OriginalDueDate);
        Assert.Null(pending.LatestDueDate);
        Assert.Null(pending.IpmsSyncedAt);

        // IPMS moves the original due date and the owner: the owner is synced, the due dates (purchasing's latest one included) stay.
        ipms.Orders["A1"] = ipms.Orders["A1"] with { DueDate = new DateOnly(2027, 1, 15), MechanicalOwner = "新执行人" };
        ipms.Orders["B1"] = ipms.Orders["B1"] with { Quantity = 2 };
        ipms.Orders["C1"] = ipms.Orders["C1"] with { Closed = true };
        ipms.Orders.Remove("D1");
        ipms.Add("F1");

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => sync.SyncNowAsync(viewer, new(null), ct))).Status);
        var status = await sync.SyncNowAsync(buyer, new(null), ct);
        Assert.Equal((6, 3, 1, 1, (string?)null, "MANUAL"),
            (status.LastProcessed, status.LastChanged, status.LastClosed, status.LastMissing, status.LastError, status.LastTrigger));
        Assert.NotNull(status.LastSuccessAt);
        Assert.False(status.Running);

        var after = (await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List;
        var filled = Row(after, "F1");
        Assert.Equal((pending.Id, pending.SerialNo, pending.VendorId, pending.TypeId, pending.OutsourceDate, pending.SendStatus),
            (filled.Id, filled.SerialNo, filled.VendorId, filled.TypeId, filled.OutsourceDate, filled.SendStatus));
        Assert.Equal(("超穎(泰國)", "ST-201L", "机构甲", "电控乙", new DateOnly(2026, 9, 30), 1),
            (filled.Customer, filled.MachineModel, filled.MechanicalOwner, filled.ElectricalOwner, filled.WorkOrderIssuedOn, filled.Quantity));
        Assert.Equal((new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31)), (filled.OriginalDueDate, filled.LatestDueDate));
        Assert.NotNull(filled.IpmsSyncedAt);
        var filledHistory = await items.ChangesAsync(viewer, filled.Id, ct);
        Assert.Contains(filledHistory, change => change.Field == "originalDueDate" && change.OldValue is null);
        Assert.Contains(filledHistory, change => change.Field == "latestDueDate" && change.OldValue is null);
        var a = Row(after, "A1");
        Assert.Equal((new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31), "新执行人", "PENDING_NOTICE"), (a.LatestDueDate, a.OriginalDueDate, a.MechanicalOwner, a.SendStatus));
        var b = Row(after, "B1");
        Assert.Equal((2, "UNSENT"), (b.Quantity, b.SendStatus));
        Assert.True(Row(after, "C1").Closed);
        Assert.Equal(Row(before, "D1").UpdatedAt, Row(after, "D1").UpdatedAt);
        // An unchanged item records the sync time but keeps its edit version and send status.
        var e = Row(after, "E1");
        Assert.Equal((Row(before, "E1").UpdatedAt, "SENT"), (e.UpdatedAt, e.SendStatus));
        Assert.NotEqual(Row(before, "E1").IpmsSyncedAt, e.IpmsSyncedAt);
        Assert.NotEqual(Row(before, "A1").UpdatedAt, a.UpdatedAt);
        Assert.DoesNotContain((await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List, x => x.WorkOrderNo == "C1");

        // Each changed IPMS field is kept in the item's history with its source and the person who synced.
        var ownerHistory = Assert.Single(await items.ChangesAsync(viewer, a.Id, ct));
        Assert.Equal(("mechanicalOwner", "机构执行人", "机构甲", "新执行人", "IPMS_MANUAL", "采购"),
            (ownerHistory.Field, ownerHistory.Label, ownerHistory.OldValue, ownerHistory.NewValue, ownerHistory.Source, ownerHistory.ActorName));
        var quantityHistory = Assert.Single(await items.ChangesAsync(viewer, b.Id, ct));
        Assert.Equal(("quantity", "1", "2"), (quantityHistory.Field, quantityHistory.OldValue, quantityHistory.NewValue));
        Assert.Empty(await items.ChangesAsync(viewer, e.Id, ct));

        // Closed items are no longer synchronized; a second run changes nothing.
        var again = await sync.SyncNowAsync(buyer, new(null), ct);
        Assert.Equal((5, 0, 0, 1), (again.LastProcessed, again.LastChanged, again.LastClosed, again.LastMissing));
        var selected = await sync.SyncNowAsync(buyer, new([a.Id]), ct);
        Assert.Equal(1, selected.LastProcessed);

        // An outage keeps the data and records the error; the last success time stays.
        ipms.Unavailable = true;
        var outage = await Assert.ThrowsAsync<ApiException>(() => sync.SyncNowAsync(buyer, new(null), ct));
        Assert.Equal(503, outage.Status);
        var failed = await sync.StatusAsync(viewer, ct);
        Assert.Equal(("IPMS 暂时无法连接，请稍后再试", selected.LastSuccessAt), (failed.LastError, failed.LastSuccessAt));
        Assert.Equal("新执行人", Row((await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List, "A1").MechanicalOwner);
        ipms.Unavailable = false;

        // A run in progress blocks another one.
        await database.ExecuteAsync("UPDATE outsource_sync_state SET lease_until=UTC_TIMESTAMP(3) + INTERVAL 5 MINUTE WHERE id=1", ct);
        Assert.True((await sync.StatusAsync(viewer, ct)).Running);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => sync.SyncNowAsync(buyer, new(null), ct))).Status);
        await database.ExecuteAsync("UPDATE outsource_sync_state SET lease_until=NULL WHERE id=1", ct);

        // The scheduled run happens once per business day.
        var day = new DateOnly(2026, 11, 10);
        Assert.NotNull(await sync.RunAsync("SCHEDULED", null, null, day, ct));
        Assert.Null(await sync.RunAsync("SCHEDULED", null, null, day, ct));
        Assert.Equal("SCHEDULED", (await sync.StatusAsync(viewer, ct)).LastTrigger);

        // Sync time: settings permission and HH:mm only.
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => sync.SetSyncTimeAsync(buyer, new("25:00"), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => sync.SetSyncTimeAsync(viewer, new("03:30"), ct))).Status);
        Assert.Equal("03:30", (await sync.SetSyncTimeAsync(buyer, new("03:30"), ct)).SyncTime);
        Assert.Equal("03:30", (await sync.StatusAsync(viewer, ct)).SyncTime);

        // The daily update can be stopped and started again; only the settings permission may switch it.
        Assert.True((await sync.StatusAsync(viewer, ct)).ScheduleEnabled);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => sync.SetScheduleAsync(viewer, new(false), ct))).Status);
        Assert.False((await sync.SetScheduleAsync(buyer, new(false), ct)).ScheduleEnabled);
        Assert.False((await sync.StatusAsync(viewer, ct)).ScheduleEnabled);
        Assert.True((await sync.SetScheduleAsync(buyer, new(true), ct)).ScheduleEnabled);
    }

    [Fact(Timeout = 180_000)]
    public async Task HiddenMechanicalOwnerOnlyQueuesSentItemsWhoseVisibleSupervisorChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("sync_supervisor", ct);
        await database.InitializeAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE employee_no='admin';
            UPDATE outsource_fields SET vendor_visible=(field_key='supervisor')
              WHERE field_key IN ('mechanicalOwner','electricalOwner','supervisor');
            INSERT INTO departments(id,name,kind,sort_no,status,created_at,updated_at) VALUES
              (9001,'机构一组','DIVISION',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9002,'机构二组','DIVISION',2,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9911,'sync_leader_a','unused','甲主管','a@example.invalid','INTERNAL',9001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9912,'sync_leader_b','unused','乙主管','b@example.invalid','INTERNAL',9002,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9913,'sync_owner_a','unused','机构甲','','INTERNAL',9001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9914,'sync_owner_b','unused','机构乙','','INTERNAL',9002,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9915,'sync_owner_c','unused','机构丙','','INTERNAL',9001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            UPDATE departments SET leader_account_id=9911 WHERE id=9001;
            UPDATE departments SET leader_account_id=9912 WHERE id=9002;
            """, ct);
        var buyer = new CurrentUser(1, "admin", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var sync = new OutsourceIpmsSyncService(factory, audit, ipms, new AppOptions(), NullLogger<OutsourceIpmsSyncService>.Instance);
        var mails = new OutsourceMailService(factory, audit, new OutsourceMailSignal());
        var settings = new OutsourceSettingsService(factory, audit);
        var vendor = await OutsourceTestVendors.CreateAsync(factory, "主管同步测试厂", ct);
        await OutsourceTestVendors.AddOutsourceEmailAsync(factory, vendor.Id, "vendor@example.invalid", ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料制造", 1), ct);
        await settings.UpdateTypeSettingsAsync(buyer, type, ct, vendorIds: [vendor.Id]);
        string[] orders = ["DIFFERENT", "SAME", "CLEARED", "NO-LEADER"];
        foreach (var no in orders) ipms.Add(no);
        ipms.Orders["NO-LEADER"] = ipms.Orders["NO-LEADER"] with { MechanicalOwner = "未知人员" };
        var imported = await items.ImportAsync(buyer, new(orders.Select((no, i) =>
            new OutsourceImportRow(i + 2, "2026-11-04", no, vendor.Name, "OEMA")).ToArray()), ct);
        Assert.Equal(orders.Length, imported.Created);
        var before = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List;
        Assert.All(before.Where(item => item.WorkOrderNo != "NO-LEADER"), item => Assert.Equal("甲主管", item.Supervisor));
        var ids = before.Select(item => item.Id).ToArray();
        var preview = await mails.PreviewAsync(buyer, new(ids), ct);
        await mails.SendAsync(buyer, new(ids, preview.Fingerprint, "supervisor-first-notice", null), ct);

        ipms.Orders["DIFFERENT"] = ipms.Orders["DIFFERENT"] with { MechanicalOwner = "机构乙" };
        ipms.Orders["SAME"] = ipms.Orders["SAME"] with { MechanicalOwner = "机构丙" };
        ipms.Orders["CLEARED"] = ipms.Orders["CLEARED"] with { MechanicalOwner = null, ElectricalOwner = "机构乙" };
        ipms.Orders["NO-LEADER"] = ipms.Orders["NO-LEADER"] with { MechanicalOwner = null, ElectricalOwner = "机构乙" };
        await sync.SyncNowAsync(buyer, new(ids), ct);
        var after = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.ToDictionary(item => item.WorkOrderNo);
        Assert.Equal(("乙主管", "PENDING_NOTICE"), (after["DIFFERENT"].Supervisor, after["DIFFERENT"].SendStatus));
        Assert.Equal(("甲主管", "SENT"), (after["SAME"].Supervisor, after["SAME"].SendStatus));
        Assert.Equal((null, "PENDING_NOTICE"), (after["CLEARED"].Supervisor, after["CLEARED"].SendStatus));
        Assert.Equal((null, "SENT"), (after["NO-LEADER"].Supervisor, after["NO-LEADER"].SendStatus));
        var queue = (await items.ListAsync(buyer, Query(false) with { SendQueue = true }, 1, 20, 0, ct)).List;
        Assert.Equal(["CLEARED", "DIFFERENT"], queue.Select(item => item.WorkOrderNo).Order(StringComparer.Ordinal));

        // The IPMS owner remains 机构丙 while the organisation replaces the leader of their unit.
        await database.ExecuteAsync("""
            UPDATE departments SET leader_account_id=9913 WHERE id=9001;
            UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='supervisor';
            """, ct);
        await sync.SyncNowAsync(buyer, new(ids), ct);
        var hidden = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Single(item => item.WorkOrderNo == "SAME");
        Assert.Equal(("机构丙", "机构甲", "SENT"), (hidden.MechanicalOwner, hidden.Supervisor, hidden.SendStatus));

        await database.ExecuteAsync("UPDATE outsource_fields SET vendor_visible=1 WHERE field_key='supervisor'", ct);
        await sync.SyncNowAsync(buyer, new(ids), ct);
        var visible = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.ToDictionary(item => item.WorkOrderNo);
        Assert.Equal(("机构丙", "机构甲", "PENDING_NOTICE"),
            (visible["SAME"].MechanicalOwner, visible["SAME"].Supervisor, visible["SAME"].SendStatus));
        Assert.Equal((null, "SENT"), (visible["NO-LEADER"].Supervisor, visible["NO-LEADER"].SendStatus));

        // A notice containing the new supervisor becomes the baseline for the next sync.
        ulong[] same = [visible["SAME"].Id];
        var resend = await mails.PreviewAsync(buyer, new(same), ct);
        await mails.SendAsync(buyer, new(same, resend.Fingerprint, "supervisor-updated-notice", null), ct);
        await sync.SyncNowAsync(buyer, new(ids), ct);
        queue = (await items.ListAsync(buyer, Query(false) with { SendQueue = true }, 1, 20, 0, ct)).List;
        Assert.Equal(["CLEARED", "DIFFERENT"], queue.Select(item => item.WorkOrderNo).Order(StringComparer.Ordinal));
    }

    [Fact(Timeout = 180_000)]
    public async Task SentItemsWithAPreSupervisorSnapshotAreNotQueuedOrMarkedByTheUpgrade()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("sync_legacy_section", ct);
        await database.InitializeAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE employee_no='admin';
            INSERT INTO departments(id,name,kind,sort_no,status,created_at,updated_at) VALUES
              (9001,'机构一组','DIVISION',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9911,'sync_leader_a','unused','甲主管','a@example.invalid','INTERNAL',9001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9913,'sync_owner_a','unused','机构甲','','INTERNAL',9001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            UPDATE departments SET leader_account_id=9911 WHERE id=9001;
            """, ct);
        var buyer = new CurrentUser(1, "admin", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var sync = new OutsourceIpmsSyncService(factory, audit, ipms, new AppOptions(), NullLogger<OutsourceIpmsSyncService>.Instance);
        var mails = new OutsourceMailService(factory, audit, new OutsourceMailSignal());
        var settings = new OutsourceSettingsService(factory, audit);
        var vendor = await OutsourceTestVendors.CreateAsync(factory, "旧快照测试厂", ct);
        await OutsourceTestVendors.AddOutsourceEmailAsync(factory, vendor.Id, "vendor@example.invalid", ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料制造", 1), ct);
        await settings.UpdateTypeSettingsAsync(buyer, type, ct, vendorIds: [vendor.Id]);
        ipms.Add("LEGACY");
        ipms.Orders["LEGACY"] = ipms.Orders["LEGACY"] with { MechanicalOwner = "机构甲" };
        await items.ImportAsync(buyer, new([new OutsourceImportRow(2, "2026-11-04", "LEGACY", vendor.Name, "OEMA")]), ct);
        var id = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Single().Id;
        var preview = await mails.PreviewAsync(buyer, new([id]), ct);
        await mails.SendAsync(buyer, new([id], preview.Fingerprint, "legacy-first-notice", null), ct);

        // What the database held before the upgrade: the 机构课别 name, not the supervisor.
        await database.ExecuteAsync($"UPDATE outsource_items SET last_sent_snapshot=JSON_SET(JSON_REMOVE(last_sent_snapshot,'$.supervisor'),'$.mechanicalSection','机构一课') WHERE id={id}", ct);
        await database.ExecuteAsync(Yf.Api.Infrastructure.Migrations.MarkLegacyOutsourceSupervisorBaseline.MarkLegacySnapshotsSql, ct);
        await database.ExecuteAsync(Yf.Api.Infrastructure.Migrations.RenameOutsourceSupervisorKey.RenameKeySql, ct);

        var listed = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Single();
        Assert.Equal("甲主管", listed.Supervisor);
        Assert.DoesNotContain("supervisor", listed.ChangedFields ?? []);
        await sync.SyncNowAsync(buyer, new([id]), ct);
        var synced = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Single();
        Assert.Equal("SENT", synced.SendStatus);
        Assert.DoesNotContain("supervisor", synced.ChangedFields ?? []);

        // The baseline now follows the supervisor, so a later real change is still noticed.
        await database.ExecuteAsync("UPDATE departments SET leader_account_id=9913 WHERE id=9001", ct);
        await sync.SyncNowAsync(buyer, new([id]), ct);
        Assert.Equal("PENDING_NOTICE", (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Single().SendStatus);
    }

    [Fact(Timeout = 180_000)]
    public async Task PurchasingMarksItemsCompletedAndCanReopenThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_done", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (80,'外协采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (81,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 80,id FROM permissions WHERE code LIKE 'outsource%';
            INSERT INTO role_permissions(role_id,permission_id) SELECT 81,id FROM permissions WHERE code IN ('outsource');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9821,'os_done_buyer','unused','采购','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9822,'os_done_viewer','unused','主管','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9821,80),(9822,81);
            """, ct);
        var buyer = new CurrentUser(9821, "os_done_buyer", "INTERNAL", null);
        var viewer = new CurrentUser(9822, "os_done_viewer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var items = new OutsourceItemService(factory, audit, ipms);
        var sync = new OutsourceIpmsSyncService(factory, audit, ipms, new AppOptions(), NullLogger<OutsourceIpmsSyncService>.Instance);
        var vendor = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.UpdateTypeSettingsAsync(buyer, type, ct, vendorIds: [vendor.Id]);
        ipms.Add("A1"); ipms.Add("B1");
        await items.ImportAsync(buyer, new([new(2, "2026-11-04", "A1", "鼎亚", "OEMA"), new(3, "2026-11-04", "B1", "鼎亚", "OEMA")]), ct);
        await database.ExecuteAsync("UPDATE outsource_items SET send_status='SENT' WHERE work_order_no='B1'", ct);
        var list = (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List;
        OutsourceItemVersion Version(OutsourceItemResponse item) => new(item.Id, item.UpdatedAt);
        var a = list.Single(x => x.WorkOrderNo == "A1");
        var b = list.Single(x => x.WorkOrderNo == "B1");

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => items.SetCompletionAsync(viewer, new([Version(a)], true), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => items.SetCompletionAsync(buyer, new([Version(a)], null), ct))).Status);
        Assert.Equal(40903, (await Assert.ThrowsAsync<ApiException>(() =>
            items.SetCompletionAsync(buyer, new([new(a.Id, a.UpdatedAt.AddSeconds(-5))], true), ct))).Code);

        // Completing keeps the send status, hides the rows by default and records who did it.
        var done = (await items.SetCompletionAsync(buyer, new([Version(a), Version(b)], true), ct)).Items;
        Assert.All(done, x => Assert.True(x.Closed));
        Assert.Equal("SENT", done.Single(x => x.Id == b.Id).SendStatus);
        Assert.Empty((await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List);
        var history = Assert.Single(await items.ChangesAsync(viewer, a.Id, ct));
        Assert.Equal(("completed", "完成状态", "未完成", "已完成", "EDIT", "采购"),
            (history.Field, history.Label, history.OldValue, history.NewValue, history.Source, history.ActorName));

        // A completed item can no longer be edited, and the IPMS sync leaves it alone.
        var completedA = done.Single(x => x.Id == a.Id);
        var edit = await Assert.ThrowsAsync<ApiException>(() => items.UpdateAsync(buyer, a.Id,
            new("2026-11-05", vendor.Id, null, "备注", "NORMAL", completedA.UpdatedAt), ct));
        Assert.Equal((409, "已完成的记录不能修改，请先重新开启"), (edit.Status, edit.Message));
        ipms.Orders["A1"] = ipms.Orders["A1"] with { Quantity = 9 };
        Assert.Equal(0, (await sync.SyncNowAsync(buyer, new(null), ct)).LastProcessed);

        // Reopening brings it back to the list and to the sync; repeating an unchanged state is a no-op.
        var reopened = Assert.Single((await items.SetCompletionAsync(buyer, new([Version(completedA)], false), ct)).Items);
        Assert.False(reopened.Closed);
        Assert.Equal(reopened.UpdatedAt, Assert.Single((await items.SetCompletionAsync(buyer, new([Version(reopened)], false), ct)).Items).UpdatedAt);
        Assert.Equal(["A1"], (await items.ListAsync(buyer, Query(false), 1, 20, 0, ct)).List.Select(x => x.WorkOrderNo));
        Assert.Equal((1, 1), ((await sync.SyncNowAsync(buyer, new(null), ct)) is var run ? (run.LastProcessed, run.LastChanged) : default));
        Assert.Equal(["未完成", "已完成"], (await items.ChangesAsync(viewer, a.Id, ct)).Where(x => x.Field == "completed").Select(x => x.NewValue));
    }

    private static OutsourceItemQuery Query(bool includeClosed) => new(null, null, null, null, null, null, null, includeClosed);
}
