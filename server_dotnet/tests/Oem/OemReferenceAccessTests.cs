using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Transfers;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

public sealed class OemReferenceAccessTests
{
    private const string Transfers = "/api/v1/oem/transfers";
    private static readonly string[] DownloadAuditActions = ["OEM_FILE_PREVIEW", "OEM_DOWNLOAD_START", "OEM_DOWNLOAD_COMPLETE"];

    [Fact(Timeout = 180_000)]
    public async Task ReferenceCanPreviewAndDownloadReleasedFileOfBoundTransfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var reference = await LoginReferenceAsync(world, ct);
        var file = await CreateDraftAsync(world, ct);
        await SealAsync(world, file, ct);
        await ReleaseAsync(world, ct);

        using var preview = await reference.Http.GetAsync(FilePath(file.Id) + "/content", ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(file.Bytes, await preview.Content.ReadAsByteArrayAsync(ct));

        var started = await reference.PostAsync(FilePath(file.Id) + "/download-sessions", null, ct).Ok();
        Assert.Equal("REFERENCE", started["purpose"]!.GetValue<string>());
        using var first = new HttpRequestMessage(HttpMethod.Get, FilePath(file.Id) + "/download");
        first.Headers.Range = new RangeHeaderValue(0, 9);
        using var partial = await reference.Http.SendAsync(first, ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(file.Bytes[..10], await partial.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(file.Bytes, await DownloadAsync(reference, file.Id, ct));
        await WaitForCompletionAsync(reference, file.Id, ct);

        await using var db = await host.OpenAsync(ct);
        var audits = await db.QueryAsync<(string Action, string Purpose)>("""
            SELECT action, JSON_UNQUOTE(JSON_EXTRACT(detail, '$.purpose'))
            FROM audit_logs WHERE target_id = @targetId AND target_type = 'oem_file'
              AND action IN ('OEM_FILE_PREVIEW', 'OEM_DOWNLOAD_START', 'OEM_DOWNLOAD_COMPLETE')
            ORDER BY id
            """, new { targetId = file.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Equal(DownloadAuditActions, audits.Select(row => row.Action));
        Assert.All(audits, row => Assert.Equal("REFERENCE", row.Purpose));
    }

    [Fact(Timeout = 180_000)]
    public async Task ReferenceDownloadDoesNotMarkFirstReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var reference = await LoginReferenceAsync(world, ct);
        await world.Admin.PostAsync("/api/v1/oem/retention-templates", new
        {
            name = "Reference retention", mode = RetentionModes.FirstReceiptOrDeadline,
            releaseTtlMinutes = 120, receiptGraceMinutes = 5,
        }, ct).Ok();
        var file = await CreateDraftAsync(world, ct);
        await SealAsync(world, file, ct);
        await ReleaseAsync(world, ct);

        await using var db = await host.OpenAsync(ct);
        const string deadlinesSql = """
            SELECT t.expires_at, f.purge_due_at FROM oem_transfer_files f
            JOIN oem_transfers t ON t.id = f.transfer_id WHERE f.id = @fileId
            """;
        var parameters = new { fileId = file.Id };
        var before = await db.QuerySingleAsync<(DateTime? Transfer, DateTime? File)>(deadlinesSql, parameters);
        Assert.NotNull(before.Transfer);
        Assert.NotNull(before.File);

        var started = await reference.PostAsync(FilePath(file.Id) + "/download-sessions", null, ct).Ok();
        Assert.Equal(file.Bytes, await DownloadAsync(reference, file.Id, ct));
        await WaitForCompletionAsync(reference, file.Id, ct);

        var session = await db.QuerySingleAsync<(string Status, bool RecipientSide)>(
            "SELECT status, recipient_side FROM oem_download_sessions WHERE id = @id",
            new { id = started["downloadSessionId"]!.GetValue<string>() });
        Assert.Equal(("COMPLETED", false), session);
        var receipt = await db.QuerySingleAsync<(DateTime? At, string? Realm, ulong? Id)>("""
            SELECT first_recipient_download_at, first_recipient_realm, first_recipient_id
            FROM oem_transfer_files WHERE id = @fileId
            """, parameters);
        Assert.Equal((null, null, null), receipt);
        Assert.Equal(before, await db.QuerySingleAsync<(DateTime? Transfer, DateTime? File)>(deadlinesSql, parameters));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action = 'OEM_FILE_FIRST_RECEIPT'"));
    }

    [Fact(Timeout = 180_000)]
    public async Task NoPermissionGetsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var file = await CreateDraftAsync(world, ct);
        await SealAsync(world, file, ct);
        await ReleaseAsync(world, ct);

        await AssertForbiddenAsync(world.Viewer, file.Id, ct);
        await AssertForbiddenAsync(world.Viewer, (ulong)long.MaxValue, ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task UnboundOrNotReleasedTransferIsNotReferenceReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var reference = await LoginReferenceAsync(world, ct);
        var file = await CreateDraftAsync(world, ct);

        await AssertForbiddenAsync(reference, file.Id, ct);
        await SealAsync(world, file, ct);
        var pending = await world.Sender.GetAsync($"{Transfers}/{file.TransferId}", ct).Ok();
        Assert.Equal("PENDING", pending["summary"]!["approvalStatus"]!.GetValue<string>());
        await AssertForbiddenAsync(reference, file.Id, ct);
        await ReleaseAsync(world, ct);
        await reference.PostAsync(FilePath(file.Id) + "/download-sessions", null, ct).Ok();

        await using var db = await host.OpenAsync(ct);
        // New sends require a work order. Simulate a released legacy transfer created before it was required.
        await db.ExecuteAsync("UPDATE oem_transfers SET outsource_item_id = NULL WHERE id = @id", new { id = file.TransferId });
        await AssertForbiddenAsync(reference, file.Id, ct);
        await reference.GetAsync(FilePath(file.Id) + "/download", ct).Status(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 180_000)]
    public async Task ReferenceHolderStillDoesNotSeeTheTransferInOemList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var reference = await LoginReferenceAsync(world, ct, canViewTransfers: true);
        var file = await CreateDraftAsync(world, ct);
        await SealAsync(world, file, ct);
        await ReleaseAsync(world, ct);

        var page = await reference.GetAsync(Transfers, ct).Ok();
        Assert.DoesNotContain(page["list"]!.AsArray(), row => row!.Id() == file.TransferId);
        await reference.GetAsync($"{Transfers}/{file.TransferId}", ct).Status(HttpStatusCode.NotFound);
    }

    [Fact(Timeout = 180_000)]
    public async Task VendorBehaviourUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherVendor = await host.CreateVendorAsync(world.Admin, "Other reference vendor", "reference_other_vendor", ct);
        var file = await CreateDraftAsync(world, ct);
        await SealAsync(world, file, ct);
        await ReleaseAsync(world, ct);

        await otherVendor.GetAsync(FilePath(file.Id) + "/content", ct).Status(HttpStatusCode.NotFound);
        await otherVendor.PostAsync(FilePath(file.Id) + "/download-sessions", null, ct).Status(HttpStatusCode.NotFound);
        var started = await world.Vendor.PostAsync(FilePath(file.Id) + "/download-sessions", null, ct).Ok();
        Assert.Equal("DOWNLOAD", started["purpose"]!.GetValue<string>());
        Assert.Equal(file.Bytes, await DownloadAsync(world.Vendor, file.Id, ct));
        await WaitForCompletionAsync(world.Vendor, file.Id, ct);

        await using var db = await host.OpenAsync(ct);
        var receipt = await db.QuerySingleAsync<(DateTime? At, string Realm, ulong? Id)>("""
            SELECT first_recipient_download_at, first_recipient_realm, first_recipient_id
            FROM oem_transfer_files WHERE id = @fileId
            """, new { fileId = file.Id });
        Assert.NotNull(receipt.At);
        Assert.Equal("oem", receipt.Realm);
        Assert.Equal(await db.ExecuteScalarAsync<ulong>("SELECT id FROM oem_accounts WHERE employee_no = 'vendor_out'"), receipt.Id);
    }

    private sealed record DraftFile(ulong TransferId, ulong Version, ulong Id, byte[] Bytes);

    private static string FilePath(ulong id) => $"/api/v1/oem/files/{id}";

    private static async Task<ApiClient> LoginReferenceAsync(OutboundWorld world, CancellationToken ct, bool canViewTransfers = false)
    {
        const string employeeNo = "reference_reader";
        const string password = "Reference#2026";
        string[] permissions = canViewTransfers
            ? [OemPermissions.OutsourceFileView, OemPermissions.TransferView]
            : [OemPermissions.OutsourceFileView];
        await world.Host.CreateInternalUserAsync(employeeNo, password, permissions, null, ct);
        return await world.Host.LoginInternalAsync(employeeNo, password, ct);
    }

    private static async Task<DraftFile> CreateDraftAsync(OutboundWorld world, CancellationToken ct)
    {
        var draft = await CreateOutboundAsync(world, "Reference document", ct);
        var transferId = TransferId(draft);
        var bytes = OemTestHost.Pdf("reference document");
        var file = await world.Host.UploadAsync(world.Sender, transferId, "reference.pdf", bytes, ct);
        return new DraftFile(transferId, Version(draft) + 1, file.Id(), bytes);
    }

    private static async Task SealAsync(OutboundWorld world, DraftFile file, CancellationToken ct)
    {
        var sealedTransfer = await world.Sender.PostAsync($"{Transfers}/{file.TransferId}/send", new { version = file.Version }, ct).Ok();
        Assert.Equal(TransferLifecycle.Sealed, sealedTransfer["summary"]!["lifecycleStatus"]!.GetValue<string>());
        await world.Host.RunOemJobsAsync(ct);
    }

    private static async Task ReleaseAsync(OutboundWorld world, CancellationToken ct)
    {
        var pending = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok())["list"]!.AsArray();
        var task = Assert.Single(pending)!;
        var released = await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve",
            new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal(TransferLifecycle.Released, released["summary"]!["lifecycleStatus"]!.GetValue<string>());
    }

    private static async Task AssertForbiddenAsync(ApiClient client, ulong fileId, CancellationToken ct)
    {
        await client.GetAsync(FilePath(fileId) + "/content", ct).Status(HttpStatusCode.Forbidden);
        await client.PostAsync(FilePath(fileId) + "/download-sessions", null, ct).Status(HttpStatusCode.Forbidden);
    }

    private static async Task WaitForCompletionAsync(ApiClient client, ulong fileId, CancellationToken ct)
    {
        // The HTTP client can receive the last byte before server-side bookkeeping commits.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var session = Assert.Single((await client.GetAsync(FilePath(fileId) + "/download-status", ct).Ok()).AsArray())!;
            var status = session["status"]!.GetValue<string>();
            if (status == "COMPLETED") return;
            Assert.True(DateTime.UtcNow < deadline, $"Download session is still {status} after 10 seconds.");
            await Task.Delay(50, ct);
        }
    }

    private static async Task<byte[]> DownloadAsync(ApiClient client, ulong fileId, CancellationToken ct)
    {
        using var response = await client.Http.GetAsync(FilePath(fileId) + "/download", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
