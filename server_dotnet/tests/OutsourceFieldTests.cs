using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
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
            no, "超穎(泰國)", "ST-201L", "机构甲", "电控乙", new DateOnly(2026, 9, 30),
            1, new DateOnly(2026, 12, 31), false);
    }

    private sealed record Fixture(SchemaShapeTests.SchemaDatabaseScope Database, OutsourceItemService Items, OutsourceFieldService Fields,
        StubIpms Ipms, CurrentUser Planner, CurrentUser Buyer, CurrentUser Mechanic, CurrentUser Admin, OutsourceTestVendors.Vendor Dingya);

    private static async Task<Fixture> CreateAsync(string name, CancellationToken ct)
    {
        var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync(name, ct);
        await database.InitializeBusinessFixtureAsync(ct);
        // 生管, 采购 and 机构 manage the list; only 采购 maintains the settings (fields included); 发信 only mails vendors.
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (70,'生管',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (71,'采购',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (72,'机构',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (73,'发信',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code IN ('outsource','outsource:manage');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 71,id FROM permissions WHERE code IN ('outsource','outsource:manage','outsource:settings');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 72,id FROM permissions WHERE code IN ('outsource','outsource:manage');
            INSERT INTO role_permissions(role_id,permission_id) SELECT 73,id FROM permissions WHERE code IN ('outsource','outsource:send');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9741,'of_planner','unused','生管甲','planner@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9742,'of_buyer','unused','采购乙','buyer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9743,'of_mechanic','unused','机构丙','mechanic@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9740,'of_admin','unused','管理员','admin@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9744,'of_sender','unused','发信丁','sender@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9741,70),(9742,71),(9743,72),(9744,73);
            INSERT INTO user_roles(user_id,role_id) SELECT 9740,id FROM roles WHERE is_built_in=1 AND name='系统管理员';
            """, ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var settings = new OutsourceSettingsService(factory, audit);
        var ipms = new StubIpms();
        var buyer = new CurrentUser(9742, "of_buyer", "INTERNAL", null);
        var dingya = await OutsourceTestVendors.CreateAsync(factory, "鼎亚", ct);
        var oema = await settings.CreateTypeAsync(buyer, new("OEMA", "进料+制造", 1), ct);
        await settings.UpdateTypeSettingsAsync(buyer, oema, ct, vendorIds: [dingya.Id]);
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

    /// <summary>Holds only outsource:send: no column of the list is theirs unless they are named on it.</summary>
    private static readonly CurrentUser Sender = new(9744, "of_sender", "INTERNAL", null);

    [Theory]
    [InlineData("quantity", "-1")]
    [InlineData("quantity", "2.5")]
    [InlineData("workOrderIssuedOn", "2026-02-30")]
    public void ImportRejectsInvalidOptionalNumbersAndDates(string key, string value)
    {
        var item = new OutsourceItem();
        var error = Assert.Throws<ApiException>(() => OutsourceItemService.FillMissingIpmsValue(item, key, value, key));
        Assert.Equal(400, error.Status);
        Assert.Null(item.Quantity);
        Assert.Null(item.WorkOrderIssuedOn);
    }

    [Fact(Timeout = 180_000)]
    public async Task FullColumnImportPreviewsAndStoresOnlyAuthorizedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_full_import", ct);
        await using var scope = f.Database;
        await scope.ExecuteAsync("""
            INSERT INTO outsource_fields(field_key,built_in,label,data_type,options,sort_no,status,editor_scope,vendor_visible,vendor_editable)
            VALUES('deliveryMode',0,'交付方式','SELECT','["自取","送货"]',9999,'ACTIVE','ALL',0,0);
            """, ct);
        f.Ipms.Add("MIXED-01");
        f.Ipms.Orders["MIXED-01"] = f.Ipms.Orders["MIXED-01"] with { MachineModel = null, Quantity = null };

        static OutsourceImportRow Row(int row, string no, Dictionary<string, string?>? fields = null) =>
            new(row, "2026-11-26", no, "鼎亚", "OEMA", fields);
        var rich = Row(2, "MIXED-01", new()
        {
            ["customer"] = "Excel 客户", ["machineModel"] = "EX-305", ["quantity"] = "4",
            ["originalDueDate"] = "2026-12-01", ["latestDueDate"] = "2027-01-05",
            ["remark"] = "提前备料", ["status"] = "暂停", ["deliveryMode"] = "送货",
            ["bomOn"] = "   ", ["drawingReleasedOn"] = null,
        });
        var request = new OutsourceImportRequest([
            rich, Row(3, "FOUR-COLS"), Row(4, "BAD-OPTION", new() { ["deliveryMode"] = "未知" }),
            Row(5, "NO-DATE-GRANT", new() { ["bomOn"] = "2026-12-02" }),
            Row(6, "SYSTEM-VALUE", new() { ["sendStatus"] = "SENT" }),
            Row(7, "BLANK-TYPE") with { TypeName = "" },
        ]);
        var preview = await f.Items.PreviewImportAsync(f.Planner, request, ct);
        Assert.Equal((2, 0, 4), (preview.Ready, preview.Skipped, preview.Invalid));
        var proposed = preview.Rows.Single(x => x.WorkOrderNo == "MIXED-01");
        Assert.Equal(("超穎(泰國)", "EX-305", 4, new DateOnly(2027, 1, 5)),
            (proposed.Customer, proposed.MachineModel, proposed.Quantity, proposed.LatestDueDate));
        Assert.Equal("2026-12-31", proposed.FieldValues!["originalDueDate"]);
        Assert.Equal("送货", proposed.FieldValues["deliveryMode"]);
        Assert.Equal("暂停", proposed.FieldValues["status"]);
        Assert.Empty((await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List);

        var result = await f.Items.ImportAsync(f.Planner, request, ct);
        Assert.Equal((2, 0, 4), (result.Created, result.Skipped, result.Failed));
        var stored = (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List;
        var mixed = stored.Single(x => x.WorkOrderNo == "MIXED-01");
        Assert.Equal((proposed.Customer, proposed.MachineModel, proposed.Quantity, proposed.LatestDueDate),
            (mixed.Customer, mixed.MachineModel, mixed.Quantity, mixed.LatestDueDate));
        Assert.Equal((new DateOnly(2026, 12, 31), "PAUSED", "提前备料", "送货"),
            (mixed.OriginalDueDate, mixed.Status, mixed.Remark, mixed.CustomValues!["deliveryMode"]));
        Assert.Null(mixed.BomOn);
        var legacy = stored.Single(x => x.WorkOrderNo == "FOUR-COLS");
        Assert.Equal("NORMAL", legacy.Status);
        Assert.Null(legacy.Remark);
        Assert.Null(legacy.MachineModel);
        Assert.Null(legacy.IpmsSyncedAt);
        Assert.Equal(2, stored.Count);

        var retry = await f.Items.ImportAsync(f.Planner, new([rich]), ct);
        Assert.Equal((0, 1, 0), (retry.Created, retry.Skipped, retry.Failed));
        Assert.Equal("提前备料", (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List.Single(x => x.Id == mixed.Id).Remark);

        using var sync = new OutsourceIpmsSyncService(EfTestSupport.DbContextFactory(scope.Options), new AuditService([]),
            f.Ipms, scope.Options, NullLogger<OutsourceIpmsSyncService>.Instance);
        await sync.SyncNowAsync(f.Buyer, new([mixed.Id]), ct);
        var sparse = (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List.Single(x => x.Id == mixed.Id);
        Assert.Equal(("EX-305", 4), (sparse.MachineModel, sparse.Quantity));
        f.Ipms.Orders["MIXED-01"] = f.Ipms.Orders["MIXED-01"] with
        {
            MachineModel = "IPMS-501", Quantity = 6, DueDate = new DateOnly(2027, 2, 1),
        };
        await sync.SyncNowAsync(f.Buyer, new([mixed.Id]), ct);
        var refreshed = (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List.Single(x => x.Id == mixed.Id);
        Assert.Equal(("IPMS-501", 6), (refreshed.MachineModel, refreshed.Quantity));
        Assert.Equal((mixed.OriginalDueDate, mixed.LatestDueDate), (refreshed.OriginalDueDate, refreshed.LatestDueDate));

        await scope.ExecuteAsync("INSERT INTO role_permissions(role_id,permission_id) SELECT 70,id FROM permissions WHERE code='outsource:date_import'", ct);
        var dates = new OutsourceImportRequest([Row(2, "WITH-DATES", new()
        {
            ["bomOn"] = "2026-12-02", ["drawingReleasedOn"] = "2026-12-03", ["customer"] = "尚未同步客户",
        })]);
        Assert.Equal(1, (await f.Items.ImportAsync(f.Planner, dates, ct)).Created);
        var dated = (await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List.Single(x => x.WorkOrderNo == "WITH-DATES");
        Assert.Equal((new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3), "尚未同步客户"),
            (dated.BomOn, dated.DrawingReleasedOn, dated.Customer));

        var scoped = new OutsourceImportRequest([Row(2, "RECHECK", new() { ["deliveryMode"] = "自取" })]);
        Assert.Equal(1, (await f.Items.PreviewImportAsync(f.Planner, scoped, ct)).Ready);
        var field = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.Single(x => x.Key == "deliveryMode");
        await f.Fields.UpdateAsync(f.Admin, field.Id, new(null, null, null, null, null, "ROLES", [71]), ct);
        Assert.Equal(1, (await f.Items.ImportAsync(f.Planner, scoped, ct)).Failed);
        Assert.DoesNotContain((await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct)).List, x => x.WorkOrderNo == "RECHECK");
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportUpdatesThePreviewedWorkOrderInPlaceAndSkipsUnchangedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_import_overwrite", ct);
        await using var scope = f.Database;
        await scope.ExecuteAsync("""
            INSERT INTO outsource_fields(field_key,built_in,label,data_type,options,sort_no,status,editor_scope,vendor_visible,vendor_editable)
            VALUES('phase',0,'工期','TEXT',NULL,9999,'ACTIVE','ALL',0,0);
            UPDATE outsource_fields SET vendor_visible=0 WHERE field_key='mechanicalOwner';
            INSERT INTO departments(id,name,kind,sort_no,status,created_at,updated_at) VALUES
              (9001,'部门甲','DIVISION',1,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9002,'部门乙','DIVISION',2,'ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            UPDATE users SET department_id=9001 WHERE id IN (9740,9741);
            UPDATE users SET department_id=9002 WHERE id IN (9742,9743);
            UPDATE departments SET leader_account_id=9740 WHERE id=9001;
            UPDATE departments SET leader_account_id=9742 WHERE id=9002;
            """, ct);
        var original = new OutsourceImportRow(2, "2026-11-26", "WO-KEEP", "鼎亚", "OEMA", new()
        {
            ["remark"] = "待装配", ["phase"] = "第二阶段", ["quantity"] = "12",
            ["customer"] = "预填客户", ["latestDueDate"] = "2027-01-10", ["status"] = "暂停", ["mechanicalOwner"] = "生管甲",
        });
        var same = original with { RowNumber = 4, WorkOrderNo = "WO-SAME", FieldValues = null };
        Assert.Equal(2, (await f.Items.ImportAsync(f.Planner, new([original, same]), ct)).Created);
        async Task<OutsourceItemResponse> GetItem() =>
            (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single(x => x.WorkOrderNo == original.WorkOrderNo);
        var before = await GetItem();
        await scope.ExecuteAsync($"""
            UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id WHERE id={before.Id};
            INSERT INTO oem_transfers(direction,oem_company_id,outsource_item_id,title,lifecycle_status,retention_template_id,created_at,updated_at,concurrency_version)
            VALUES('INTERNAL_TO_OEM',{before.VendorId},{before.Id},'关联传递单','DRAFT',(SELECT MIN(id) FROM oem_retention_templates),UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),0);
            """, ct);
        before = await GetItem();
        var update = original with
        {
            WorkOrderNo = "wo-keep", OutsourceDate = "2026-11-29",
            FieldValues = new() { ["remark"] = "可送货", ["phase"] = " ", ["quantity"] = null, ["latestDueDate"] = "" },
        };
        var create = new OutsourceImportRow(3, "2026-11-26", "WO-NEW", "鼎亚", "OEMA");
        var preview = await f.Items.PreviewImportAsync(f.Buyer, new([update, create, same]), ct);
        Assert.Equal((2, 1, 0), (preview.Ready, preview.Skipped, preview.Invalid));
        Assert.Equal(["READY_UPDATE", "READY", "SKIPPED"], preview.Rows.Select(r => r.Status));
        var proposed = preview.Rows[0];
        Assert.Equal((before.Id, before.UpdatedAt), (proposed.ExpectedItemId, proposed.ExpectedUpdatedAt));
        Assert.Equal((12, before.LatestDueDate, "预填客户"), (proposed.Quantity, proposed.LatestDueDate, proposed.Customer));
        Assert.Equal("可送货", proposed.FieldValues!["remark"]);
        Assert.Equal("第二阶段", proposed.FieldValues["phase"]);
        Assert.Equal(["outsourceDate", "remark"], proposed.ChangedFields!.Order(StringComparer.Ordinal));
        Assert.Equal("待装配", (await GetItem()).Remark);

        var stamped = ConfirmUpdate(update, proposed);
        var confirmation = new OutsourceImportRequest([stamped, create, same]);
        var result = await f.Items.ImportAsync(f.Buyer, confirmation, ct);
        Assert.Equal((1, 1, 1, 0), (result.Created, result.Updated, result.Skipped, result.Failed));
        Assert.Equal(["UPDATED", "CREATED", "SKIPPED"], result.Rows.Select(r => r.Status));
        var after = await GetItem();
        Assert.Equal((before.Id, before.SerialNo, before.ImportedAt, before.ImportedBy),
            (after.Id, after.SerialNo, after.ImportedAt, after.ImportedBy));
        Assert.Equal((new DateOnly(2026, 11, 29), "可送货", "PENDING_NOTICE"), (after.OutsourceDate, after.Remark, after.SendStatus));
        Assert.Equal((12, before.LatestDueDate, "PAUSED", "第二阶段"), (after.Quantity, after.LatestDueDate, after.Status, after.CustomValues!["phase"]));
        await using (var connection = await scope.Database.OpenAsync(ct))
            Assert.Equal(before.Id, await connection.QuerySingleAsync<ulong>("SELECT outsource_item_id FROM oem_transfers WHERE title='关联传递单'"));
        var history = await f.Items.ChangesAsync(f.Buyer, before.Id, ct);
        Assert.Contains(history, change => change.Field == "remark" && change.Source == "EDIT" && change.ActorName == "采购乙"
            && change.OldValue == "待装配" && change.NewValue == "可送货");

        var stale = await Assert.ThrowsAsync<ApiException>(() => f.Items.ImportAsync(f.Buyer,
            new([create with { WorkOrderNo = "WO-UNEXPECTED" }, stamped]), ct));
        Assert.Equal(409, stale.Status);
        Assert.DoesNotContain((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List, x => x.WorkOrderNo == "WO-UNEXPECTED");
        var repeatedPreview = await f.Items.PreviewImportAsync(f.Buyer, new([update, create, same]), ct);
        Assert.Equal((0, 3, 0), (repeatedPreview.Ready, repeatedPreview.Skipped, repeatedPreview.Invalid));
        var replay = await f.Items.ImportAsync(f.Buyer, new([update, create, same]), ct);
        Assert.Equal((0, 0, 3, 0), (replay.Created, replay.Updated, replay.Skipped, replay.Failed));
        Assert.Equal(after.UpdatedAt, (await GetItem()).UpdatedAt);
        Assert.Equal(history.Length, (await f.Items.ChangesAsync(f.Buyer, before.Id, ct)).Length);

        // The owner is hidden from vendors, but its derived supervisor is in their last signed-off snapshot.
        Assert.Equal("管理员", after.Supervisor);
        await scope.ExecuteAsync("""
            UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id, last_sent_snapshot='{"supervisor":"管理员"}'
            WHERE work_order_no='WO-KEEP';
            """, ct);
        var reassign = update with { FieldValues = new() { ["mechanicalOwner"] = "机构丙" } };
        var assignment = Assert.Single((await f.Items.PreviewImportAsync(f.Buyer, new([reassign]), ct)).Rows);
        Assert.Equal("采购乙", assignment.FieldValues!["supervisor"]);
        await f.Items.ImportAsync(f.Buyer, new([ConfirmUpdate(reassign, assignment)]), ct);
        var notified = await GetItem();
        Assert.Equal(("采购乙", "PENDING_NOTICE"), (notified.Supervisor, notified.SendStatus));
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportConfirmationRechecksTheWorkOrderVersionIdentityAndPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_overwrite_guard", ct);
        await using var scope = f.Database;
        var item = await ImportOneAsync(f, "WO-EDIT", ct);
        var change = new OutsourceImportRow(2, "2026-11-04", "WO-EDIT", "鼎亚", "OEMA", new() { ["remark"] = "Excel 新备注" });
        var create = new OutsourceImportRow(3, "2026-11-04", "WO-ATOMIC", "鼎亚", "OEMA");
        async Task<OutsourceImportRow> PreviewUpdate(OutsourceImportRow? input = null)
        {
            var row = input ?? change;
            var candidate = Assert.Single((await f.Items.PreviewImportAsync(f.Planner, new([row]), ct)).Rows);
            Assert.Equal("READY_UPDATE", candidate.Status);
            return ConfirmUpdate(row, candidate);
        }
        async Task AssertConflict(OutsourceImportRow stamped)
        {
            var conflict = await Assert.ThrowsAsync<ApiException>(() => f.Items.ImportAsync(f.Planner, new([create, stamped]), ct));
            Assert.Equal(409, conflict.Status);
            Assert.DoesNotContain((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List, x => x.WorkOrderNo == create.WorkOrderNo);
        }
        var before = await PreviewUpdate();
        await AssertConflict(before with { ExpectedChangeHash = null });
        await AssertConflict(before with { ExpectedChangeHash = new string('0', 64) });
        // Legacy/create-only confirmation cannot overwrite an existing order without its preview identity.
        Assert.Equal(1, (await f.Items.ImportAsync(f.Planner, new([change]), ct)).Skipped);
        await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, "另一采购已修改", item.Status, item.UpdatedAt), ct);
        await AssertConflict(before);
        var file = change with { FieldValues = new() { ["remark"] = "Excel 新备注", ["machineModel"] = "Excel 机型" } };
        var beforeIpms = await PreviewUpdate(file);
        var originalOrder = f.Ipms.Orders["WO-EDIT"];
        f.Ipms.Orders["WO-EDIT"] = originalOrder with { MachineModel = "IPMS-新机型" };
        await AssertConflict(beforeIpms);
        Assert.Equal(originalOrder.MachineModel, (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single().MachineModel);
        f.Ipms.Orders["WO-EDIT"] = originalOrder;
        var latest = await PreviewUpdate();
        var remarkField = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.Single(x => x.Key == "remark");
        await f.Fields.UpdateAsync(f.Admin, remarkField.Id, new(null, null, null, null, null, "ROLES", [71]), ct);
        Assert.Equal(1, (await f.Items.ImportAsync(f.Planner, new([latest]), ct)).Failed);
        Assert.Equal("另一采购已修改", (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single().Remark);
        await f.Fields.UpdateAsync(f.Admin, remarkField.Id, new(null, null, null, null, null, "ALL", null), ct);
        var dateField = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.Single(x => x.Key == "outsourceDate");
        await f.Fields.UpdateAsync(f.Admin, dateField.Id, new(null, null, null, null, null, "ROLES", [71]), ct);
        var deniedDate = change with { OutsourceDate = "2026-11-30" };
        Assert.Equal("INVALID", Assert.Single((await f.Items.PreviewImportAsync(f.Planner, new([deniedDate]), ct)).Rows).Status);
        Assert.Equal("READY_UPDATE", Assert.Single((await f.Items.PreviewImportAsync(f.Planner, new([change]), ct)).Rows).Status);
        await f.Fields.UpdateAsync(f.Admin, dateField.Id, new(null, null, null, null, null, "ALL", null), ct);

        await f.Items.BatchDeleteAsync(f.Buyer, new([new(item.Id, latest.ExpectedUpdatedAt!.Value)]), ct);
        await AssertConflict(latest);
        var replacement = await ImportOneAsync(f, "WO-EDIT", ct);
        Assert.NotEqual(item.Id, replacement.Id);
        await AssertConflict(latest);
        Assert.Null((await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single().Remark);

        var first = await PreviewUpdate();
        var second = await PreviewUpdate(change with { FieldValues = new() { ["remark"] = "另一份导入" } });
        async Task<bool> Submit(OutsourceImportRow input)
        {
            try
            {
                Assert.Equal(1, (await f.Items.ImportAsync(f.Planner, new([input]), ct)).Updated);
                return true;
            }
            catch (ApiException ex) when (ex.Status == 409) { return false; }
        }
        var outcomes = await Task.WhenAll(Submit(first), Submit(second));
        Assert.Single(outcomes, accepted => accepted);
        var final = (await f.Items.ListAsync(f.Buyer, All(), 1, 20, 0, ct)).List.Single();
        Assert.Equal(outcomes[0] ? "Excel 新备注" : "另一份导入", final.Remark);
        var beforeClose = await PreviewUpdate(change with { FieldValues = new() { ["remark"] = "完成前的更新" } });
        await f.Items.SetCompletionAsync(f.Buyer, new([new(final.Id, final.UpdatedAt)], true), ct);
        await AssertConflict(beforeClose);
        Assert.Equal("INVALID", Assert.Single((await f.Items.PreviewImportAsync(f.Planner, new([change]), ct)).Rows).Status);
    }

    private static OutsourceImportRow ConfirmUpdate(OutsourceImportRow row, OutsourceImportPreviewRow preview)
    {
        Assert.NotNull(preview.ExpectedItemId);
        Assert.NotNull(preview.ExpectedUpdatedAt);
        Assert.False(string.IsNullOrWhiteSpace(preview.ExpectedChangeHash));
        return row with { ExpectedItemId = preview.ExpectedItemId, ExpectedUpdatedAt = preview.ExpectedUpdatedAt, ExpectedChangeHash = preview.ExpectedChangeHash };
    }

    [Fact(Timeout = 180_000)]
    public async Task TheMigrationKeepsTodaysColumnsAndAddsAnInternalRobotVColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_seed", ct);
        await using var _ = f.Database;

        var settings = await f.Fields.SettingsAsync(f.Admin, ct);
        Assert.Equal(["outsourceDate", "status", "electricalHandoff", "workOrderNo", "files", "customer", "machineModel", "hasRobotV", "type", "vendor",
                "supervisor", "mechanicalOwner", "electricalOwner", "workOrderIssuedOn", "bomOn", "drawingReleasedOn",
                "originalDueDate", "latestDueDate", "quantity", "remark"],
            settings.Fields.Select(x => x.Key));
        var robotV = settings.Fields.Single(x => x.Key == "hasRobotV");
        // 是否有RobotV is not filled in on the list (StopInternalRobotVEditing), by administrators neither.
        Assert.Equal(("是否有RobotV", "BOOLEAN", false, false, "NONE"), (robotV.Label, robotV.DataType, robotV.VendorVisible, robotV.VendorEditable, robotV.EditorScope));
        // Vendors keep seeing every built-in column; the IPMS columns are never filled in by hand.
        Assert.All(settings.Fields.Where(x => x.BuiltIn), x => Assert.True(x.VendorVisible));
        Assert.Equal("NONE", settings.Fields.Single(x => x.Key == "customer").EditorScope);
        var handoff = settings.Fields.Single(x => x.Key == "electricalHandoff");
        Assert.Equal(("转迅得研发电控设计", "BOOLEAN", true, true, true, "NONE", false),
            (handoff.Label, handoff.DataType, handoff.BuiltIn, handoff.VendorVisibleLocked, handoff.Pinned, handoff.EditorScope, handoff.EditorScopeAllowed));
        Assert.All(settings.Fields.Where(x => x.Key is "bomOn" or "drawingReleasedOn"),
            x => Assert.Equal(("IMPORTED", "NONE", false), (x.Category, x.EditorScope, x.EditorScopeAllowed)));
        Assert.Contains(settings.Roles, x => x.Name == "采购");

        // Everyone who could edit before still can (editor scope ALL), so the layout offers the same columns.
        var layout = await f.Fields.LayoutAsync(f.Buyer, ct);
        // BOM时间 and 发图 are only imported (outsource:date_import), never filled in on the list.
        Assert.Equal(["outsourceDate", "status", "type", "vendor", "latestDueDate", "remark"],
            layout.Fields.Where(x => x.CanEdit).Select(x => x.Key));
        Assert.DoesNotContain((await f.Fields.LayoutAsync(f.Admin, ct)).Fields.Where(x => x.CanEdit),
            x => x.Key is "hasRobotV" or "electricalHandoff");
        // Seeing the list needs no permission; filling a column in does (ALL = outsource:manage).
        Assert.Empty((await f.Fields.LayoutAsync(Sender, ct)).Fields.Where(x => x.CanEdit));
        // Field settings come with the settings permission.
        Assert.NotEmpty((await f.Fields.SettingsAsync(f.Buyer, ct)).Fields);
        Assert.Equal(403, await StatusOfAsync(() => f.Fields.SettingsAsync(f.Mechanic, ct)));
    }

    [Fact(Timeout = 180_000)]
    public async Task TheSettingsPageEnforcesWhatEachKindOfColumnMayChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_fields_rules", ct);
        await using var _ = f.Database;
        var fields = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.ToDictionary(x => x.Key);
        var handoff = fields["electricalHandoff"];
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, handoff.Id,
            new(null, null, null, false, null, null, null), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, handoff.Id,
            new(null, null, null, null, null, "ALL", null), ct)));

        // Columns are not added on the settings page (2026-11-19); a release adds them, as this one stands in for.
        await f.Database.ExecuteAsync("""
            INSERT INTO outsource_fields(field_key,built_in,label,data_type,options,sort_no,status,editor_scope,vendor_visible,vendor_editable)
            VALUES('cdelivery',0,'交付方式','SELECT','["自取","送货"]',9999,'ACTIVE','ALL',0,0)
            """, ct);
        var delivery = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.Single(x => x.Label == "交付方式");
        Assert.Equal((false, false, "ALL", "CUSTOM"), (delivery.VendorVisible, delivery.VendorEditable, delivery.EditorScope, delivery.Category));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, delivery.Id, new(null, "SELECT", [], null, null, null, null), ct)));
        Assert.Equal(409, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, delivery.Id, new("备注", null, null, null, null, null, null), ct)));

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
        Assert.Equal(["outsourceDate", "status", "electricalHandoff", "workOrderNo", hidden.Key], ordered.Fields.Take(5).Select(x => x.Key));
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

        // 机构 fills in 备注. 内部不填写 binds the administrator too; any other setting lets them fill the column in.
        saved = await f.Items.UpdateAsync(f.Mechanic, item.Id, new(null, item.VendorId, null, "机构的备注", saved.Status, saved.UpdatedAt), ct);
        Assert.Equal("机构的备注", saved.Remark);
        Assert.Equal(403, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "是" }), ct)));
        await f.Fields.UpdateAsync(f.Admin, fields["hasRobotV"].Id, new(null, null, null, null, null, "ROLES", [72]), ct);
        saved = (await f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "是" }), ct)).Items.Single();
        Assert.Equal("true", saved.CustomValues!["hasRobotV"]);
        Assert.Equal(400, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["hasRobotV"] = "也许" }), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Admin,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, Values: new() { ["customer"] = "x" }), ct)));

        // 最新交期 only by named people: 机构丙. Any active internal account may be named, with or without a permission.
        var settings = await f.Fields.SettingsAsync(f.Admin, ct);
        Assert.Contains(9741UL, settings.Users!.Select(x => x.Id));
        Assert.Contains(Sender.Id, settings.Users!.Select(x => x.Id));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "USERS", null, [987654]), ct)));
        Assert.Equal(400, await StatusOfAsync(() => f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "USERS", null, []), ct)));
        var named = (await f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "USERS", null, [9743, 9742]), ct)).Fields.Single(x => x.Key == "latestDueDate");
        Assert.Equal("USERS", named.EditorScope);
        Assert.Equal([9742UL, 9743UL], named.EditorUserIds!);
        named = (await f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "USERS", null, [9743]), ct)).Fields.Single(x => x.Key == "latestDueDate");
        Assert.Equal([9743UL], named.EditorUserIds!);
        Assert.Equal(403, await StatusOfAsync(() => f.Items.BatchUpdateAsync(f.Buyer,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, LatestDueDate: "2026-12-01"), ct)));
        saved = (await f.Items.BatchUpdateAsync(f.Mechanic,
            new([new(item.Id, saved.UpdatedAt)], null, null, null, null, null, LatestDueDate: "2026-12-01"), ct)).Items.Single();
        Assert.Equal(new DateOnly(2026, 12, 1), saved.LatestDueDate);
        // Being named is enough: 发信丁 holds no edit permission, fills in 最新交期, and still cannot do anything else.
        named = (await f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "USERS", null, [Sender.Id]), ct)).Fields.Single(x => x.Key == "latestDueDate");
        Assert.Equal(["latestDueDate"], (await f.Fields.LayoutAsync(Sender, ct)).Fields.Where(x => x.CanEdit).Select(x => x.Key));
        saved = await f.Items.UpdateAsync(Sender, item.Id, new(null, item.VendorId, null, saved.Remark, saved.Status, saved.UpdatedAt,
            LatestDueDate: "2026-12-02"), ct);
        Assert.Equal(new DateOnly(2026, 12, 2), saved.LatestDueDate);
        Assert.Equal(403, await StatusOfAsync(() => f.Items.UpdateAsync(Sender, item.Id,
            new(null, item.VendorId, null, "发信的备注", saved.Status, saved.UpdatedAt), ct)));
        Assert.Equal(403, await StatusOfAsync(() => f.Items.SetCompletionAsync(Sender, new([new(item.Id, saved.UpdatedAt)], true), ct)));
        Assert.Equal(403, await StatusOfAsync(() => f.Items.BatchDeleteAsync(Sender, new([new(item.Id, saved.UpdatedAt)]), ct)));
        // Back to everyone: the named people are dropped.
        named = (await f.Fields.UpdateAsync(f.Admin, fields["latestDueDate"].Id,
            new(null, null, null, null, null, "ALL", null, null), ct)).Fields.Single(x => x.Key == "latestDueDate");
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

        // 是否有RobotV is internal by default: filling it in (once opened to staff again) keeps the item sent.
        var fields = (await f.Fields.SettingsAsync(f.Admin, ct)).Fields.ToDictionary(x => x.Key);
        await f.Fields.UpdateAsync(f.Admin, fields["hasRobotV"].Id, new(null, null, null, null, null, "ALL", null), ct);
        item = await f.Items.UpdateAsync(f.Buyer, item.Id, new(null, item.VendorId, null, null, item.Status, item.UpdatedAt,
            Values: new() { ["hasRobotV"] = "true" }), ct);
        Assert.Equal("SENT", item.SendStatus);

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

    [Fact(Timeout = 180_000)]
    public async Task SendersSeeWhatWaitsForThemAndMayDecideNotToMail()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("outsource_send_queue", ct);
        await using var _ = f.Database;
        var sender = new CurrentUser(9744, "of_sender", "INTERNAL", null);
        foreach (var no in new[] { "W1", "W2", "W3", "W4" }) await ImportOneAsync(f, no, ct);
        await f.Database.ExecuteAsync("""
            UPDATE outsource_items SET send_status='SENT', notified_vendor_id=vendor_id WHERE work_order_no='W2';
            UPDATE outsource_items SET send_status='PENDING_NOTICE', notified_vendor_id=vendor_id WHERE work_order_no='W3';
            UPDATE outsource_items SET closed_at=UTC_TIMESTAMP(3) WHERE work_order_no='W4';
            """, ct);
        Dictionary<string, OutsourceItemResponse> Items(PageResponse<OutsourceItemResponse> page) => page.List.ToDictionary(x => x.WorkOrderNo);
        var items = Items(await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct));

        // Only accounts that may send see the count: open items never mailed or changed since the last mail.
        Assert.Equal(2, (await f.Items.SendQueueAsync(sender, ct)).Count);
        Assert.Equal(403, await StatusOfAsync(() => f.Items.SendQueueAsync(f.Buyer, ct)));
        var queue = await f.Items.ListAsync(sender, All() with { SendQueue = true }, 1, 50, 0, ct);
        Assert.Equal(["W1", "W3"], queue.List.Select(x => x.WorkOrderNo).Order(StringComparer.Ordinal));

        // Deciding not to mail needs the send permission and applies only to what waits for a sender.
        Assert.Equal(403, await StatusOfAsync(() => f.Items.SkipNoticeAsync(f.Buyer, new([new(items["W1"].Id, items["W1"].UpdatedAt)]), ct)));
        Assert.Equal(409, await StatusOfAsync(() => f.Items.SkipNoticeAsync(sender, new([new(items["W2"].Id, items["W2"].UpdatedAt)]), ct)));
        // Both carry yellow marks before the decision: W1 was edited by hand, W3 differs from what was mailed.
        var w1 = await f.Items.UpdateAsync(f.Buyer, items["W1"].Id,
            new(null, items["W1"].VendorId, null, "未发送时的备注", items["W1"].Status, items["W1"].UpdatedAt), ct);
        Assert.Contains("remark", w1.ChangedFields!);
        await f.Database.ExecuteAsync("UPDATE outsource_items SET last_sent_snapshot='{\"remark\":\"旧备注\"}' WHERE work_order_no='W3'", ct);
        var w3 = Items(await f.Items.ListAsync(f.Buyer, All(), 1, 50, 0, ct))["W3"];
        Assert.NotEmpty(w3.ChangedFields!);
        var skipped = (await f.Items.SkipNoticeAsync(sender,
            new([new(w1.Id, w1.UpdatedAt), new(w3.Id, w3.UpdatedAt)]), ct)).Items.ToDictionary(x => x.WorkOrderNo);
        Assert.Equal(("NOT_NEEDED", "SENT"), (skipped["W1"].SendStatus, skipped["W3"].SendStatus));
        // The decision is the new baseline: every cell is back to its normal colour.
        Assert.True(skipped["W1"].ChangedFields is null or { Length: 0 });
        Assert.True(skipped["W3"].ChangedFields is null or { Length: 0 });
        Assert.Equal(0, (await f.Items.SendQueueAsync(sender, ct)).Count);
        var history = Assert.Single(await f.Items.ChangesAsync(sender, items["W1"].Id, ct), x => x.Field == "sendStatus");
        Assert.Equal(("发送状态", "未发送", "无需通知"), (history.Label, history.OldValue, history.NewValue));

        // A later edit does not bring a "no mail needed" item back; only a mailed item asks for a new notice.
        var edited = await f.Items.UpdateAsync(f.Buyer, skipped["W1"].Id,
            new(null, skipped["W1"].VendorId, null, "再改一次", skipped["W1"].Status, skipped["W1"].UpdatedAt), ct);
        Assert.Equal("NOT_NEEDED", edited.SendStatus);
        Assert.Equal(["remark"], edited.ChangedFields!);
        edited = await f.Items.UpdateAsync(f.Buyer, skipped["W3"].Id,
            new(null, skipped["W3"].VendorId, null, "再改一次", skipped["W3"].Status, skipped["W3"].UpdatedAt), ct);
        Assert.Equal("PENDING_NOTICE", edited.SendStatus);
        Assert.Equal(["remark"], edited.ChangedFields!);
        Assert.Equal(1, (await f.Items.SendQueueAsync(sender, ct)).Count);
    }
}
