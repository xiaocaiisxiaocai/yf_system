using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Outsource;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class OutsourceContactConsistencyTests
{
    private const string Contacts = "/api/v1/outsource/contacts";
    private const string Portal = "/api/v1/oem/contacts";

    [Fact(Timeout = 180_000)]
    public async Task ContactMatrixUsesConsistentAudiencesWritesAndImportPlans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var vendorA = await host.CreateVendorAsync(admin, "联络甲厂", "ct_fix_a", ct);
        var vendorB = await host.CreateVendorAsync(admin, "联络乙厂", "ct_fix_b", ct);
        var companyA = await host.CompanyIdOfAsync("ct_fix_a", ct);
        var companyB = await host.CompanyIdOfAsync("ct_fix_b", ct);
        var text = await admin.PostAsync(Contacts + "/columns",
            new { title = "交接事项", dataType = "TEXT", vendorVisible = true }, ct).Ok();
        var people = await admin.PostAsync(Contacts + "/columns",
            new { title = "联系人", dataType = "PERSON", vendorVisible = true }, ct).Ok();

        await VerifyVendorSnapshotsAsync(host, admin, vendorA, companyA, companyB, text.Id(), ct);
        await VerifyConcurrentOwnershipAsync(host, admin, vendorA, companyA, companyB, people.Id(), ct);
        await VerifyImportAndLinkageAsync(admin, vendorA, vendorB,
            text["colKey"]!.GetValue<string>(), people["colKey"]!.GetValue<string>(), ct);
    }

    private static async Task VerifyVendorSnapshotsAsync(OemTestHost host, ApiClient admin, ApiClient vendorA,
        ulong companyA, ulong companyB, ulong textColumn, CancellationToken ct)
    {
        await using var db = await host.OpenAsync(ct);
        var vendor = await db.QuerySingleAsync<(ulong AccountId, string SessionId)>(new CommandDefinition("""
            SELECT a.id, t.session_id FROM oem_accounts a
            JOIN oem_refresh_tokens t ON t.account_id=a.id
            WHERE a.employee_no='ct_fix_a' AND t.revoked=0
            """, cancellationToken: ct));
        var actor = new OemAccountActor(vendor.AccountId, "ct_fix_a", companyA, vendor.SessionId);
        var row = await admin.PostAsync(Contacts + "/rows", new { }, ct).Ok();
        foreach (var export in new[] { false, true })
        {
            row = await SetVisibilityAsync(admin, row, "SPECIFIC_VENDORS", [companyA], ct);
            row = await SetTextAsync(admin, row, textColumn, "甲厂原内容", ct);
            var pause = new PauseAfterRead("outsource_contact_row_vendors");
            var service = new OutsourceContactVendorService(Factory(host, pause));
            async Task<string> ReadAsync()
            {
                if (!export) return string.Join(" ", (await service.GetAsync(actor, ct)).Rows.SelectMany(r => r.Values.Values));
                using var zip = new ZipArchive(new MemoryStream((await service.ExportAsync(actor, ct)).Content));
                using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                return await sheet.ReadToEndAsync(ct);
            }
            var pending = ReadAsync();
            try
            {
                await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                row = await SetVisibilityAsync(admin, row, "SPECIFIC_VENDORS", [companyB], ct);
                row = await SetTextAsync(admin, row, textColumn, "乙厂更新内容", ct);
            }
            finally { pause.Resume.TrySetResult(); }

            var result = await pending;
            Assert.Contains("甲厂原内容", result);
            Assert.DoesNotContain("乙厂更新内容", result);
            var fresh = await vendorA.GetAsync(Portal, ct).Ok();
            Assert.DoesNotContain(fresh["rows"]!.AsArray(), r => r!.Id() == row.Id());
        }
    }

    private static async Task VerifyConcurrentOwnershipAsync(OemTestHost host, ApiClient admin, ApiClient vendor,
        ulong companyA, ulong companyB, ulong peopleColumn, CancellationToken ct)
    {
        var row = await admin.PostAsync(Contacts + "/rows", new { }, ct).Ok();
        row = await SetVisibilityAsync(admin, row, "SPECIFIC_VENDORS", [companyA], ct);
        var person = await admin.PostAsync(Contacts + "/people",
            new { scopeType = "INTERNAL", name = "并发联络人", phone = "13800138001" }, ct).Ok();
        await using var db = await host.OpenAsync(ct);
        var adminId = await db.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM users WHERE employee_no='admin'", cancellationToken: ct));
        var pause = new PauseAfterRead("outsource_contact_people");
        var service = new OutsourceContactService(Factory(host, pause), host.Service<AuditService>());
        var edit = service.UpdateCellsAsync(new CurrentUser(adminId, "admin", "INTERNAL", null),
            new(peopleColumn, [new(row.Id(), row["version"]!.GetValue<uint>())], null, [new(person.Id(), null)]), ct);
        Task<ApiResponse>? ownership = null;
        long? lockOwner = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            var name = MySqlNamedLock.Name("contact-matrix", db.Database);
            lockOwner = await db.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT IS_USED_LOCK(@name)", new { name }, cancellationToken: ct));
            ownership = admin.PutAsync(Contacts + "/people/" + person.Id(),
                new { scopeType = "VENDOR", vendorId = companyB, name = "并发联络人", phone = "13800138001" }, ct);
        }
        finally { pause.Resume.TrySetResult(); }

        Assert.Single(await edit);
        await ownership!.Status(HttpStatusCode.BadRequest);
        Assert.NotNull(lockOwner);
        var matrix = await vendor.GetAsync(Portal, ct).Ok();
        Assert.Contains(matrix["rows"]!.AsArray(), r => r!.Id() == row.Id());
    }

    private static async Task VerifyImportAndLinkageAsync(ApiClient admin, ApiClient vendorA, ApiClient vendorB,
        string textKey, string peopleKey, CancellationToken ct)
    {
        const string name = "同一内部联络人";
        const string firstPhone = "13800138002";
        const string secondPhone = "13800138003";
        static object[] Cells(string textKey, string peopleKey, string phone) =>
            [new { colKey = textKey, textValue = "电话联动验收" }, new { colKey = peopleKey, textValue = name + " " + phone }];

        var rows = new[]
        {
            new { rowNumber = 2, visibilityText = "联络甲厂", cells = Cells(textKey, peopleKey, firstPhone) },
            new { rowNumber = 3, visibilityText = "联络乙厂", cells = Cells(textKey, peopleKey, firstPhone) },
        };
        var preview = await admin.PostAsync(Contacts + "/import/preview", new { mode = "REPLACE_ALL", rows }, ct).Ok();
        var stale = preview["previewVersion"]!.GetValue<string>();
        var newRow = await admin.PostAsync(Contacts + "/rows", new { }, ct).Ok();
        await admin.PostAsync(Contacts + "/import", new { mode = "REPLACE_ALL", rows, previewVersion = stale }, ct)
            .Status(HttpStatusCode.Conflict);
        var intact = await admin.GetAsync(Contacts, ct).Ok();
        Assert.Contains(intact["rows"]!.AsArray(), r => r!.Id() == newRow.Id());

        preview = await admin.PostAsync(Contacts + "/import/preview", new { mode = "REPLACE_ALL", rows }, ct).Ok();
        Assert.Equal(intact["rows"]!.AsArray().Count, preview["replaceCount"]!.GetValue<int>());
        var version = preview["previewVersion"]!.GetValue<string>();
        await admin.PostAsync(Contacts + "/import", new { mode = "APPEND", rows, previewVersion = version }, ct)
            .Status(HttpStatusCode.Conflict);
        await admin.PostAsync(Contacts + "/import", new { mode = "REPLACE_ALL", rows = rows.Take(1), previewVersion = version }, ct)
            .Status(HttpStatusCode.Conflict);
        var written = await admin.PostAsync(Contacts + "/import", new { mode = "REPLACE_ALL", rows, previewVersion = version }, ct).Ok();
        Assert.Equal(1, written["createdPeople"]!.GetValue<int>());

        var a = await PersonInPortalAsync(vendorA, peopleKey, firstPhone, ct);
        var b = await PersonInPortalAsync(vendorB, peopleKey, firstPhone, ct);
        Assert.Equal(a, b);
        var updateRows = new[] { new { rowNumber = 2, visibilityText = "仅内部", cells = Cells(textKey, peopleKey, secondPhone) } };
        var phonePreview = await admin.PostAsync(Contacts + "/import/preview", new { mode = "APPEND", rows = updateRows }, ct).Ok();
        Assert.Equal(1, phonePreview["phoneChangeCount"]!.GetValue<int>());
        await admin.PutAsync(Contacts + "/people/" + a, new { scopeType = "INTERNAL", name, phone = "13800138004" }, ct).Ok();
        await admin.PostAsync(Contacts + "/import", new { mode = "APPEND", rows = updateRows, confirmPhoneUpdates = true,
            previewVersion = phonePreview["previewVersion"]!.GetValue<string>() }, ct).Status(HttpStatusCode.Conflict);
        Assert.Equal(a, await PersonInPortalAsync(vendorA, peopleKey, "13800138004", ct));
        phonePreview = await admin.PostAsync(Contacts + "/import/preview", new { mode = "APPEND", rows = updateRows }, ct).Ok();
        var updated = await admin.PostAsync(Contacts + "/import", new { mode = "APPEND", rows = updateRows, confirmPhoneUpdates = true,
            previewVersion = phonePreview["previewVersion"]!.GetValue<string>() }, ct).Ok();
        Assert.Equal(1, updated["updatedPhones"]!.GetValue<int>());
        Assert.Equal(a, await PersonInPortalAsync(vendorA, peopleKey, secondPhone, ct));
        Assert.Equal(a, await PersonInPortalAsync(vendorB, peopleKey, secondPhone, ct));
    }

    private static async Task<ulong> PersonInPortalAsync(ApiClient vendor, string key, string phone, CancellationToken ct)
    {
        var matrix = await vendor.GetAsync(Portal, ct).Ok();
        var row = Assert.Single(matrix["rows"]!.AsArray());
        var person = Assert.Single(row!["people"]![key]!.AsArray());
        Assert.Equal(phone, person!["phone"]!.GetValue<string>());
        return person.Id("personId");
    }

    private static async Task<JsonNode> SetVisibilityAsync(ApiClient admin, JsonNode row, string visibility,
        ulong[] vendorIds, CancellationToken ct) => (await admin.PutAsync(Contacts + "/rows/visibility",
        new { rows = Versions(row), visibility, vendorIds }, ct).Ok()).AsArray().Single()!;

    private static async Task<JsonNode> SetTextAsync(ApiClient admin, JsonNode row, ulong columnId,
        string textValue, CancellationToken ct) => (await admin.PutAsync(Contacts + "/cells",
        new { rows = Versions(row), columnId, textValue }, ct).Ok()).AsArray().Single()!;

    private static object[] Versions(JsonNode row) => [new { rowId = row.Id(), expectedVersion = row["version"]!.GetValue<uint>() }];

    private static IDbContextFactory<YfDbContext> Factory(OemTestHost host, DbCommandInterceptor interceptor) =>
        new TestContextFactory(new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(host.ConnectionString, EfDb.ServerVersion)
            .AddInterceptors(UtcDatabaseSession.Instance, interceptor).Options);

    private sealed class TestContextFactory(DbContextOptions<YfDbContext> options) : IDbContextFactory<YfDbContext>
    {
        public YfDbContext CreateDbContext() => new(options);
    }

    private sealed class PauseAfterRead(string table) : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"`{table}`", StringComparison.Ordinal) && Reached.TrySetResult())
                await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
