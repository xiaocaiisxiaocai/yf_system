using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Tests.Oem;

/// <summary>Approval templates, retention templates and OEM parameter pages over HTTP.</summary>
public sealed class OemPolicyTests
{
    [Fact]
    public void ValidationSettingsReplaceScannerAndVirusDatabaseSettings()
    {
        var settings = new OemSettings(new Dictionary<string, string?>
        {
            [OemSettingCatalog.ValidationMaxRetries] = "7",
        });
        Assert.Equal(7, settings.ValidationMaxRetries);
        Assert.All(OemSettingCatalog.All.Where(item => item.Key.Contains("archive_", StringComparison.Ordinal)),
            item => Assert.True(item.Key.StartsWith("oem.validation.", StringComparison.Ordinal), item.Key));
        Assert.DoesNotContain(OemSettingCatalog.All, item => item.Key.StartsWith("oem.scan.", StringComparison.Ordinal));
        Assert.False(OemSettingCatalog.TryGet("oem.scan.max_signature_age_hours", out _));
        Assert.False(OemSettingCatalog.TryGet("oem.scan.block_on_stale_signatures", out _));
    }

    [Fact(Timeout = 180_000)]
    public async Task FlowTemplatesAreValidatedScopedPreviewedAndVersioned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var org = await host.CreateOrgPathAsync("电装", ct);
        var otherOrg = await host.CreateOrgPathAsync("机加", ct);
        var leader = await host.CreateInternalUserAsync("dz_leader", "Leader#2026x", ["oem:flow_approve"], org.Section, ct);
        var sender = await host.CreateInternalUserAsync("dz_sender", "Sender#2026x", ["oem:transfer_create"], org.Section, ct);
        var reviewer = await host.CreateInternalUserAsync("qa_reviewer", "Review#2026x", ["oem:flow_approve"], otherOrg.Section, ct);
        var noPermission = await host.CreateInternalUserAsync("no_perm", "NoPerm#2026x", [], otherOrg.Section, ct);
        var directStaff = await host.CreateInternalUserAsync("dept_direct", "Direct#2026x", ["oem:transfer_create"], org.Department, ct);

        // The seeded default template exists and routes to the section leader.
        var templates = (await admin.GetAsync("/api/v1/oem/flow-templates", ct).Ok()).AsArray();
        var defaultTemplate = Assert.Single(templates)!;
        Assert.True(defaultTemplate["isDefault"]!.GetValue<bool>());

        // Preview: no leader configured yet -> explained failure.
        var preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={sender}", ct).Ok();
        Assert.False(preview["ok"]!.GetValue<bool>());
        Assert.Contains("未配置主管", preview["reason"]!.GetValue<string>());
        await admin.PutAsync($"/api/v1/admin/departments/{org.Section}/leader", new { leaderUserId = leader }, ct).Ok();
        preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={sender}", ct).Ok();
        Assert.True(preview["ok"]!.GetValue<bool>());
        Assert.Equal("dz_leader", preview["nodes"]![0]!["approvers"]![0]!["employeeNo"]!.GetValue<string>());

        // Leader sending with the default template: SKIP policy -> no approval needed.
        preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={leader}", ct).Ok();
        Assert.True(preview["ok"]!.GetValue<bool>());
        Assert.False(preview["requiresApproval"]!.GetValue<bool>());

        // Staff directly under a department cannot send.
        preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={directStaff}", ct).Ok();
        Assert.Contains("课别", preview["reason"]!.GetValue<string>());

        // Node validation: an approver without oem:flow_approve is rejected.
        await admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "电装专用", isDefault = false,
            nodes = new[] { new { name = "质量会签", approverSource = "SPECIFIED_USERS", approvalMode = "ALL", selfPolicy = "BLOCK", enabled = true, approverUserIds = new[] { reviewer, noPermission }, fallbackUserIds = Array.Empty<ulong>() } },
        }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "电装专用", isDefault = false,
            nodes = new[] { new { name = "主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "DESIGNATED", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = Array.Empty<ulong>() } },
        }, ct).Status(HttpStatusCode.BadRequest);

        var created = await admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "电装专用", isDefault = false, departmentIds = new[] { org.Department },
            nodes = new object[]
            {
                new { name = "课别主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "DESIGNATED", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = new[] { reviewer } },
                new { name = "质量确认", approverSource = "SPECIFIED_USERS", approvalMode = "ANY", selfPolicy = "BLOCK", enabled = true, approverUserIds = new[] { reviewer }, fallbackUserIds = Array.Empty<ulong>() },
            },
        }, ct).Ok();
        var templateId = created.Id();
        Assert.Equal(2, created["nodes"]!.AsArray().Count);

        // The department-scoped template now applies to the sender, and it is a configuration
        // error for the same reviewer to appear in two nodes once the leader uses the fallback.
        preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={sender}", ct).Ok();
        Assert.Equal(templateId, preview["template"]!.Id());
        Assert.Equal(org.Department, preview["matchedScope"]!.Id());
        preview = await admin.GetAsync($"/api/v1/oem/flow-templates/preview?userId={leader}", ct).Ok();
        Assert.Contains("同一审批人", preview["reason"]!.GetValue<string>());

        // Another template cannot claim the same organisation.
        await admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "冲突模板", isDefault = false, departmentIds = new[] { org.Department },
            nodes = new[] { new { name = "主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "SKIP", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = Array.Empty<ulong>() } },
        }, ct).Status(HttpStatusCode.Conflict);
        await admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "机加专用", isDefault = false, departmentIds = new[] { otherOrg.Department },
            nodes = new[] { new { name = "主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "SKIP", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = Array.Empty<ulong>() } },
        }, ct).Ok();

        // Optimistic concurrency and default-template rules.
        var version = created["version"]!.GetValue<ulong>();
        await admin.PutAsync($"/api/v1/oem/flow-templates/{templateId}/definition", new
        {
            name = "电装专用改", status = "ACTIVE", isDefault = false, version,
            departmentIds = new[] { otherOrg.Department },
            nodes = new[] { new { name = "不应落库", approverSource = "SPECIFIED_USERS", approvalMode = "ANY", selfPolicy = "BLOCK", enabled = true, approverUserIds = new[] { reviewer }, fallbackUserIds = Array.Empty<ulong>() } },
        }, ct).Status(HttpStatusCode.Conflict);
        var unchanged = await admin.GetAsync($"/api/v1/oem/flow-templates/{templateId}", ct).Ok();
        Assert.Equal(version, unchanged["version"]!.GetValue<ulong>());
        Assert.Equal("课别主管", unchanged["nodes"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(org.Department, unchanged["scopes"]![0]!.Id());

        var replaced = await admin.PutAsync($"/api/v1/oem/flow-templates/{templateId}/definition", new
        {
            name = "电装专用", status = "ACTIVE", isDefault = false, version,
            departmentIds = new[] { org.Department },
            nodes = new object[]
            {
                new { name = "课别主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "DESIGNATED", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = new[] { reviewer } },
                new { name = "质量确认", approverSource = "SPECIFIED_USERS", approvalMode = "ANY", selfPolicy = "BLOCK", enabled = true, approverUserIds = new[] { reviewer }, fallbackUserIds = Array.Empty<ulong>() },
            },
        }, ct).Ok();
        version = replaced["version"]!.GetValue<ulong>();
        await admin.PutAsync($"/api/v1/oem/flow-templates/{templateId}/scopes", new { departmentIds = new[] { org.Section }, version = version + 5 }, ct)
            .Status(HttpStatusCode.Conflict);
        var rescoped = await admin.PutAsync($"/api/v1/oem/flow-templates/{templateId}/scopes", new { departmentIds = new[] { org.Section }, version }, ct).Ok();
        Assert.Equal(version + 1, rescoped["version"]!.GetValue<ulong>());
        await admin.PutAsync($"/api/v1/oem/flow-templates/{defaultTemplate.Id()}",
            new { name = defaultTemplate["name"]!.GetValue<string>(), status = "DISABLED", isDefault = true, version = defaultTemplate["version"]!.GetValue<ulong>() }, ct)
            .Status(HttpStatusCode.BadRequest);
        var promoted = await admin.PutAsync($"/api/v1/oem/flow-templates/{templateId}",
            new { name = "电装专用", status = "ACTIVE", isDefault = true, version = version + 1 }, ct).Ok();
        Assert.True(promoted["isDefault"]!.GetValue<bool>());
        var list = (await admin.GetAsync("/api/v1/oem/flow-templates", ct).Ok()).AsArray();
        Assert.Single(list, item => item!["isDefault"]!.GetValue<bool>());

        // Only holders of oem:flow_template_manage may see or change templates.
        var senderClient = await host.LoginInternalAsync("dz_sender", "Sender#2026x", ct);
        await senderClient.GetAsync("/api/v1/oem/flow-templates", ct).Status(HttpStatusCode.Forbidden);
        var approverOptions = (await admin.GetAsync("/api/v1/oem/approver-options?keyword=reviewer", ct).Ok()).AsArray();
        Assert.Single(approverOptions);
    }

    [Fact(Timeout = 180_000)]
    public async Task RetentionTemplatesAndParameterPagesEnforceTheirOwnPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);

        await admin.PostAsync("/api/v1/oem/retention-templates", new { name = "下载后删除", mode = "AFTER_FIRST_RECEIPT", releaseTtlMinutes = 60, receiptGraceMinutes = 60 }, ct)
            .Status(HttpStatusCode.BadRequest);
        var created = await admin.PostAsync("/api/v1/oem/retention-templates",
            new { name = "下载后 1 天或 7 天内删除", mode = "FIRST_RECEIPT_OR_DEADLINE", releaseTtlMinutes = 7 * 24 * 60, receiptGraceMinutes = 24 * 60 }, ct).Ok();
        Assert.Contains("最迟发布 7 天", created["summary"]!.GetValue<string>());

        // Creating a policy makes it the only active policy and keeps older rows as disabled history.
        var keep = (await admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray().Single(item => item!["mode"]!.GetValue<string>() == "KEEP")!;
        Assert.Equal("DISABLED", keep["status"]!.GetValue<string>());
        var options = (await admin.GetAsync("/api/v1/oem/retention-template-options", ct).Ok()).AsArray();
        Assert.Equal([created.Id()], options.Select(item => item!.Id()));

        // Enabling one policy atomically disables the previous policy, and the only active policy cannot be disabled.
        var activatedKeep = await admin.PutAsync($"/api/v1/oem/retention-templates/{keep.Id()}", new
        {
            name = "不自动删除", mode = "KEEP", status = "ACTIVE", version = keep["version"]!.GetValue<ulong>(),
        }, ct).Ok();
        var createdAfterSwitch = (await admin.GetAsync("/api/v1/oem/retention-templates", ct).Ok()).AsArray()
            .Single(item => item!.Id() == created.Id())!;
        Assert.Equal("DISABLED", createdAfterSwitch["status"]!.GetValue<string>());
        await admin.PutAsync($"/api/v1/oem/retention-templates/{keep.Id()}", new
        {
            name = "不自动删除", mode = "KEEP", status = "DISABLED", version = activatedKeep["version"]!.GetValue<ulong>(),
        }, ct).Status(HttpStatusCode.BadRequest);

        // Legacy duplicate-active data is read deterministically by lowest id, then the next management write repairs it.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_retention_templates SET status='ACTIVE' WHERE id=@id", new { id = created.Id() });
        options = (await admin.GetAsync("/api/v1/oem/retention-template-options", ct).Ok()).AsArray();
        Assert.Equal([keep.Id()], options.Select(item => item!.Id()));
        var repaired = await admin.PutAsync($"/api/v1/oem/retention-templates/{created.Id()}", new
        {
            name = createdAfterSwitch["name"]!.GetValue<string>(), mode = "FIRST_RECEIPT_OR_DEADLINE",
            releaseTtlMinutes = 7 * 24 * 60, receiptGraceMinutes = 24 * 60,
            status = "ACTIVE", version = createdAfterSwitch["version"]!.GetValue<ulong>(),
        }, ct).Ok();
        Assert.Equal("ACTIVE", repaired["status"]!.GetValue<string>());
        options = (await admin.GetAsync("/api/v1/oem/retention-template-options", ct).Ok()).AsArray();
        Assert.Equal([created.Id()], options.Select(item => item!.Id()));

        // OEM accounts see the same single effective option.
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "厂商C" }, ct).Ok()).Id();
        await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts",
            new { employeeNo = "vendor_c", realName = "王五", email = "wang@vendor.invalid", password = "Vendor#2026" }, ct).Ok();
        var vendor = await host.LoginOemAsync("vendor_c", "Vendor#2026", ct);
        await vendor.PutAsync("/api/v1/oem/auth/password", new { oldPassword = "Vendor#2026", newPassword = "Vendor#2027x" }, ct).Ok();
        vendor = await host.LoginOemAsync("vendor_c", "Vendor#2027x", ct);
        options = (await vendor.GetAsync("/api/v1/oem/retention-template-options", ct).Ok()).AsArray();
        Assert.Equal([created.Id()], options.Select(item => item!.Id()));
        await vendor.GetAsync("/api/v1/oem/retention-templates", ct).Status(HttpStatusCode.Forbidden);

        // File-policy and notify pages are separate permissions and separate key groups.
        await host.CreateInternalUserAsync("file_admin", "FileAdm#2026x", ["oem:file_policy_manage"], null, ct);
        var fileAdmin = await host.LoginInternalAsync("file_admin", "FileAdm#2026x", ct);
        var files = (await fileAdmin.GetAsync("/api/v1/oem/file-policies", ct).Ok()).AsArray();
        Assert.Contains(files, item => item!["key"]!.GetValue<string>() == "oem.upload.max_file_size");
        Assert.DoesNotContain(files, item => item!["key"]!.GetValue<string>().StartsWith("oem.notify", StringComparison.Ordinal));
        Assert.Contains(files, item => item!["key"]!.GetValue<string>() == OemSettingCatalog.ValidationMaxRetries);
        Assert.DoesNotContain(files, item => item!["key"]!.GetValue<string>().StartsWith("oem.scan.", StringComparison.Ordinal));
        await fileAdmin.GetAsync("/api/v1/oem/notify-policies", ct).Status(HttpStatusCode.Forbidden);
        var updated = (await fileAdmin.PutAsync("/api/v1/oem/file-policies",
            new { items = new[] { new { key = "oem.upload.allowed_exts.oem_to_internal", value = "STEP, pdf" } } }, ct).Ok()).AsArray();
        Assert.Equal("pdf,step", updated.Single(item => item!["key"]!.GetValue<string>() == "oem.upload.allowed_exts.oem_to_internal")!["value"]!.GetValue<string>());
        await fileAdmin.PutAsync("/api/v1/oem/file-policies", new { items = new[] { new { key = "oem.notify.enabled", value = "false" } } }, ct)
            .Status(HttpStatusCode.BadRequest);
        await fileAdmin.PutAsync("/api/v1/oem/file-policies", new { items = new[] { new { key = "oem.storage.reconcile_required", value = "x" } } }, ct)
            .Status(HttpStatusCode.BadRequest);
        await fileAdmin.PutAsync("/api/v1/oem/file-policies", new { items = new[] { new { key = OemSettingCatalog.ValidationMaxRetries, value = "99" } } }, ct)
            .Status(HttpStatusCode.BadRequest);
        var validation = (await fileAdmin.PutAsync("/api/v1/oem/file-policies",
            new { items = new[] { new { key = OemSettingCatalog.ValidationMaxRetries, value = "7" } } }, ct).Ok()).AsArray();
        Assert.Equal("7", validation.Single(item => item!["key"]!.GetValue<string>() == OemSettingCatalog.ValidationMaxRetries)!["value"]!.GetValue<string>());
        var notify = (await admin.PutAsync("/api/v1/oem/notify-policies", new { items = new[] { new { key = "oem.notify.event.receipt", value = "true" } } }, ct).Ok()).AsArray();
        Assert.Equal("true", notify.Single(item => item!["key"]!.GetValue<string>() == "oem.notify.event.receipt")!["value"]!.GetValue<string>());
    }
}
