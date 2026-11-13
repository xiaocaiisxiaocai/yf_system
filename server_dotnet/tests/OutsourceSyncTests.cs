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
            no, "超穎(泰國)", "ST-201L", "4.王家琦", "机构甲", "电控乙", new DateOnly(2026, 9, 30), null, null,
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
            INSERT INTO role_permissions(role_id,permission_id) SELECT 81,id FROM permissions WHERE code IN ('outsource','outsource:view');
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

        var vendor = await settings.CreateVendorAsync(buyer, new("鼎亚", null, 1), ct);
        var type = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.SetTypeVendorsAsync(buyer, type.Id, new([vendor.Id]), ct);
        foreach (var no in new[] { "A1", "B1", "C1", "D1", "E1" }) ipms.Add(no);
        var imported = await items.ImportAsync(buyer, new(new[] { "A1", "B1", "C1", "D1", "E1" }
            .Select((no, index) => new OutsourceImportRow(index + 2, "2026-11-04", no, "鼎亚", "OEMA")).ToArray()), ct);
        Assert.Equal(5, imported.Created);
        await database.ExecuteAsync("UPDATE outsource_items SET send_status='SENT' WHERE work_order_no IN ('A1','E1')", ct);
        OutsourceItemResponse Row(IEnumerable<OutsourceItemResponse> rows, string no) => rows.Single(x => x.WorkOrderNo == no);
        var before = (await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List;

        ipms.Orders["A1"] = ipms.Orders["A1"] with { DueDate = new DateOnly(2027, 1, 15) };
        ipms.Orders["B1"] = ipms.Orders["B1"] with { Quantity = 2 };
        ipms.Orders["C1"] = ipms.Orders["C1"] with { Closed = true };
        ipms.Orders.Remove("D1");

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => sync.SyncNowAsync(viewer, new(null), ct))).Status);
        var status = await sync.SyncNowAsync(buyer, new(null), ct);
        Assert.Equal((5, 2, 1, 1, (string?)null, "MANUAL"),
            (status.LastProcessed, status.LastChanged, status.LastClosed, status.LastMissing, status.LastError, status.LastTrigger));
        Assert.NotNull(status.LastSuccessAt);
        Assert.False(status.Running);

        var after = (await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List;
        var a = Row(after, "A1");
        Assert.Equal((new DateOnly(2027, 1, 15), new DateOnly(2026, 12, 31), "PENDING_NOTICE"), (a.LatestDueDate, a.OriginalDueDate, a.SendStatus));
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
        var dueHistory = Assert.Single(await items.ChangesAsync(viewer, a.Id, ct));
        Assert.Equal(("latestDueDate", "最新交期", "2026-12-31", "2027-01-15", "IPMS_MANUAL", "采购"),
            (dueHistory.Field, dueHistory.Label, dueHistory.OldValue, dueHistory.NewValue, dueHistory.Source, dueHistory.ActorName));
        var quantityHistory = Assert.Single(await items.ChangesAsync(viewer, b.Id, ct));
        Assert.Equal(("quantity", "1", "2"), (quantityHistory.Field, quantityHistory.OldValue, quantityHistory.NewValue));
        Assert.Empty(await items.ChangesAsync(viewer, e.Id, ct));

        // Closed items are no longer synchronized; a second run changes nothing.
        var again = await sync.SyncNowAsync(buyer, new(null), ct);
        Assert.Equal((4, 0, 0, 1), (again.LastProcessed, again.LastChanged, again.LastClosed, again.LastMissing));
        var selected = await sync.SyncNowAsync(buyer, new([a.Id]), ct);
        Assert.Equal(1, selected.LastProcessed);

        // An outage keeps the data and records the error; the last success time stays.
        ipms.Unavailable = true;
        var outage = await Assert.ThrowsAsync<ApiException>(() => sync.SyncNowAsync(buyer, new(null), ct));
        Assert.Equal(503, outage.Status);
        var failed = await sync.StatusAsync(viewer, ct);
        Assert.Equal(("IPMS 暂时无法连接，请稍后再试", selected.LastSuccessAt), (failed.LastError, failed.LastSuccessAt));
        Assert.Equal(new DateOnly(2027, 1, 15), Row((await items.ListAsync(buyer, Query(true), 1, 20, 0, ct)).List, "A1").LatestDueDate);
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
    }

    private static OutsourceItemQuery Query(bool includeClosed) => new(null, null, null, null, null, null, null, includeClosed);
}
