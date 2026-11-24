using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

public sealed class MesMirrorImportTests
{
    private static readonly CurrentUser Editor = new(9821, "mes_import_editor", "INTERNAL", null);
    private static readonly CurrentUser Viewer = new(9822, "mes_import_viewer", "INTERNAL", null);

    [Fact]
    public void ImportAllowsTheMesKeysUsedByThePageAndOutsourcing()
    {
        foreach (var field in new[]
        {
            "workOrder", "customerName", "deviceModels", "planEndDate", "customerEndDate", "productionBatch", "applyEndTime",
            "deviceNum", "deliveryDate", "productionStatus", "delayType", "outMachineStatus", "pickDate",
            "twodReportProgress", "yfdkChargeUser", "zzzkACtualDate", "qjdbzzChargeUser", "kshltWorkorderbgbl",
        })
            Assert.Contains(field, MesMirrorSyncService.ImportFields);
        Assert.DoesNotContain("zzzkActualDate", MesMirrorSyncService.ImportFields);
        Assert.DoesNotContain("seq", MesMirrorSyncService.ImportFields);
    }

    [Theory]
    [InlineData("POST", "/api/v1/outsource/mes-mirror/import", true)]
    [InlineData("POST", "/api/v1/outsource/mes-mirror/import/preview", true)]
    [InlineData("POST", "/api/v1/outsource/mes-mirror/import/preview/other", false)]
    [InlineData("POST", "/api/v1/outsource/mes-mirror/orders/query", false)]
    [InlineData("GET", "/api/v1/outsource/mes-mirror/import", false)]
    public void OnlyTheTwoImportPostsHaveTheLargerBodyLimit(string method, string path, bool allowed)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        Assert.Equal(allowed, OutsourceModule.IsMesImport(request));
        Assert.Equal(64L * 1024 * 1024, OutsourceModule.MesImportRequestLimitBytes);
    }

    [Fact(Timeout = 180_000)]
    public async Task ImportsOnlySelectedConflictsAndMergesPresentColumnsWithoutChangingFetchState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_excel_merge", ct);
        using var service = await PrepareAsync(database, ct);
        await database.ExecuteAsync("""
            INSERT INTO mes_mirror_state(id,last_count,last_success_at,last_trigger) VALUES (1,5000,'2026-11-22 19:00:00','SCHEDULED');
            INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json) VALUES
              ('A','old-a','2026-11-22 19:00:00','旧客户','M-1','{"workOrder":"A","customerName":"旧客户","deviceModels":"M-1","planEndDate":"2026-12-01","deviceNum":15,"mesOnly":{"n":3}}'),
              ('B','old-b','2026-11-22 19:00:00','旧客户B',NULL,'{"workOrder":"B","customerName":"旧客户B"}'),
              ('C','old-c','2026-11-22 19:00:00','留存C','M-3','{"workOrder":"C","customerName":"留存C","deviceModels":"M-3","planEndDate":"2026-12-01","bomReportProgress":100.0}'),
              ('D','old-d','2026-11-22 19:00:00','旧客户D',NULL,'{"workOrder":"D","customerName":"旧客户D"}');
            """, ct);
        var longCustomer = new string('客', 270);
        MesImportRow[] rows =
        [
            Row(2, " A ", ("workOrder", "A"), ("customerName", longCustomer), ("planEndDate", null), ("deviceModels", " \t ")),
            Row(3, "B", ("customerName", "更新B")),
            Row(4, "C", ("customerName", null), ("deviceModels", ""), ("planEndDate", " \t "), ("bomReportProgress", "100")),
            Row(5, "New", ("workOrder", null), ("customerName", ""), ("deviceModels", " \t "), ("pickDate", null),
                ("deviceNum", "3"), ("planEndDate", "2026-12-25")),
            Row(6, "D", ("customerName", "  "), ("deviceModels", "新机型D")),
            Row(7, "Late", ("customerName", "Excel")),
        ];

        var preview = await service.PreviewImportAsync(Editor, new(rows), ct);
        Assert.Equal(2, preview.Added);
        Assert.Equal(1, preview.Unchanged);
        Assert.Empty(preview.Invalid);
        Assert.Empty(preview.Duplicates);
        Assert.Equal(["A", "B", "D"], preview.Existing.Select(x => x.WorkOrder));
        var conflict = preview.Existing[0];
        Assert.Equal(new("旧客户", "M-1", "2026-12-01"), conflict.Current);
        Assert.Equal(new(longCustomer, "M-1", "2026-12-01"), conflict.Excel);
        Assert.Equal(1, conflict.ChangedFields);

        // Another import may add this work order between preview and confirmation.
        await database.ExecuteAsync("""
            INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,raw_json)
            VALUES ('Late','other','2026-11-23 01:00:00','要保留','{"workOrder":"Late","customerName":"要保留"}');
            """, ct);
        var result = await service.ConfirmImportAsync(Editor, new(rows, ["A", "D"], []), ct);
        Assert.Equal((1, 2, 2, 1), (result.Added, result.Overwritten, result.Skipped, result.Unchanged));
        Assert.Empty(result.Invalid);

        await using var connection = await database.Database.OpenAsync(ct);
        var entries = (await connection.QueryAsync<StoredRow>("""
            SELECT work_order_no AS WorkOrder, batch_id AS BatchId, customer_name AS CustomerName,
                   device_models AS DeviceModels, raw_json AS RawJson, plan_end_sort AS PlanEndSort
            FROM mes_work_orders
            """)).ToDictionary(x => x.WorkOrder);
        Assert.Equal(6, entries.Count);
        Assert.Equal("old-b", entries["B"].BatchId);
        Assert.Equal("旧客户B", entries["B"].CustomerName);
        Assert.Equal("old-c", entries["C"].BatchId);
        Assert.Equal("留存C", entries["C"].CustomerName);
        Assert.Equal("M-3", entries["C"].DeviceModels);
        Assert.Equal("2026-12-01", entries["C"].PlanEndSort);
        Assert.Equal("other", entries["Late"].BatchId);
        Assert.Equal("要保留", entries["Late"].CustomerName);
        Assert.Equal(longCustomer[..256], entries["A"].CustomerName);
        Assert.Equal("M-1", entries["A"].DeviceModels);
        Assert.Equal("旧客户D", entries["D"].CustomerName);
        Assert.Equal("新机型D", entries["D"].DeviceModels);
        Assert.StartsWith("excel-", entries["A"].BatchId);
        Assert.Equal(36, entries["A"].BatchId.Length);
        Assert.Equal(entries["A"].BatchId, entries["New"].BatchId);
        Assert.Equal("2026-12-01", entries["A"].PlanEndSort);
        Assert.Equal("2026-12-25", entries["New"].PlanEndSort);
        Assert.Null(entries["New"].CustomerName);
        Assert.Null(entries["New"].DeviceModels);

        using var updated = JsonDocument.Parse(entries["A"].RawJson);
        Assert.Equal(15, updated.RootElement.GetProperty("deviceNum").GetInt32());
        Assert.Equal(3, updated.RootElement.GetProperty("mesOnly").GetProperty("n").GetInt32());
        Assert.Equal(longCustomer, updated.RootElement.GetProperty("customerName").GetString());
        Assert.Equal("2026-12-01", updated.RootElement.GetProperty("planEndDate").GetString());
        Assert.Equal("M-1", updated.RootElement.GetProperty("deviceModels").GetString());
        using var updatedD = JsonDocument.Parse(entries["D"].RawJson);
        Assert.Equal("旧客户D", updatedD.RootElement.GetProperty("customerName").GetString());
        using var created = JsonDocument.Parse(entries["New"].RawJson);
        Assert.Equal("New", created.RootElement.GetProperty("workOrder").GetString());
        Assert.Equal("3", created.RootElement.GetProperty("deviceNum").GetString());
        Assert.Equal(3, created.RootElement.EnumerateObject().Count());

        Assert.Equal(5000, await connection.ExecuteScalarAsync<int>("SELECT last_count FROM mes_mirror_state WHERE id=1"));
        Assert.Equal(new DateTime(2026, 11, 22, 19, 0, 0, DateTimeKind.Unspecified),
            await connection.ExecuteScalarAsync<DateTime>("SELECT last_success_at FROM mes_mirror_state WHERE id=1"));
        Assert.Equal("SCHEDULED", await connection.ExecuteScalarAsync<string>("SELECT last_trigger FROM mes_mirror_state WHERE id=1"));
        using var audit = JsonDocument.Parse(Assert.Single(await connection.QueryAsync<string>(
            "SELECT CAST(detail AS CHAR) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR'")));
        Assert.Equal("EXCEL", audit.RootElement.GetProperty("trigger").GetString());
        Assert.Equal(1, audit.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(2, audit.RootElement.GetProperty("overwritten").GetInt32());
        Assert.Equal(2, audit.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact(Timeout = 180_000)]
    public async Task RequiresAValidChoiceForEachDuplicateAndAppliesTheChosenRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_excel_duplicates", ct);
        using var service = await PrepareAsync(database, ct);
        await database.ExecuteAsync("""
            INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,raw_json)
            VALUES ('A','old',UTC_TIMESTAMP(3),'原客户','{"workOrder":"A","customerName":"原客户"}');
            """, ct);
        MesImportRow[] rows =
        [
            Row(2, "a", ("customerName", "原客户")), Row(3, "A", ("customerName", "选中的更新")),
            Row(4, "NEW", ("deviceModels", "M-1")), Row(5, "new", ("deviceModels", "M-2")),
        ];
        var preview = await service.PreviewImportAsync(Editor, new(rows), ct);
        Assert.Empty(preview.Existing);
        Assert.Equal(2, preview.Duplicates.Length);
        var existing = preview.Duplicates[0];
        Assert.Equal("a", existing.WorkOrder);
        Assert.True(existing.AlreadyExists);
        Assert.Equal([2, 3], existing.Rows.Select(row => row.RowNumber));
        Assert.Null(existing.Rows[0].Existing);
        Assert.Equal("A", existing.Rows[1].Existing?.WorkOrder);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.ConfirmImportAsync(Editor, new(rows, ["A"], []), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            service.ConfirmImportAsync(Editor, new(rows, ["A"], new() { ["a"] = 4, ["NEW"] = 5 }), ct))).Status);

        var imported = await service.ConfirmImportAsync(Editor, new(rows, ["A"], new() { ["a"] = 3, ["NEW"] = 5 }), ct);
        Assert.Equal((1, 1, 2, 0), (imported.Added, imported.Overwritten, imported.Skipped, imported.Unchanged));
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal("选中的更新", await connection.ExecuteScalarAsync<string>("SELECT customer_name FROM mes_work_orders WHERE work_order_no='A'"));
        Assert.Equal("M-2", await connection.ExecuteScalarAsync<string>("SELECT device_models FROM mes_work_orders WHERE work_order_no='new'"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mes_work_orders"));
    }

    [Fact(Timeout = 180_000)]
    public async Task PreviewIsReadOnlyAndConfirmationRejectsActiveLeasesAndUnauthorizedActors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_excel_lease", ct);
        using var service = await PrepareAsync(database, ct);
        await database.ExecuteAsync("INSERT INTO mes_mirror_state(id,lease_until,last_count) VALUES (1,UTC_TIMESTAMP(3)+INTERVAL 10 MINUTE,9000)", ct);
        MesImportRow[] rows = [Row(2, "WO-1", ("customerName", "客户"))];
        Assert.Equal(1, (await service.PreviewImportAsync(Editor, new(rows), ct)).Added);
        var conflict = await Assert.ThrowsAsync<ApiException>(() => service.ConfirmImportAsync(Editor, new(rows, [], []), ct));
        Assert.Equal((409, 40901, "IPMS 资料抓取进行中或有待确认的结果，请稍后再导入"), (conflict.Status, conflict.Code, conflict.Message));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.PreviewImportAsync(Viewer, new(rows), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.ConfirmImportAsync(Viewer, new(rows, [], []), ct))).Status);
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mes_work_orders"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR'"));
        Assert.Equal(9000, await connection.ExecuteScalarAsync<int>("SELECT last_count FROM mes_mirror_state WHERE id=1"));
    }

    [Fact(Timeout = 180_000)]
    public async Task InvalidWorkOrderFieldAndValueAreReportedAndNeverWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_excel_invalid", ct);
        using var service = await PrepareAsync(database, ct);
        MesImportRow[] rows =
        [
            Row(2, new string('X', 65)), Row(3, "BAD-FIELD", ("unknown", "x")),
            Row(4, "BAD-VALUE", ("deviceModels", new string('x', 1001))), Row(5, "  "),
            Row(6, "VALID", ("deviceModels", "M-6")),
        ];
        var preview = await service.PreviewImportAsync(Editor, new(rows), ct);
        Assert.Equal([2, 3, 4, 5], preview.Invalid.Select(row => row.RowNumber));
        Assert.Contains("工单号", preview.Invalid[0].Reason);
        Assert.Contains("unknown", preview.Invalid[1].Reason);
        Assert.Contains("1000", preview.Invalid[2].Reason);
        Assert.Equal(1, preview.Added);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.PreviewImportAsync(Editor, new([]), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.ConfirmImportAsync(Editor, new([], [], []), ct))).Status);
        var imported = await service.ConfirmImportAsync(Editor, new(rows, [], []), ct);
        Assert.Equal((1, 0, 4, 0), (imported.Added, imported.Overwritten, imported.Skipped, imported.Unchanged));
        Assert.Equal(preview.Invalid, imported.Invalid);
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal("VALID", Assert.Single(await connection.QueryAsync<string>("SELECT work_order_no FROM mes_work_orders")));
    }

    private static MesImportRow Row(int rowNumber, string workOrder, params (string Field, string? Value)[] values) =>
        new(rowNumber, workOrder, values.ToDictionary(x => x.Field, x => x.Value, StringComparer.Ordinal));

    private static async Task<MesMirrorSyncService> PrepareAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (80,'IPMS 导入',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (81,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 80,id FROM permissions WHERE code='outsource:settings';
            INSERT INTO role_permissions(role_id,permission_id) SELECT 81,id FROM permissions WHERE code='outsource';
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9821,'mes_import_editor','unused','编辑','editor@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9822,'mes_import_viewer','unused','查看','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9821,80),(9822,81);
            """, ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        return new MesMirrorSyncService(factory, new AuditService([]), new UnconfiguredMesSource(),
            new DbMesSettings(factory, database.Options), database.Options, NullLogger<MesMirrorSyncService>.Instance);
    }

    private sealed record StoredRow(string WorkOrder, string BatchId, string? CustomerName, string? DeviceModels, string RawJson, string PlanEndSort);
}
