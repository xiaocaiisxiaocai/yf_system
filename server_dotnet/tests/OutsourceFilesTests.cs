using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class OutsourceFilesTests
{
    private const string Items = "/api/v1/outsource/items";
    private const string VendorItems = "/api/v1/oem/outsource-items";

    [Fact(Timeout = 180_000)]
    public async Task FileCountCountsOnlyReleasedAvailableFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        var file = await AddFileAsync(host, transfer, "available.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        var newer = await AddTransferAsync(host, company, item, ct, direction: TransferDirections.OemToInternal, day: 2);
        var newerFile = await AddFileAsync(host, newer, "newer.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, transfer, "pending.pdf", PayloadStatuses.Available, ValidationStatuses.Pending, ct);
        await AddFileAsync(host, await AddTransferAsync(host, company, null, ct), "unbound.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);

        var row = await RowAsync(admin, item, ct);
        Assert.Equal(2, row["fileCount"]!.GetValue<int>());
        Assert.Equal(0, row["purgedFileCount"]!.GetValue<int>());
        var files = (await admin.GetAsync($"{Items}/{item}/files", ct).Ok())["files"]!.AsArray();
        Assert.Equal([newerFile, file], files.Where(x => x!["state"]!.GetValue<string>() == "AVAILABLE").Select(x => x!.Id("fileId")));
        var latest = files[0]!;
        Assert.Equal((newer, "newer.pdf", 17UL, "OEM_TO_INTERNAL", "档案厂商", new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc)),
            (latest.Id("transferId"), latest["name"]!.GetValue<string>(), latest["sizeBytes"]!.GetValue<ulong>(),
                latest["direction"]!.GetValue<string>(), latest["vendorName"]!.GetValue<string>(), latest["sentAt"]!.GetValue<DateTime>()));

        var updated = await admin.PutAsync($"{Items}/{item}", new { vendorId = company, status = "NORMAL", remark = "已核对", expectedUpdatedAt = row["updatedAt"] }, ct).Ok();
        Assert.Equal(2, updated["fileCount"]!.GetValue<int>());
    }

    [Fact(Timeout = 180_000)]
    public async Task PurgedFilesStayListedAsPurged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        await AddFileAsync(host, transfer, "purged.pdf", PayloadStatuses.Purged, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, transfer, "purge-pending.pdf", PayloadStatuses.PurgePending, ValidationStatuses.Valid, ct);

        Assert.Equal(2, (await RowAsync(admin, item, ct))["purgedFileCount"]!.GetValue<int>());
        await AssertFilesAsync(admin, item, ct, "PURGED", "purge-pending.pdf", "purged.pdf");
    }

    [Fact(Timeout = 180_000)]
    public async Task FileColumnFilterUsesDisplayedIndicatorIncludingMixedAndProcessingOnlyRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, mixed) = await CreateItemAsync(host, ct);
        var mixedTransfer = await AddTransferAsync(host, company, mixed, ct);
        await AddFileAsync(host, mixedTransfer, "live.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, mixedTransfer, "removed.pdf", PayloadStatuses.Purged, ValidationStatuses.Valid, ct);
        var expired = await host.CreateOutsourceItemAsync(company, "FILES-2", ct);
        var expiredFile = await AddFileAsync(host, await AddTransferAsync(host, company, expired, ct),
            "expired.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await using (var db = await host.OpenAsync(ct))
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE oem_transfer_files SET purge_due_at=DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 HOUR) WHERE id=@expiredFile",
                new { expiredFile }, cancellationToken: ct));
        var processing = await host.CreateOutsourceItemAsync(company, "FILES-3", ct);
        await AddFileAsync(host, await AddTransferAsync(host, company, processing, ct),
            "processing.pdf", PayloadStatuses.Available, ValidationStatuses.Pending, ct);
        var empty = await host.CreateOutsourceItemAsync(company, "FILES-4", ct);

        async Task<ulong[]> Filter(string value)
        {
            var filter = Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(new { files = value }));
            var result = await admin.GetAsync($"{Items}?columnFilters={filter}", ct).Ok();
            return result["list"]!.AsArray().Select(row => row!.Id()).Order().ToArray();
        }
        Assert.Equal([mixed], await Filter("available"));
        Assert.Equal([expired], await Filter("purged"));
        Assert.Equal([processing, empty], await Filter("none"));
    }

    [Fact(Timeout = 180_000)]
    public async Task ExpiredFilesNotYetPurgedAreListedAsPurgedAndNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        await AddFileAsync(host, transfer, "live.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        var expired = await AddFileAsync(host, transfer, "expired.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_transfer_files SET purge_due_at=DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 HOUR) WHERE id=@expired", new { expired });

        var row = await RowAsync(admin, item, ct);
        Assert.Equal((1, 1), (row["fileCount"]!.GetValue<int>(), row["purgedFileCount"]!.GetValue<int>()));
        var files = (await admin.GetAsync($"/api/v1/outsource/items/{item}/files", ct).Ok())["files"]!.AsArray();
        Assert.Equal("PURGED", Assert.Single(files, file => file!["name"]!.GetValue<string>() == "expired.pdf")!["state"]!.GetValue<string>());
    }

    [Fact(Timeout = 180_000)]
    public async Task ProcessingFilesAreListedButNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        await AddFileAsync(host, transfer, "promoting.pdf", PayloadStatuses.Promoting, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, transfer, "validating.pdf", PayloadStatuses.Quarantined, ValidationStatuses.Validating, ct);

        Assert.Equal(0, (await RowAsync(admin, item, ct))["fileCount"]!.GetValue<int>());
        await AssertFilesAsync(admin, item, ct, "PROCESSING", "promoting.pdf", "validating.pdf");
    }

    [Fact(Timeout = 180_000)]
    public async Task FailedValidationAndLostFilesAreNotListed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        await AddFileAsync(host, transfer, "invalid.pdf", PayloadStatuses.Available, ValidationStatuses.Invalid, ct);
        await AddFileAsync(host, transfer, "error.pdf", PayloadStatuses.PurgePending, ValidationStatuses.Error, ct);
        await AddFileAsync(host, transfer, "lost.pdf", PayloadStatuses.StorageLost, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, transfer, "missing.pdf", PayloadStatuses.MissingUnverified, ValidationStatuses.Valid, ct);

        var row = await RowAsync(admin, item, ct);
        Assert.Equal(0, row["fileCount"]!.GetValue<int>());
        Assert.Equal(0, row["purgedFileCount"]!.GetValue<int>());
        await AssertFilesAsync(admin, item, ct, "AVAILABLE");
    }

    [Fact(Timeout = 180_000)]
    public async Task DraftAndPendingApprovalFilesAreNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var draft = await AddTransferAsync(host, company, item, ct, TransferLifecycle.Draft);
        var sealedTransfer = await AddTransferAsync(host, company, item, ct, TransferLifecycle.Sealed);
        await AddFileAsync(host, draft, "draft.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, sealedTransfer, "approval.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);

        var row = await RowAsync(admin, item, ct);
        Assert.Equal(0, row["fileCount"]!.GetValue<int>());
        Assert.Equal(0, row["purgedFileCount"]!.GetValue<int>());
        await AssertFilesAsync(admin, item, ct, "AVAILABLE");
    }

    [Fact(Timeout = 180_000)]
    public async Task ListAggregatesInOneQueryForManyItems()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (_, company, first) = await CreateItemAsync(host, ct);
        var itemIds = new List<ulong> { first };
        for (var i = 2; i <= 6; i++) itemIds.Add(await host.CreateOutsourceItemAsync(company, $"FILES-{i}", ct));
        foreach (var item in itemIds)
            await AddFileAsync(host, await AddTransferAsync(host, company, item, ct), "available.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);

        var counter = new FileQueryCounter();
        var factory = new TestContextFactory(new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(host.ConnectionString, EfDb.ServerVersion).AddInterceptors(UtcDatabaseSession.Instance, counter).Options);
        var service = new OutsourceItemService(factory, new AuditService([]), host.Service<IIpmsClient>());
        var query = new OutsourceItemQuery(null, null, null, null, null, null, null, false);
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);

        Assert.Equal(1, Assert.Single((await service.ListAsync(admin, query, 1, 1, 0, ct)).List).FileCount);
        Assert.Equal(1, counter.Count);
        counter.Count = 0;
        var rows = (await service.ListAsync(admin, query, 1, 20, 0, ct)).List;
        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.FileCount));
        Assert.Equal(1, counter.Count);
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalFilesListCanOpenFollowsPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var transfer = await AddTransferAsync(host, company, item, ct);
        await AddFileAsync(host, transfer, "reference.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        const string password = "Files#2026";
        await host.CreateInternalUserAsync("files_list", password, [], null, ct);
        await host.CreateInternalUserAsync("files_open", password, ["outsource:file_view"], null, ct);
        var reader = await host.LoginInternalAsync("files_list", password, ct);
        var opener = await host.LoginInternalAsync("files_open", password, ct);

        var path = $"{Items}/{item}/files";
        var listed = await reader.GetAsync(path, ct).Ok();
        Assert.False(listed["canOpen"]!.GetValue<bool>());
        Assert.Equal("reference.pdf", Assert.Single(listed["files"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.True((await opener.GetAsync(path, ct).Ok())["canOpen"]!.GetValue<bool>());
        await admin.GetAsync($"{Items}/999999/files", ct).Status(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorSeesOnlyOwnCompanyFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, oldCompany, item) = await CreateItemAsync(host, ct);
        var vendor = await host.CreateVendorAsync(admin, "现任档案厂商", "files_current", ct);
        var currentCompany = await host.CompanyIdOfAsync("files_current", ct);
        await AddFileAsync(host, await AddTransferAsync(host, oldCompany, item, ct), "old.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);
        await AddFileAsync(host, await AddTransferAsync(host, oldCompany, item, ct), "old-purged.pdf", PayloadStatuses.Purged, ValidationStatuses.Valid, ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE outsource_items SET vendor_id=@currentCompany WHERE id=@item", new { currentCompany, item });
        await AddFileAsync(host, await AddTransferAsync(host, currentCompany, item, ct), "own.pdf", PayloadStatuses.Available, ValidationStatuses.Valid, ct);

        var row = Assert.Single((await vendor.GetAsync(VendorItems, ct).Ok())["list"]!.AsArray())!;
        Assert.Equal(1, row["fileCount"]!.GetValue<int>());
        Assert.Equal(0, row["purgedFileCount"]!.GetValue<int>());
        var files = await vendor.GetAsync($"{VendorItems}/{item}/files", ct).Ok();
        Assert.True(files["canOpen"]!.GetValue<bool>());
        Assert.Equal("own.pdf", Assert.Single(files["files"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal(2, (await RowAsync(admin, item, ct))["fileCount"]!.GetValue<int>());
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorOtherCompanyItemIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, _, item) = await CreateItemAsync(host, ct);
        var vendor = await host.CreateVendorAsync(admin, "其他档案厂商", "files_other", ct);
        await vendor.GetAsync($"{VendorItems}/{item}/files", ct).Status(HttpStatusCode.NotFound);
        await admin.GetAsync($"{VendorItems}/{item}/files", ct).Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 180_000)]
    public async Task ItemWithTransfersCannotBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, company, item) = await CreateItemAsync(host, ct);
        var other = await host.CreateOutsourceItemAsync(company, "FILES-UNBOUND", ct);
        await AddTransferAsync(host, company, item, ct, TransferLifecycle.Draft);
        var response = await admin.PostAsync($"{Items}/batch-delete", new { items = new[]
        {
            new { id = other, expectedUpdatedAt = (await RowAsync(admin, other, ct))["updatedAt"] },
            new { id = item, expectedUpdatedAt = (await RowAsync(admin, item, ct))["updatedAt"] },
        } }, ct).Status(HttpStatusCode.Conflict, 40901);
        Assert.Equal("工令 FILES-1 已有往来文件，不能删除", response.Json!["message"]!.GetValue<string>());
        Assert.Equal(2, (await admin.GetAsync(Items, ct).Ok())["list"]!.AsArray().Count);
    }

    [Fact(Timeout = 180_000)]
    public async Task FilesColumnIsExcludedFromMailExportAndSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var (admin, _, item) = await CreateItemAsync(host, ct);
        await using var db = await host.Service<IDbContextFactory<YfDbContext>>().CreateDbContextAsync(ct);
        var layout = await OutsourceFieldLayout.LoadAsync(db, ct);
        Assert.Contains(layout.VendorColumns, x => x.Key == "files");
        Assert.DoesNotContain("files", layout.NoticeKeys);
        Assert.Empty(layout.MailBodyFields(["files"]));
        var reference = await OutsourceReference.LoadAsync(db, ct);
        var row = await db.OutsourceItems.SingleAsync(x => x.Id == item, ct);
        await reference.LoadValuesAsync(db, [row], ct);
        var mailFields = layout.MailWorkbookFields();
        Assert.DoesNotContain(mailFields, x => x.Key == "files");
        Assert.DoesNotContain("files", OutsourceWorkbook.Snapshot(reference.Json(row), mailFields).Keys);
        Assert.DoesNotContain("档案", OutsourceItemService.TemplateHeaders);
        Assert.DoesNotContain(layout.WorkbookFields(vendor: true), x => x.Key == "files");

        using var export = await admin.Http.GetAsync($"{Items}/export", ct);
        export.EnsureSuccessStatusCode();
        using var xlsx = new ZipArchive(await export.Content.ReadAsStreamAsync(ct), ZipArchiveMode.Read);
        using var sheet = xlsx.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheet = (await XDocument.LoadAsync(sheet, LoadOptions.None, ct)).Root!;
        var ns = worksheet.Name.Namespace;
        var header = worksheet.Element(ns + "sheetData")!.Elements(ns + "row").First();
        Assert.DoesNotContain("档案", header.Descendants(ns + "t").Select(x => x.Value));
    }

    [Fact(Timeout = 180_000)]
    public async Task FilesColumnAppearsInFieldSettingsAsReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        const string fields = "/api/v1/outsource/settings/fields";
        var listed = (await admin.GetAsync(fields, ct).Ok())["fields"]!.AsArray();
        var field = Assert.Single(listed, x => x!["key"]!.GetValue<string>() == "files")!;
        Assert.Equal("workOrderNo", listed[listed.IndexOf(field) - 1]!["key"]!.GetValue<string>());
        Assert.Equal(("档案", "TEXT", "FIXED", "NONE"), (field["label"]!.GetValue<string>(), field["dataType"]!.GetValue<string>(),
            field["category"]!.GetValue<string>(), field["editorScope"]!.GetValue<string>()));
        Assert.True(field["builtIn"]!.GetValue<bool>());
        Assert.True(field["vendorVisibleLocked"]!.GetValue<bool>());
        Assert.False(field["pinned"]!.GetValue<bool>());
        Assert.False(field["editorScopeAllowed"]!.GetValue<bool>());
        var path = $"{fields}/{field.Id()}";
        await admin.PutAsync(path, new { label = "其他" }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(path, new { vendorVisible = false }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync(path, new { editorScope = "ALL" }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync($"{path}/status", new { status = "DISABLED" }, ct).Status(HttpStatusCode.BadRequest);
    }

    private static async Task<(ApiClient Admin, ulong Company, ulong Item)> CreateItemAsync(OemTestHost host, CancellationToken ct)
    {
        var admin = await host.LoginAdminAsync(ct);
        var company = (await admin.PostAsync("/api/v1/oem/companies", new { name = "档案厂商" }, ct).Ok()).Id();
        return (admin, company, await host.CreateOutsourceItemAsync(company, "FILES-1", ct));
    }

    private static async Task<JsonNode> RowAsync(ApiClient client, ulong id, CancellationToken ct) =>
        (await client.GetAsync(Items, ct).Ok())["list"]!.AsArray().Single(x => x!.Id() == id)!;

    private static async Task AssertFilesAsync(ApiClient client, ulong item, CancellationToken ct, string state, params string[] names)
    {
        var files = (await client.GetAsync($"{Items}/{item}/files", ct).Ok())["files"]!.AsArray();
        Assert.Equal(names, files.Select(x => x!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(files, x => Assert.Equal(state, x!["state"]!.GetValue<string>()));
    }

    private static async Task<ulong> AddTransferAsync(OemTestHost host, ulong company, ulong? item, CancellationToken ct,
        string lifecycle = TransferLifecycle.Released, string direction = TransferDirections.InternalToOem, int day = 1)
    {
        await using var conn = await host.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("""
            INSERT INTO oem_transfers(direction,oem_company_id,outsource_item_id,title,lifecycle_status,retention_template_id,
                sent_at,released_at,created_at,updated_at,concurrency_version)
            VALUES(@direction,@company,@item,'档案测试',@lifecycle,(SELECT MIN(id) FROM oem_retention_templates),
                @sentAt,@releasedAt,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),0);
            SELECT LAST_INSERT_ID();
            """, new { company, item, direction, lifecycle,
                sentAt = lifecycle == TransferLifecycle.Draft ? (DateTime?)null : new DateTime(2026, 11, day),
                releasedAt = lifecycle == TransferLifecycle.Released ? new DateTime(2026, 11, day, 1, 0, 0) : (DateTime?)null }, cancellationToken: ct));
    }

    private static async Task<ulong> AddFileAsync(OemTestHost host, ulong transfer, string name, string payload, string scan, CancellationToken ct)
    {
        await using var conn = await host.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("""
            INSERT INTO oem_transfer_files(transfer_id,original_name,stored_name,ext,size_bytes,sha256,storage_path,
                payload_status,scan_status,purge_attempt_count,concurrency_version,created_at,updated_at)
            VALUES(@transfer,@name,@stored,'pdf',17,REPEAT('a',64),@stored,@payload,@scan,0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            SELECT LAST_INSERT_ID();
            """, new { transfer, name, payload, scan, stored = Guid.NewGuid().ToString("N") }, cancellationToken: ct));
    }

    private sealed class TestContextFactory(DbContextOptions<YfDbContext> options) : IDbContextFactory<YfDbContext>
    {
        public YfDbContext CreateDbContext() => new(options);
    }

    private sealed class FileQueryCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("oem_transfer_files", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("GROUP BY", command.CommandText, StringComparison.OrdinalIgnoreCase);
                Count++;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
