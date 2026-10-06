using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Tests.Oem;

/// <summary>End-to-end transfer workflows: upload → validate → promote → approve/release, and every fail-closed path.</summary>
public sealed class OemTransferFlowTests
{
    internal sealed record OutboundWorld(OemTestHost Host, ApiClient Admin, ApiClient Sender, ApiClient Leader, ApiClient Vendor,
        ApiClient Viewer, ulong CompanyId, ulong KeepTemplateId, (ulong Division, ulong Department, ulong Section) Org, ulong LeaderId, ulong SenderId);

    internal static async Task<OutboundWorld> OutboundWorldAsync(OemTestHost host, CancellationToken ct)
    {
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("装配", ct);
        var leaderId = await host.CreateInternalUserAsync("zp_leader", "Leader#2026x", ["oem:flow_approve", "oem:transfer_view"], org.Section, ct);
        var senderId = await host.CreateInternalUserAsync("zp_sender", "Sender#2026x", ["oem:transfer_create"], org.Section, ct);
        await host.CreateInternalUserAsync("zp_viewer", "Viewer#2026x", ["oem:transfer_view", "oem:file_download"], org.Section, ct);
        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = leaderId }, ct).Ok();
        var vendor = await host.CreateVendorAsync(admin, "精工代工", "vendor_out", ct);
        var companyId = await host.CompanyIdOfAsync("vendor_out", ct);
        var keep = (await admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray().Single(item => item!["mode"]!.GetValue<string>() == "KEEP")!.Id();
        return new OutboundWorld(host, admin,
            await host.LoginInternalAsync("zp_sender", "Sender#2026x", ct),
            await host.LoginInternalAsync("zp_leader", "Leader#2026x", ct),
            vendor,
            await host.LoginInternalAsync("zp_viewer", "Viewer#2026x", ct),
            companyId, keep, org, leaderId, senderId);
    }

    internal static async Task<JsonNode> CreateOutboundAsync(OutboundWorld world, string title, CancellationToken ct) =>
        await world.Sender.PostAsync("/api/v1/oem/transfers",
            new { title, description = "图纸第一版", oemCompanyId = world.CompanyId, retentionTemplateId = world.KeepTemplateId }, ct).Ok();

    internal static ulong TransferId(JsonNode detail) => detail["summary"]!.Id();
    internal static ulong Version(JsonNode detail) => detail["summary"]!["version"]!.GetValue<ulong>();
    private static string Lifecycle(JsonNode detail) => detail["summary"]!["lifecycleStatus"]!.GetValue<string>();

    private static async Task<byte[]> DownloadAsync(ApiClient client, ulong fileId, CancellationToken ct)
    {
        await client.PostAsync($"/api/v1/oem/files/{fileId}/download-sessions", null, ct).Ok();
        using var response = await client.Http.GetAsync($"/api/v1/oem/files/{fileId}/download", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    [Fact]
    public void GeneratedTitleKeepsTheMultiFileSuffixAndDoesNotSplitUnicodeScalars()
    {
        var longName = string.Concat(Enumerable.Repeat("😀", 140)) + ".pdf";
        var title = OemTransferService.GeneratedTitle(
        [
            new OemTransferFile { Id = 1, OriginalName = longName },
            new OemTransferFile { Id = 2, OriginalName = "second.step" },
        ]);

        Assert.Equal(128, title.EnumerateRunes().Count());
        Assert.EndsWith(" 等2个文件", title);
        Assert.DoesNotContain('\uFFFD', title);
    }

    [Fact(Timeout = 240_000)]
    public async Task OutboundTransferIsValidatedApprovedReleasedAndDownloadableWithoutAScanner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new { oemCompanyId = world.CompanyId }, ct).Ok();
        var id = TransferId(draft);
        Assert.Equal(OemTransferService.DraftTitlePlaceholder, draft["summary"]!["title"]!.GetValue<string>());

        // Drafts are private to their author.
        await world.Viewer.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        var content = OemTestHost.Pdf("chassis drawing");
        var file = await host.UploadAsync(world.Sender, id, "chassis.pdf", content, ct);
        Assert.Equal("QUARANTINED", file["payloadStatus"]!.GetValue<string>());
        Assert.Equal(ValidationStatuses.Pending, file["validationStatus"]!.GetValue<string>());

        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Status(HttpStatusCode.Conflict);
        var sent = await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) }, ct).Ok();
        Assert.Equal("SEALED", Lifecycle(sent));
        Assert.Equal("WAITING_FILES", sent["summary"]!["approvalStatus"]!.GetValue<string>());
        Assert.Equal("chassis.pdf", sent["summary"]!["title"]!.GetValue<string>());
        // Frozen after sending.
        await world.Sender.DeleteAsync($"/api/v1/oem/files/{file.Id()}", ct).Status(HttpStatusCode.Conflict);

        // The vendor learns nothing before release; the approver cannot act before validation.
        Assert.Empty((await world.Vendor.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray());
        await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        Assert.Empty((await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok()).AsArray());

        await host.RunOemJobsAsync(ct);
        var pending = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok()).AsArray();
        var task = Assert.Single(pending)!;
        var detail = await world.Leader.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("PENDING", detail["summary"]!["approvalStatus"]!.GetValue<string>());
        Assert.Equal("VALID", detail["summary"]!["validationSummary"]!.GetValue<string>());
        Assert.Equal(ValidationStatuses.Valid, detail["files"]![0]!["validationStatus"]!.GetValue<string>());
        Assert.Equal(0, detail["files"]![0]!["validationAttempts"]!.GetValue<int>());
        Assert.Equal("文件校验通过", detail["files"]![0]!["validationMessage"]!.GetValue<string>());
        var validationStatus = await world.Leader.GetAsync($"/api/v1/oem/files/{file.Id()}/validation-status", ct).Ok();
        Assert.Equal(ValidationStatuses.Valid, validationStatus["validationStatus"]!.GetValue<string>());
        Assert.Equal("REVIEW", detail["capabilities"]!["contentPurpose"]!.GetValue<string>());
        Assert.True(detail["files"]![0]!["downloadable"]!.GetValue<bool>());

        // Only the assignee decides; a stale version is refused.
        await world.Viewer.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct)
            .Status(HttpStatusCode.Forbidden);
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() + 3 }, ct)
            .Status(HttpStatusCode.Conflict);
        var released = await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("RELEASED", Lifecycle(released));
        Assert.Equal("APPROVED", released["summary"]!["approvalStatus"]!.GetValue<string>());

        // Now the vendor sees it and is a recipient; approval internals stay hidden from it.
        var vendorView = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("RECIPIENT", vendorView["capabilities"]!["contentPurpose"]!.GetValue<string>());
        Assert.Null(vendorView["approval"]);
        Assert.True(vendorView["files"]![0]!["downloadable"]!.GetValue<bool>());
        Assert.Equal(content, await DownloadAsync(world.Vendor, file.Id(), ct));
        // The sender holds only transfer_create: it sees the result but reads content only with view + download.
        var senderView = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("NONE", senderView["capabilities"]!["contentPurpose"]!.GetValue<string>());

        await using var conn = await host.OpenAsync(ct);
        Assert.Equal("AVAILABLE", await conn.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@id", new { id = file.Id() }));
        Assert.StartsWith("available/", await conn.ExecuteScalarAsync<string>("SELECT storage_path FROM oem_transfer_files WHERE id=@id", new { id = file.Id() }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_TRANSFER_RELEASE' AND target_id=@id",
            new { id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
    }

    [Fact(Timeout = 240_000)]
    public async Task DraftCannotOverrideEffectiveRetentionAndSendKeepsItsPolicySnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);

        var firstPolicy = await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "发布后 1 小时", mode = "AFTER_RELEASE", releaseTtlMinutes = 60 }, ct).Ok();
        var draft = await world.Sender.PostAsync("/api/v1/oem/transfers", new
        {
            title = "策略原题",
            oemCompanyId = world.CompanyId,
            retentionTemplateId = world.KeepTemplateId,
        }, ct).Ok();
        var id = TransferId(draft);
        Assert.Equal(firstPolicy.Id(), draft["retention"]!["templateId"]!.GetValue<ulong>());

        var keep = (await world.Admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray()
            .Single(item => item!.Id() == world.KeepTemplateId)!;
        await world.Admin.PutAsync($"/api/v1/oem/retention-templates/{world.KeepTemplateId}", new
        {
            name = keep["name"]!.GetValue<string>(),
            mode = "KEEP",
            status = "ACTIVE",
            version = keep["version"]!.GetValue<ulong>(),
        }, ct).Ok();
        draft = await world.Sender.PutAsync($"/api/v1/oem/transfers/{id}", new
        {
            description = "只更新说明",
            retentionTemplateId = firstPolicy.Id(),
            version = Version(draft),
        }, ct).Ok();
        Assert.Equal(world.KeepTemplateId, draft["retention"]!["templateId"]!.GetValue<ulong>());
        Assert.Equal("策略原题", draft["summary"]!["title"]!.GetValue<string>());

        var sendPolicy = await world.Admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "发布后 2 小时", mode = "AFTER_RELEASE", releaseTtlMinutes = 120 }, ct).Ok();
        await host.UploadAsync(world.Sender, id, "policy.pdf", OemTestHost.Pdf("policy snapshot"), ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        var sent = await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) }, ct).Ok();
        Assert.Equal(sendPolicy.Id(), sent["retention"]!["templateId"]!.GetValue<ulong>());
        Assert.Equal("AFTER_RELEASE", sent["retention"]!["mode"]!.GetValue<string>());
        Assert.Equal(120U, sent["retention"]!["releaseTtlMinutes"]!.GetValue<uint>());

        keep = (await world.Admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray()
            .Single(item => item!.Id() == world.KeepTemplateId)!;
        await world.Admin.PutAsync($"/api/v1/oem/retention-templates/{world.KeepTemplateId}", new
        {
            name = keep["name"]!.GetValue<string>(),
            mode = "KEEP",
            status = "ACTIVE",
            version = keep["version"]!.GetValue<ulong>(),
        }, ct).Ok();
        var unchanged = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal(sendPolicy.Id(), unchanged["retention"]!["templateId"]!.GetValue<ulong>());
        Assert.Equal("AFTER_RELEASE", unchanged["retention"]!["mode"]!.GetValue<string>());
        Assert.Equal(120U, unchanged["retention"]!["releaseTtlMinutes"]!.GetValue<uint>());
    }

    [Fact(Timeout = 240_000)]
    public async Task RejectionAndInvalidContentFailuresCloseTransfersWithoutRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);

        // Rejection requires a reason and terminates the transfer.
        var rejected = await CreateOutboundAsync(world, "待驳回", ct);
        var rejectedId = TransferId(rejected);
        await host.UploadAsync(world.Sender, rejectedId, "a.pdf", OemTestHost.Pdf("a"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{rejectedId}/send", new { version = Version(rejected) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var task = (await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok()).AsArray().Single()!;
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/reject", new { version = task["version"]!.GetValue<ulong>() }, ct)
            .Status(HttpStatusCode.BadRequest);
        var closed = await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/reject",
            new { version = task["version"]!.GetValue<ulong>(), reason = "版本不对" }, ct).Ok();
        Assert.Equal("REJECTED", Lifecycle(closed));
        Assert.Equal("NONE", closed["capabilities"]!["contentPurpose"]!.GetValue<string>());
        Assert.NotNull(closed["files"]![0]!["purgeDueAt"]!.GetValue<DateTime?>());

        // A file whose content does not match its extension is invalid and blocks sending.
        var draft = await CreateOutboundAsync(world, "格式无效草稿", ct);
        var draftId = TransferId(draft);
        await host.UploadAsync(world.Sender, draftId, "disguised.pdf", [0x4D, 0x5A, 0x90, 0x00, 0x6E, 0x6F, 0x74], ct);
        await host.RunOemJobsAsync(ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{draftId}", ct).Ok();
        Assert.Equal(ValidationStatuses.Invalid, draft["files"]![0]!["validationStatus"]!.GetValue<string>());
        Assert.Contains("可执行程序", draft["files"]![0]!["validationMessage"]!.GetValue<string>());
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{draftId}/send", new { version = Version(draft) }, ct).Status(HttpStatusCode.BadRequest);
        // Removing it and adding a valid file makes the draft sendable again.
        await world.Sender.DeleteAsync($"/api/v1/oem/files/{draft["files"]![0]!.Id()}", ct).Ok();
        await host.UploadAsync(world.Sender, draftId, "valid.pdf", OemTestHost.Pdf("valid"), ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{draftId}", ct).Ok();
        Assert.Single(draft["files"]!.AsArray());

        // Same-size content tampering discovered after sending blocks the transfer and cancels approval.
        var late = await CreateOutboundAsync(world, "发送后发现摘要篡改", ct);
        var lateId = TransferId(late);
        var lateFile = await host.UploadAsync(world.Sender, lateId, "late.pdf", OemTestHost.Pdf("original"), ct);
        await using (var conn = await host.OpenAsync(ct))
        {
            var relative = (await conn.ExecuteScalarAsync<string>(
                "SELECT storage_path FROM oem_transfer_files WHERE id=@id", new { id = lateFile.Id() }))!;
            var path = Path.Combine(host.StorageRoot, "oem", relative);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            bytes[^2] ^= 0x5A;
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{lateId}/send", new { version = Version(late) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var blocked = await world.Sender.GetAsync($"/api/v1/oem/transfers/{lateId}", ct).Ok();
        Assert.Equal("BLOCKED", Lifecycle(blocked));
        Assert.Equal("CANCELLED", blocked["summary"]!["approvalStatus"]!.GetValue<string>());
        Assert.Equal(ValidationStatuses.Invalid, blocked["files"]![0]!["validationStatus"]!.GetValue<string>());
        Assert.Contains("SHA-256", blocked["files"]![0]!["validationMessage"]!.GetValue<string>());

        // Invalid format and encrypted archives remain blocked in quarantine.
        var mismatch = await CreateOutboundAsync(world, "非法归档", ct);
        var mismatchId = TransferId(mismatch);
        await host.UploadAsync(world.Sender, mismatchId, "secret.zip", OemInspectionTests.EncryptedZip(), ct);
        await host.RunOemJobsAsync(ct);
        var inspected = await world.Sender.GetAsync($"/api/v1/oem/transfers/{mismatchId}", ct).Ok();
        var invalidArchive = Assert.Single(inspected["files"]!.AsArray())!;
        Assert.Equal(ValidationStatuses.Invalid, invalidArchive["validationStatus"]!.GetValue<string>());
        Assert.Contains("加密", invalidArchive["validationMessage"]!.GetValue<string>());
        Assert.Equal("QUARANTINED", invalidArchive["payloadStatus"]!.GetValue<string>());
    }

    [Fact(Timeout = 240_000)]
    public async Task InboundTransfersNeedNoApprovalAndStayInsideTheVendorScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var otherVendor = await host.CreateVendorAsync(world.Admin, "另一家代工", "vendor_other", ct);
        await host.CreateInternalUserAsync("view_only", "ViewOnly#2026x", ["oem:transfer_view"], world.Org.Section, ct);
        var viewOnly = await host.LoginInternalAsync("view_only", "ViewOnly#2026x", ct);

        // The OEM account cannot pick another vendor.
        await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "越权", oemCompanyId = world.CompanyId + 99, retentionTemplateId = world.KeepTemplateId }, ct)
            .Status(HttpStatusCode.BadRequest);
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "回传工艺文件", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var id = TransferId(draft);
        Assert.Equal("OEM_TO_INTERNAL", draft["summary"]!["direction"]!.GetValue<string>());
        // Extension whitelist for the inbound direction.
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init", new { fileName = "tool.exe", fileSize = 10 }, ct).Status(HttpStatusCode.BadRequest);
        await host.UploadAsync(world.Vendor, id, "process.pdf", OemTestHost.Pdf("process"), ct);
        var sent = await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        Assert.Equal("NOT_REQUIRED", sent["summary"]!["approvalStatus"]!.GetValue<string>());

        // Sealed: internal viewers see metadata only; content is locked until validation finishes.
        var early = await world.Viewer.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("NONE", early["capabilities"]!["contentPurpose"]!.GetValue<string>());
        await host.RunOemJobsAsync(ct);
        var released = await world.Viewer.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("RELEASED", Lifecycle(released));
        Assert.Equal("RECIPIENT", released["capabilities"]!["contentPurpose"]!.GetValue<string>());
        Assert.Equal(OemTestHost.Pdf("process"), await DownloadAsync(world.Viewer, released["files"]![0]!.Id(), ct));
        // View without download: metadata only.
        var metadataOnly = await viewOnly.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.False(metadataOnly["files"]![0]!["downloadable"]!.GetValue<bool>());

        // Another vendor and the supplier line never see it.
        Assert.Empty((await otherVendor.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray());
        await otherVendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        await host.CreateSupplierUserAsync("supplier_y", "Supplier#2026", ct);
        var supplier = await host.LoginInternalAsync("supplier_y", "Supplier#2026", ct);
        await supplier.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.Forbidden, 40304);
    }

    [Fact(Timeout = 240_000)]
    public async Task LeaderSelfSendSkipsApprovalAndDirectStaffCannotSend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("INSERT INTO role_permissions(role_id,permission_id) SELECT ur.role_id,p.id FROM user_roles ur JOIN permissions p ON p.code='oem:transfer_create' WHERE ur.user_id=@id",
                new { id = world.LeaderId });
        var draft = await world.Leader.PostAsync("/api/v1/oem/transfers",
            new { title = "主管自发", oemCompanyId = world.CompanyId, retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var id = TransferId(draft);
        await host.UploadAsync(world.Leader, id, "self.pdf", OemTestHost.Pdf("self"), ct);
        var sent = await world.Leader.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        Assert.Equal("SKIPPED", sent["summary"]!["approvalStatus"]!.GetValue<string>());
        await host.RunOemJobsAsync(ct);
        Assert.Equal("RELEASED", Lifecycle(await world.Leader.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok()));
        await using (var conn = await host.OpenAsync(ct))
            Assert.Contains("SELF_POLICY_SKIP", await conn.ExecuteScalarAsync<string>(
                "SELECT detail FROM audit_logs WHERE action='OEM_TRANSFER_SEND' AND target_id=@id",
                new { id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        await host.CreateInternalUserAsync("direct_staff", "Direct#2026x", ["oem:transfer_create"], world.Org.Department, ct);
        var direct = await host.LoginInternalAsync("direct_staff", "Direct#2026x", ct);
        var refused = await direct.PostAsync("/api/v1/oem/transfers",
            new { title = "直属发送", oemCompanyId = world.CompanyId, retentionTemplateId = world.KeepTemplateId }, ct).Status(HttpStatusCode.BadRequest);
        Assert.Contains("课别", refused.Body);
    }

    [Fact(Timeout = 240_000)]
    public async Task DisabledApproverBlocksTheFlowUntilARecoveryAdminReassigns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var backupId = await host.CreateInternalUserAsync("backup_approver", "Backup#2026x", ["oem:flow_approve"], world.Org.Section, ct);
        await host.CreateInternalUserAsync("recovery_only", "Recovery#2026x", ["oem:approval_recover"], world.Org.Section, ct);
        var recovery = await host.LoginInternalAsync("recovery_only", "Recovery#2026x", ct);
        var draft = await CreateOutboundAsync(world, "审批人离职", ct);
        var id = TransferId(draft);
        var uploaded = await host.UploadAsync(world.Sender, id, "doc.pdf", OemTestHost.Pdf("doc"), ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) + 1 }, ct).Ok();
        await host.RunOemJobsAsync(ct);

        // Knowing an id/version is not enough to turn the recovery permission into a
        // general approval or transfer-administration permission. The flow must already
        // be an outbound approval block before either recovery command is accepted.
        var inProgress = await world.Admin.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        var inProgressTask = inProgress["approval"]!["nodes"]![0]!["tasks"]![0]!;
        await recovery.PostAsync($"/api/v1/oem/approvals/{inProgressTask.Id()}/reassign",
            new { newApproverUserId = backupId, reason = "不应允许改派正常审批", version = inProgressTask["version"]!.GetValue<ulong>() }, ct)
            .Status(HttpStatusCode.Conflict);
        await recovery.PostAsync($"/api/v1/oem/transfers/{id}/cancel",
            new { reason = "不应允许终止正常审批", version = Version(inProgress) }, ct)
            .Status(HttpStatusCode.Conflict);

        // Inbound transfers have no internal approval-recovery flow. Even while sealed,
        // they cannot be terminated through the recovery endpoint by guessing an id.
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers",
            new { title = "入站不可恢复终止", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var inboundId = TransferId(inbound);
        await host.UploadAsync(world.Vendor, inboundId, "inbound.pdf", OemTestHost.Pdf("inbound"), ct);
        var inboundSent = await world.Vendor.PostAsync($"/api/v1/oem/transfers/{inboundId}/send",
            new { version = Version(inbound) + 1 }, ct).Ok();
        await recovery.PostAsync($"/api/v1/oem/transfers/{inboundId}/cancel",
            new { reason = "不应允许终止入站传递", version = Version(inboundSent) }, ct)
            .Status(HttpStatusCode.Conflict);

        await world.Admin.PutAsync($"/api/v1/admin/users/{world.LeaderId}/status", new { status = "DISABLED" }, ct).Ok();
        Assert.Equal(1, await host.Service<OemApprovalService>().RevalidateActiveInstancesAsync(ct));
        var blocked = await world.Admin.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("APPROVAL_BLOCKED", blocked["summary"]!["approvalStatus"]!.GetValue<string>());
        var task = blocked["approval"]!["nodes"]![0]!["tasks"]![0]!;

        // The recovery-only role gets one explicit, metadata-only scope. It cannot
        // browse the general transfer list or inspect any file record/content.
        Assert.Empty((await recovery.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray());
        var recoveryList = (await recovery.GetAsync("/api/v1/oem/transfers?approvalStatus=APPROVAL_BLOCKED", ct).Ok())["list"]!.AsArray();
        Assert.Equal(id, Assert.Single(recoveryList)!.Id());
        var recoveryDetail = await recovery.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Null(recoveryDetail["description"]);
        Assert.Null(recoveryDetail["manifestSha256"]);
        Assert.Empty(recoveryDetail["files"]!.AsArray());
        Assert.Equal("NONE", recoveryDetail["capabilities"]!["contentPurpose"]!.GetValue<string>());
        Assert.NotNull(recoveryDetail["approval"]);
        await recovery.GetAsync($"/api/v1/oem/files/{uploaded.Id()}/validation-status", ct).Status(HttpStatusCode.NotFound);
        var options = (await recovery.GetAsync("/api/v1/oem/approver-options", ct).Ok()).AsArray();
        Assert.Contains(options, option => option!.Id() == backupId);

        // Reassignment rules: never the initiator, always an eligible approver, reason required.
        await recovery.PostAsync($"/api/v1/oem/approvals/{task.Id()}/reassign",
            new { newApproverUserId = world.SenderId, reason = "x", version = task["version"]!.GetValue<ulong>() }, ct).Status(HttpStatusCode.BadRequest);
        await recovery.PostAsync($"/api/v1/oem/approvals/{task.Id()}/reassign",
            new { newApproverUserId = backupId, reason = "", version = task["version"]!.GetValue<ulong>() }, ct).Status(HttpStatusCode.BadRequest);
        var resumed = await recovery.PostAsync($"/api/v1/oem/approvals/{task.Id()}/reassign",
            new { newApproverUserId = backupId, reason = "原主管离职", version = task["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("PENDING", resumed["summary"]!["approvalStatus"]!.GetValue<string>());
        Assert.Empty(resumed["files"]!.AsArray());
        await recovery.GetAsync($"/api/v1/oem/transfers/{id}", ct).Status(HttpStatusCode.NotFound);
        Assert.Empty((await recovery.GetAsync("/api/v1/oem/transfers?approvalStatus=APPROVAL_BLOCKED", ct).Ok())["list"]!.AsArray());
        var tasks = resumed["approval"]!["nodes"]![0]!["tasks"]!.AsArray();
        Assert.Equal(["SUPERSEDED", "PENDING"], tasks.Select(item => item!["status"]!.GetValue<string>()));

        // A recovery admin may terminate, but never approve on someone's behalf.
        var newTask = tasks[1]!;
        await world.Admin.PostAsync($"/api/v1/oem/approvals/{newTask.Id()}/approve", new { version = newTask["version"]!.GetValue<ulong>() }, ct)
            .Status(HttpStatusCode.Forbidden);
        var cancelled = await world.Admin.PostAsync($"/api/v1/oem/transfers/{id}/cancel",
            new { reason = "业务取消", version = resumed["summary"]!["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("CANCELLED", Lifecycle(cancelled));
        await world.Admin.PostAsync($"/api/v1/oem/transfers/{id}/cancel", new { reason = "再次", version = Version(cancelled) }, ct).Status(HttpStatusCode.Conflict);
    }

    [Fact(Timeout = 240_000)]
    public async Task VendorStorageQuotaCountsBothDirectionsAndDraftDeletionPurges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        await world.Admin.PutAsync("/api/v1/oem/file-policies", new { items = new[] { new { key = "oem.upload.max_storage_per_company", value = "1048576" } } }, ct).Ok();

        var outbound = await CreateOutboundAsync(world, "占用配额", ct);
        await host.UploadAsync(world.Sender, TransferId(outbound), "big.pdf", OemTestHost.Pdf(new string('a', 700_000)), ct);
        var inbound = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "超出配额", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{TransferId(inbound)}/uploads/init", new { fileName = "more.pdf", fileSize = 500_000 }, ct)
            .Status(HttpStatusCode.Conflict);

        // Deleting the draft abandons it and schedules its files for immediate purge.
        outbound = await world.Sender.GetAsync($"/api/v1/oem/transfers/{TransferId(outbound)}", ct).Ok();
        await world.Sender.DeleteAsync($"/api/v1/oem/transfers/{TransferId(outbound)}?version={Version(outbound)}", ct).Ok();
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal("ABANDONED", await conn.ExecuteScalarAsync<string>("SELECT lifecycle_status FROM oem_transfers WHERE id=@id", new { id = TransferId(outbound) }));
        Assert.Equal("DRAFT_DELETED", await conn.ExecuteScalarAsync<string>("SELECT purge_reason FROM oem_transfer_files WHERE transfer_id=@id", new { id = TransferId(outbound) }));
    }

    [Fact(Timeout = 240_000)]
    public async Task PromotionRejectsSameSizeContentMismatchAndTerminalFailureBlocksTheTransfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "提升完整性失败", ct);
        var transferId = TransferId(draft);
        var content = OemTestHost.Pdf(new string('x', 700_000));
        var uploaded = await host.UploadAsync(world.Sender, transferId, "integrity.pdf", content, ct);

        string promotionId = Guid.NewGuid().ToString("D");
        string sourcePath;
        string targetPath;
        await using (var conn = await host.OpenAsync(ct))
        {
            var file = await conn.QuerySingleAsync<(string StoredName, string StoragePath, ulong SizeBytes, string Sha256)>(
                "SELECT stored_name AS StoredName, storage_path AS StoragePath, size_bytes AS SizeBytes, sha256 AS Sha256 " +
                "FROM oem_transfer_files WHERE id=@id", new { id = uploaded.Id() });
            var targetRelative = OemStorage.AvailableRelative(file.StoredName);
            sourcePath = Path.Combine(host.StorageRoot, "oem", file.StoragePath);
            targetPath = Path.Combine(host.StorageRoot, "oem", targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var corrupt = content.ToArray();
            corrupt[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(targetPath, corrupt, ct);
            Assert.Equal(content.Length, corrupt.Length);
            Assert.NotEqual(file.Sha256, Convert.ToHexString(SHA256.HashData(corrupt)).ToLowerInvariant());

            await conn.ExecuteAsync("UPDATE oem_transfer_files SET scan_status=@status, payload_status='PROMOTING' WHERE id=@id",
                new { id = uploaded.Id(), status = ValidationStatuses.Valid });
            await conn.ExecuteAsync(@"INSERT INTO oem_file_promotions
                (id,file_id,file_sha256,size_bytes,source_path,target_path,status,attempt_count,concurrency_version,created_at)
                VALUES (@promotionId,@fileId,@sha256,@sizeBytes,@source,@target,'PREPARED',9,0,UTC_TIMESTAMP(3))",
                new { promotionId, fileId = uploaded.Id(), sha256 = file.Sha256, sizeBytes = file.SizeBytes, source = file.StoragePath, target = targetRelative });
        }

        await world.Sender.PostAsync($"/api/v1/oem/transfers/{transferId}/send", new { version = Version(draft) + 1 }, ct).Ok();
        Assert.False(await host.Service<OemPromotionService>().PromoteAsync(promotionId, ct));

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(targetPath));
        await using (var db = await host.OpenAsync(ct))
        {
            Assert.Equal("FAILED", await db.ExecuteScalarAsync<string>("SELECT status FROM oem_file_promotions WHERE id=@promotionId", new { promotionId }));
            Assert.Equal(ValidationStatuses.Error,
                await db.ExecuteScalarAsync<string>("SELECT scan_status FROM oem_transfer_files WHERE id=@id", new { id = uploaded.Id() }));
            Assert.Equal("BLOCKED", await db.ExecuteScalarAsync<string>("SELECT lifecycle_status FROM oem_transfers WHERE id=@transferId", new { transferId }));
            Assert.NotNull(await db.ExecuteScalarAsync<DateTime?>("SELECT purge_due_at FROM oem_transfer_files WHERE id=@id", new { id = uploaded.Id() }));
            await db.ExecuteAsync("UPDATE oem_transfer_files SET purge_due_at=UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE id=@id", new { id = uploaded.Id() });
        }

        Assert.Equal(1, await host.Service<OemPurgeService>().RunOnceAsync(ct));
        Assert.False(File.Exists(sourcePath));
        Assert.False(File.Exists(targetPath));
        await using (var verify = await host.OpenAsync(ct))
            Assert.Equal("PURGED", await verify.ExecuteScalarAsync<string>("SELECT payload_status FROM oem_transfer_files WHERE id=@id", new { id = uploaded.Id() }));

        // PURGED content no longer consumes the vendor's logical storage quota.
        await world.Admin.PutAsync("/api/v1/oem/file-policies", new
        {
            items = new[] { new { key = "oem.upload.max_storage_per_company", value = "1048576" } },
        }, ct).Ok();
        var replacement = await CreateOutboundAsync(world, "清理后重新上传", ct);
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(replacement)}/uploads/init",
            new { fileName = "replacement.pdf", fileSize = 700_000 }, ct).Ok();
    }

    [Fact(Timeout = 240_000)]
    public async Task UserAssignedToAnOemApprovalTemplateCannotBeHardDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var approverId = await host.CreateInternalUserAsync("template_history", "Template#2026x", ["oem:flow_approve"], world.Org.Section, ct);
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync(@"INSERT INTO oem_flow_template_node_users(node_id,role,user_id)
                SELECT id,'APPROVER',@approverId FROM oem_flow_template_nodes ORDER BY id LIMIT 1", new { approverId });

        await world.Admin.DeleteAsync($"/api/v1/admin/users/{approverId}", ct).Status(HttpStatusCode.BadRequest);
        await using var verify = await host.OpenAsync(ct);
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE id=@approverId", new { approverId }));
    }

    [Fact(Timeout = 240_000)]
    public async Task TerminalPromotionFailureSchedulesDraftCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "草稿提升失败", ct);
        var transferId = TransferId(draft);
        var content = OemTestHost.Pdf("draft-integrity");
        var uploaded = await host.UploadAsync(world.Sender, transferId, "draft.pdf", content, ct);
        var promotionId = Guid.NewGuid().ToString("D");
        await using (var conn = await host.OpenAsync(ct))
        {
            var file = await conn.QuerySingleAsync<(string StoredName, string StoragePath, ulong SizeBytes, string Sha256)>(
                "SELECT stored_name AS StoredName, storage_path AS StoragePath, size_bytes AS SizeBytes, sha256 AS Sha256 " +
                "FROM oem_transfer_files WHERE id=@id", new { id = uploaded.Id() });
            var targetRelative = OemStorage.AvailableRelative(file.StoredName);
            var targetPath = Path.Combine(host.StorageRoot, "oem", targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var corrupt = content.ToArray();
            corrupt[^1] ^= 0x3C;
            await File.WriteAllBytesAsync(targetPath, corrupt, ct);
            await conn.ExecuteAsync("UPDATE oem_transfer_files SET scan_status=@status, payload_status='PROMOTING' WHERE id=@id",
                new { id = uploaded.Id(), status = ValidationStatuses.Valid });
            await conn.ExecuteAsync(@"INSERT INTO oem_file_promotions
                (id,file_id,file_sha256,size_bytes,source_path,target_path,status,attempt_count,concurrency_version,created_at)
                VALUES (@promotionId,@fileId,@sha256,@sizeBytes,@source,@target,'PREPARED',9,0,UTC_TIMESTAMP(3))",
                new { promotionId, fileId = uploaded.Id(), sha256 = file.Sha256, sizeBytes = file.SizeBytes, source = file.StoragePath, target = targetRelative });
        }

        Assert.False(await host.Service<OemPromotionService>().PromoteAsync(promotionId, ct));
        await using var verify = await host.OpenAsync(ct);
        var state = await verify.QuerySingleAsync<(string Lifecycle, string Promotion, DateTime? PurgeDue, string PurgeReason)>(@"
            SELECT t.lifecycle_status AS Lifecycle,p.status AS Promotion,f.purge_due_at AS PurgeDue,f.purge_reason AS PurgeReason
            FROM oem_transfers t JOIN oem_transfer_files f ON f.transfer_id=t.id
            JOIN oem_file_promotions p ON p.file_id=f.id WHERE t.id=@transferId", new { transferId });
        Assert.Equal(("DRAFT", "FAILED", PurgeReasons.Blocked), (state.Lifecycle, state.Promotion, state.PurgeReason));
        Assert.NotNull(state.PurgeDue);
    }
}
