using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Directory deletion removes only unreferenced, unsent empty drafts and keeps their audit history.</summary>
public sealed class OemDirectoryVisibilityTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherCompanyDraftOrAbandonedTransferDoesNotBlockDeletingAnEmptyCompany(bool abandon)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);
        var otherCompanyTransfer = await CreateDraftAsync(world.Sender, world.JunCompany, ct);
        if (abandon) await AbandonAsync(world.Sender, otherCompanyTransfer, ct);

        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Ok();
        await world.Admin.DeleteAsync($"/api/v1/oem/companies/{world.YuanCompany}", ct).Ok();

        var remainingAccount = Assert.Single((await world.Admin.GetAsync(
            $"/api/v1/oem/companies/{world.JunCompany}/accounts", ct).Ok()).AsArray())!;
        Assert.Equal("JC001", remainingAccount["employeeNo"]!.GetValue<string>());
        await using var connection = await host.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_companies WHERE id=@id", new { id = world.YuanCompany }));
        Assert.Equal(1, await TransferCountAsync(connection, [TransferId(otherCompanyTransfer)], ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task AccountAndCompanyDeletionCleanOnlyTheirOwnedEmptyDrafts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);

        // Account deletion owns only the vendor-authored direction, in both hidden DRAFT and visible ABANDONED states.
        var inboundDraft = await CreateDraftAsync(world.YuanVendor, world.YuanCompany, ct);
        var inboundAbandoned = await CreateDraftAsync(world.YuanVendor, world.YuanCompany, ct);
        await AbandonAsync(world.YuanVendor, inboundAbandoned, ct);

        // Internal outbound drafts remain until company deletion, again covering both lifecycle states.
        var outboundDraft = await CreateDraftAsync(world.Sender, world.YuanCompany, ct);
        var outboundAbandoned = await CreateDraftAsync(world.Sender, world.YuanCompany, ct);
        await AbandonAsync(world.Sender, outboundAbandoned, ct);

        // Cleanup is always scoped to the selected company.
        var otherCompanyAbandoned = await CreateDraftAsync(world.Sender, world.JunCompany, ct);
        await AbandonAsync(world.Sender, otherCompanyAbandoned, ct);

        var yuanIds = new[]
        {
            TransferId(inboundDraft), TransferId(inboundAbandoned),
            TransferId(outboundDraft), TransferId(outboundAbandoned),
        };
        var accountOwnedIds = new[] { TransferId(inboundDraft), TransferId(inboundAbandoned) };
        var companyOwnedIds = new[] { TransferId(outboundDraft), TransferId(outboundAbandoned) };
        var auditCount = await TransferAuditCountAsync(host, yuanIds, ct);
        Assert.True(auditCount >= 6); // Four creates plus two abandon operations.

        // The administrator sees abandoned history, while both other authors' drafts remain private.
        var listed = await world.Admin.GetAsync($"/api/v1/oem/transfers?companyId={world.YuanCompany}", ct).Ok();
        Assert.Equal(2UL, listed["total"]!.GetValue<ulong>());
        Assert.All(listed["list"]!.AsArray(), row =>
            Assert.Equal("ABANDONED", row!["lifecycleStatus"]!.GetValue<string>()));

        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Ok();

        await using (var afterAccount = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await afterAccount.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM oem_accounts WHERE id=@id", new { id = world.YuanAccount }));
            Assert.Equal(0, await TransferCountAsync(afterAccount, accountOwnedIds, ct));
            Assert.Equal(2, await TransferCountAsync(afterAccount, companyOwnedIds, ct));
            Assert.Equal(1, await TransferCountAsync(afterAccount, [TransferId(otherCompanyAbandoned)], ct));
        }
        Assert.Equal(auditCount, await TransferAuditCountAsync(host, yuanIds, ct));

        await world.Admin.DeleteAsync($"/api/v1/oem/companies/{world.YuanCompany}", ct).Ok();
        await using (var afterCompany = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await afterCompany.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM oem_companies WHERE id=@id", new { id = world.YuanCompany }));
            Assert.Equal(0, await TransferCountAsync(afterCompany, yuanIds, ct));
            Assert.Equal(1, await TransferCountAsync(afterCompany, [TransferId(otherCompanyAbandoned)], ct));
        }
        Assert.Equal(auditCount, await TransferAuditCountAsync(host, yuanIds, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task FailedAccountDeletionRollsBackEligibleCleanupAndKeepsNonEmptyDrafts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);

        var emptyDraft = await CreateDraftAsync(world.YuanVendor, world.YuanCompany, ct);
        var uploadingDraft = await CreateDraftAsync(world.YuanVendor, world.YuanCompany, ct);
        var upload = await world.YuanVendor.PostAsync(
            $"/api/v1/oem/transfers/{TransferId(uploadingDraft)}/uploads/init",
            new { fileName = "pending.pdf", fileSize = (ulong)OemTestHost.Pdf("pending").Length }, ct).Ok();

        var removedFileDraft = await CreateDraftAsync(world.YuanVendor, world.YuanCompany, ct);
        var removedFile = await host.UploadAsync(world.YuanVendor, TransferId(removedFileDraft),
            "removed.pdf", OemTestHost.Pdf("removed"), ct);
        await world.YuanVendor.DeleteAsync($"/api/v1/oem/files/{removedFile.Id()}", ct).Ok();

        var transferIds = new[]
        {
            TransferId(emptyDraft), TransferId(uploadingDraft), TransferId(removedFileDraft),
        };
        var auditCount = await TransferAuditCountAsync(host, transferIds, ct);
        var lastAccount = await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct)
            .Status(HttpStatusCode.Conflict, 40901);
        Assert.Contains("草稿", lastAccount.Json!["message"]!.GetValue<string>());
        await world.Admin.PutAsync($"/api/v1/oem/companies/{world.YuanCompany}/status",
            new { status = "DISABLED" }, ct).Ok();

        // Disabling the vendor lifts the last-account rule, but the account's own history still protects it.
        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Status(HttpStatusCode.BadRequest);

        // Neither failure may partially clean the eligible draft.
        await using var connection = await host.OpenAsync(ct);
        Assert.Equal(3, await TransferCountAsync(connection, transferIds, ct));
        Assert.Equal(world.YuanCompany, await connection.ExecuteScalarAsync<ulong>(
            "SELECT oem_company_id FROM oem_accounts WHERE id=@id", new { id = world.YuanAccount }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_upload_sessions WHERE id=@id", new { id = upload["sessionId"]!.GetValue<string>() }));
        Assert.Equal("FILE_REMOVED", await connection.ExecuteScalarAsync<string>(
            "SELECT purge_reason FROM oem_transfer_files WHERE id=@id", new { id = removedFile.Id() }));
        Assert.Equal(auditCount, await TransferAuditCountAsync(connection, transferIds, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task FailedCompanyDeletionRollsBackItsEmptyDraftCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await SetupAsync(host, ct);

        var emptyDraft = await CreateDraftAsync(world.Sender, world.YuanCompany, ct);
        var uploadingDraft = await CreateDraftAsync(world.Sender, world.YuanCompany, ct);
        var upload = await world.Sender.PostAsync(
            $"/api/v1/oem/transfers/{TransferId(uploadingDraft)}/uploads/init",
            new { fileName = "company-pending.pdf", fileSize = (ulong)OemTestHost.Pdf("pending").Length }, ct).Ok();
        var transferIds = new[] { TransferId(emptyDraft), TransferId(uploadingDraft) };

        await world.Admin.PutAsync($"/api/v1/oem/companies/{world.YuanCompany}/status",
            new { status = "DISABLED" }, ct).Ok();
        await world.Admin.DeleteAsync($"/api/v1/oem/accounts/{world.YuanAccount}", ct).Ok();
        var companyRejected = await world.Admin.DeleteAsync($"/api/v1/oem/companies/{world.YuanCompany}", ct)
            .Status(HttpStatusCode.BadRequest);
        Assert.Contains("草稿", companyRejected.Json!["message"]!.GetValue<string>());

        await using var connection = await host.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_companies WHERE id=@id", new { id = world.YuanCompany }));
        Assert.Equal(2, await TransferCountAsync(connection, transferIds, ct));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM oem_upload_sessions WHERE id=@id", new { id = upload["sessionId"]!.GetValue<string>() }));
    }

    private sealed record World(
        ApiClient Admin, ApiClient Sender, ApiClient YuanVendor,
        ulong JunCompany, ulong YuanCompany, ulong YuanAccount);

    private static async Task<World> SetupAsync(OemTestHost host, CancellationToken ct)
    {
        var admin = await host.LoginAdminAsync(ct);
        var jun = (await admin.PostAsync("/api/v1/oem/companies", new { name = "骏创" }, ct).Ok()).Id();
        var yuan = (await admin.PostAsync("/api/v1/oem/companies", new { name = "元控" }, ct).Ok()).Id();
        // Reverse account creation order so company ids and account ids are not interchangeable.
        var yuanAccount = (await admin.PostAsync($"/api/v1/oem/companies/{yuan}/accounts",
            new { employeeNo = "YK001", password = "Vendor#2026" }, ct).Ok()).Id();
        await admin.PostAsync($"/api/v1/oem/companies/{jun}/accounts",
            new { employeeNo = "JC001", password = "Vendor#2026" }, ct).Ok();
        Assert.NotEqual(yuan, yuanAccount);

        var yuanVendor = await host.LoginOemAsync("YK001", "Vendor#2026", ct);
        await yuanVendor.PutAsync("/api/v1/oem/auth/password",
            new { oldPassword = "Vendor#2026", newPassword = "Vendor#2027x" }, ct).Ok();
        yuanVendor = await host.LoginOemAsync("YK001", "Vendor#2027x", ct);

        var org = await host.CreateOrgPathAsync("删除验收", ct);
        await host.CreateInternalUserAsync("draft_author", "Sender#2026x", ["oem:transfer_create"], org.Section, ct);
        return new(admin, await host.LoginInternalAsync("draft_author", "Sender#2026x", ct),
            yuanVendor, jun, yuan, yuanAccount);
    }

    private static Task<JsonNode> CreateDraftAsync(ApiClient author, ulong companyId, CancellationToken ct) =>
        author.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = companyId }, ct).Ok();

    private static async Task AbandonAsync(ApiClient author, JsonNode draft, CancellationToken ct) =>
        await author.DeleteAsync($"/api/v1/oem/transfers/{TransferId(draft)}?version={Version(draft)}", ct).Ok();

    private static async Task<int> TransferCountAsync(
        MySqlConnector.MySqlConnection connection, IReadOnlyCollection<ulong> ids, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM oem_transfers WHERE id IN @ids", new { ids = ids.ToArray() }, cancellationToken: ct));

    private static async Task<int> TransferAuditCountAsync(
        OemTestHost host, IReadOnlyCollection<ulong> ids, CancellationToken ct)
    {
        await using var connection = await host.OpenAsync(ct);
        return await TransferAuditCountAsync(connection, ids, ct);
    }

    private static async Task<int> TransferAuditCountAsync(
        MySqlConnector.MySqlConnection connection, IReadOnlyCollection<ulong> ids, CancellationToken ct)
    {
        var targets = ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE target_type='oem_transfer' AND target_id IN @targets",
            new { targets }, cancellationToken: ct));
    }
}
