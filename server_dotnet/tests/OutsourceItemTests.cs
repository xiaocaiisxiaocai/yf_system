using System.IO.Compression;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>Import, list, edit, delete and export of the outsourcing list (外协清单契约 → 汇入 / 采购编辑与删除 / 页面).</summary>
public sealed class OutsourceItemTests
{
    private sealed class StubIpms : IIpmsClient
    {
        public Dictionary<string, IpmsWorkOrder> Orders { get; } = new(StringComparer.Ordinal);
        public bool Unavailable { get; set; }
        public bool IsConfigured => true;

        public Task<IReadOnlyDictionary<string, IpmsWorkOrder>> LookupAsync(IReadOnlyCollection<string> workOrderNos, CancellationToken ct)
        {
            if (Unavailable) throw new IpmsUnavailableException("IPMS 暂时无法连接，请稍后再试");
            IReadOnlyDictionary<string, IpmsWorkOrder> found = workOrderNos.Where(Orders.ContainsKey)
                .ToDictionary(no => no, no => Orders[no], StringComparer.Ordinal);
            return Task.FromResult(found);
        }

        public void Add(string no, string? type, bool closed = false) => Orders[no] = new(
            no, "超穎(泰國)", "ST-201L", "4.王家琦", "机构甲", "电控乙", new DateOnly(2026, 9, 30), null, null,
            type, 1, new DateOnly(2026, 12, 31), closed);
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportListEditDeleteAndExportFollowTheContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("outsource_items", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (70,'生管',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (71,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (72,'主管',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:import');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 71,id FROM permissions WHERE code IN ('outsource','outsource:view','outsource:manage','outsource:settings');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 72,id FROM permissions WHERE code IN ('outsource','outsource:view');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9711,'oi_planner','unused','生管甲','planner@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9712,'oi_buyer','unused','采购乙','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9713,'oi_viewer','unused','主管丙','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9711,70),(9712,71),(9713,72);
            """, ct);
        var planner = new CurrentUser(9711, "oi_planner", "INTERNAL", null);
        var buyer = new CurrentUser(9712, "oi_buyer", "INTERNAL", null);
        var viewer = new CurrentUser(9713, "oi_viewer", "INTERNAL", null);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var service = new OutsourceItemService(factory, audit, ipms);

        var dingya = await settings.CreateVendorAsync(buyer, new("鼎亚", null, 1), ct);
        var yuankong = await settings.CreateVendorAsync(buyer, new("元控", null, 2), ct);
        var retired = await settings.CreateVendorAsync(buyer, new("停用厂", null, 3), ct);
        await settings.SetVendorStatusAsync(buyer, retired.Id, AccountStatuses.Disabled, ct);
        var oema = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        var odm = await settings.CreateTypeAsync(buyer, new("ODM-12345", "含售前售后", 2), ct);
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([dingya.Id]), ct);
        await settings.SetTypeVendorsAsync(buyer, odm.Id, new([yuankong.Id]), ct);

        ipms.Add("21089901", "OEMA【进料+制造】");
        ipms.Add("21089961", "OEMA");
        ipms.Add("21090001", "OEMA");
        ipms.Add("21090101", "XYZ");
        ipms.Add("21090201", "OEMA", closed: true);
        ipms.Add("21090301", "OEMA");
        ipms.Add("21089902", "odm-12345");
        OutsourceImportRequest Request(params (string Date, string No, string Vendor)[] rows) =>
            new(rows.Select((row, index) => new OutsourceImportRow(index + 2, row.Date, row.No, row.Vendor)).ToArray());
        var request = Request(
            ("2026-11-04", "21089901", "鼎亚"), ("2026-11-04", "21089961", "元控"), ("2026-11-04", "99999999", "鼎亚"),
            ("2026-11-04", "21090001", "鼎亚"), ("2026-11-05", "21090001", "鼎亚"), ("11/04/2026", "21090401", "鼎亚"),
            ("2026-11-04", "21090501", "不存在"), ("2026-11-04", "21090101", "鼎亚"), ("2026-11-04", "21090201", "鼎亚"),
            ("2026-11-04", "21090301", "停用厂"));

        var preview = await service.PreviewImportAsync(planner, request, ct);
        Assert.Equal((1, 2, 7), (preview.Ready, preview.Skipped, preview.Invalid));
        var messages = preview.Rows.ToDictionary(x => x.RowNumber, x => (x.Status, x.Message));
        Assert.Equal(("READY", null), messages[2]);
        Assert.Equal(("INVALID", "OEMA【进料+制造】 不可配元控"), messages[3]);
        Assert.Equal(("INVALID", "IPMS 查无此工令"), messages[4]);
        Assert.Equal("SKIPPED", messages[5].Status);
        Assert.Equal("SKIPPED", messages[6].Status);
        Assert.Equal(("INVALID", "外发日期格式不正确"), messages[7]);
        Assert.Equal(("INVALID", "外发厂商不在外协厂商目录中"), messages[8]);
        Assert.Equal(("INVALID", "外发类型「XYZ」不在字典中"), messages[9]);
        Assert.Equal(("INVALID", "IPMS 显示该工令已结案"), messages[10]);
        Assert.Equal(("INVALID", "外发厂商已停用"), messages[11]);
        var ready = preview.Rows.Single(x => x.RowNumber == 2);
        Assert.Equal(("超穎(泰國)", "OEMA【进料+制造】", new DateOnly(2026, 12, 31)), (ready.Customer, ready.TypeLabel, ready.LatestDueDate));

        // Only planners import; the confirmed import re-checks and stores only READY rows.
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.PreviewImportAsync(viewer, request, ct))).Status);
        var imported = await service.ImportAsync(planner, request, ct);
        Assert.Equal((1, 2, 7), (imported.Created, imported.Skipped, imported.Failed));
        var again = await service.ImportAsync(planner, Request(("2026-11-04", "21089901", "鼎亚"), ("2026-11-06", "21089902", "元控")), ct);
        Assert.Equal((1, 1, 0), (again.Created, again.Skipped, again.Failed));
        Assert.Equal("工令已存在，未覆盖现有资料", again.Rows.Single(x => x.Key == "21089901").Message);

        // An IPMS outage rejects the whole batch; nothing half-imported.
        ipms.Unavailable = true;
        var outage = await Assert.ThrowsAsync<ApiException>(() => service.PreviewImportAsync(planner, Request(("2026-11-04", "21090001", "鼎亚")), ct));
        Assert.Equal((503, "IPMS 暂时无法连接，请稍后再试"), (outage.Status, outage.Message));
        ipms.Unavailable = false;

        // List, filters and tab counts.
        var all = await service.ListAsync(viewer, Query(), 1, 20, 0, ct);
        Assert.Equal(2UL, all.Total);
        Assert.Equal(["21089902", "21089901"], all.List.Select(x => x.WorkOrderNo));
        var first = all.List.Single(x => x.WorkOrderNo == "21089901");
        Assert.Equal((OutsourceMatchStates.Ok, "鼎亚", new DateOnly(2026, 12, 31), "UNSENT", "NORMAL"),
            (first.MatchState, first.VendorName, first.OriginalDueDate, first.SendStatus, first.Status));
        Assert.Equal(oema.Id, first.TypeId);
        Assert.Equal(["21089902"], (await service.ListAsync(viewer, Query(vendorId: yuankong.Id), 1, 20, 0, ct)).List.Select(x => x.WorkOrderNo));
        Assert.Equal(["21089901"], (await service.ListAsync(viewer, Query(typeId: oema.Id), 1, 20, 0, ct)).List.Select(x => x.WorkOrderNo));
        Assert.Equal(["21089902"], (await service.ListAsync(viewer, Query(keyword: "089902"), 1, 20, 0, ct)).List.Select(x => x.WorkOrderNo));
        var summary = await service.SummaryAsync(viewer, false, ct);
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Vendors.Single(x => x.VendorId == dingya.Id).Count);

        // Purchasing edits: version check, type/vendor guard, and a sent row becomes "pending notice".
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateAsync(planner, first.Id, new("2026-11-04", dingya.Id, null, "PAUSED", first.UpdatedAt), ct))).Status);
        var stale = await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateAsync(buyer, first.Id, new("2026-11-04", dingya.Id, null, "PAUSED", first.UpdatedAt.AddSeconds(-1)), ct));
        Assert.Equal(40903, stale.Code);
        var mismatch = await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateAsync(buyer, first.Id, new("2026-11-04", yuankong.Id, null, "NORMAL", first.UpdatedAt), ct));
        Assert.Equal("工令 21089901：OEMA【进料+制造】 不可配元控", mismatch.Message);
        var paused = await service.UpdateAsync(buyer, first.Id, new("2026-11-08", dingya.Id, " 加急 ", "PAUSED", first.UpdatedAt), ct);
        Assert.Equal((new DateOnly(2026, 11, 8), "加急", "PAUSED", "UNSENT"), (paused.OutsourceDate, paused.Remark, paused.Status, paused.SendStatus));
        await database.ExecuteAsync($"UPDATE outsource_items SET send_status='SENT' WHERE id={first.Id}", ct);
        var sent = (await service.ListAsync(buyer, Query(vendorId: dingya.Id), 1, 20, 0, ct)).List.Single();
        var noticed = await service.UpdateAsync(buyer, sent.Id, new("2026-11-08", dingya.Id, "", "PAUSED", sent.UpdatedAt), ct);
        Assert.Equal(("PENDING_NOTICE", (string?)null), (noticed.SendStatus, noticed.Remark));

        // Batch edits only change the given fields.
        var current = (await service.ListAsync(buyer, Query(), 1, 20, 0, ct)).List;
        var batch = await service.BatchUpdateAsync(buyer, new(current.Select(x => new OutsourceItemVersion(x.Id, x.UpdatedAt)).ToArray(),
            null, null, null, "CANCELLED"), ct);
        Assert.All(batch.Items, x => Assert.Equal("CANCELLED", x.Status));
        Assert.Equal(new DateOnly(2026, 11, 8), batch.Items.Single(x => x.WorkOrderNo == "21089901").OutsourceDate);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            service.BatchUpdateAsync(buyer, new([new(first.Id, DateTime.UtcNow)], null, null, null, null), ct))).Status);

        // The mapping is resolved on read: removing it flags the item instead of changing its vendor.
        await settings.SetTypeVendorsAsync(buyer, oema.Id, new([]), ct);
        Assert.Equal(OutsourceMatchStates.VendorMismatch,
            (await service.ListAsync(buyer, Query(vendorId: dingya.Id), 1, 20, 0, ct)).List.Single().MatchState);

        // Export: all columns, cancelled rows grey.
        var export = await service.ExportAsync(viewer, Query(), ct);
        Assert.StartsWith("外协清单_", export.FileName, StringComparison.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(export.Content)))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync(ct);
            Assert.Contains("21089901", sheet, StringComparison.Ordinal);
            Assert.Contains("超穎(泰國)", sheet, StringComparison.Ordinal);
            Assert.Contains("<c r=\"A2\" s=\"3\"", sheet, StringComparison.Ordinal);
        }

        // Deletion: only unsent rows, and only by purchasing.
        var latest = (await service.ListAsync(buyer, Query(), 1, 20, 0, ct)).List;
        var sentRow = latest.Single(x => x.WorkOrderNo == "21089901");
        var unsentRow = latest.Single(x => x.WorkOrderNo == "21089902");
        Assert.Equal("已发送给厂商的记录不能删除，请改为「取消」", (await Assert.ThrowsAsync<ApiException>(() =>
            service.BatchDeleteAsync(buyer, new([new(sentRow.Id, sentRow.UpdatedAt), new(unsentRow.Id, unsentRow.UpdatedAt)]), ct))).Message);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            service.BatchDeleteAsync(planner, new([new(unsentRow.Id, unsentRow.UpdatedAt)]), ct))).Status);
        await service.BatchDeleteAsync(buyer, new([new(unsentRow.Id, unsentRow.UpdatedAt)]), ct);
        Assert.Equal(1UL, (await service.ListAsync(buyer, Query(), 1, 20, 0, ct)).Total);

        // Closed rows are hidden unless asked for and cannot be edited.
        await database.ExecuteAsync($"UPDATE outsource_items SET closed_at=UTC_TIMESTAMP(3) WHERE id={sentRow.Id}", ct);
        Assert.Equal(0UL, (await service.ListAsync(buyer, Query(), 1, 20, 0, ct)).Total);
        var closed = (await service.ListAsync(buyer, Query(includeClosed: true), 1, 20, 0, ct)).List.Single();
        Assert.True(closed.Closed);
        Assert.Equal("已结案的记录不能修改", (await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateAsync(buyer, closed.Id, new("2026-11-08", dingya.Id, null, "NORMAL", closed.UpdatedAt), ct))).Message);

        await using var connection = await database.Database.OpenAsync(ct);
        var actions = (await connection.QueryAsync<string>(
            "SELECT action FROM audit_logs WHERE action LIKE 'OUTSOURCE_ITEM_%' ORDER BY id")).ToArray();
        Assert.Equal(2, actions.Count(x => x == "OUTSOURCE_ITEM_IMPORT"));
        Assert.Equal(1, actions.Count(x => x == "OUTSOURCE_ITEM_DELETE"));
        Assert.Equal(4, actions.Count(x => x == "OUTSOURCE_ITEM_UPDATE"));
    }

    private static OutsourceItemQuery Query(ulong? vendorId = null, ulong? typeId = null, string? keyword = null, bool includeClosed = false) =>
        new(vendorId, typeId, null, null, null, null, keyword, includeClosed);
}
