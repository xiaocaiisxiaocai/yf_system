using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.Oem.Transfers;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>Release-time recipient gate, task activation timestamps and the paged approval inbox.</summary>
public sealed class OemApprovalInboxTests
{
    private static async Task<JsonNode> SendForApprovalAsync(OutboundWorld world, string title, CancellationToken ct)
    {
        var draft = await CreateOutboundAsync(world, title, ct);
        await world.Host.UploadAsync(world.Sender, TransferId(draft), title + ".pdf", OemTestHost.Pdf(title), ct);
        return await world.Sender.PostAsync($"/api/v1/oem/transfers/{TransferId(draft)}/send", new { version = Version(draft) + 1 }, ct).Ok();
    }

    private static async Task<JsonArray> PendingAsync(ApiClient client, string query, CancellationToken ct) =>
        (await client.GetAsync("/api/v1/oem/approvals/pending" + query, ct).Ok())["list"]!.AsArray();

    [Theory(Timeout = 240_000)]
    [InlineData("account")]
    [InlineData("company")]
    public async Task ApprovalDoesNotReleaseToAVendorThatCanNoLongerReceive(string disabled)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var blockedId = TransferId(await SendForApprovalAsync(world, "收件方失效", ct));
        var releasedId = TransferId(await SendForApprovalAsync(world, "收件方正常", ct));
        await host.RunOemJobsAsync(ct);
        var tasks = await PendingAsync(world.Leader, "", ct);
        var releasedTask = tasks.Single(task => task!.Id("transferId") == releasedId)!;
        var blockedTask = tasks.Single(task => task!.Id("transferId") == blockedId)!;

        // While the vendor still has an enabled account, approval releases as before.
        var released = await world.Leader.PostAsync($"/api/v1/oem/approvals/{releasedTask.Id("taskId")}/approve",
            new { version = releasedTask["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("RELEASED", released["summary"]!["lifecycleStatus"]!.GetValue<string>());

        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync(disabled == "account"
                ? "UPDATE oem_accounts SET status='DISABLED' WHERE oem_company_id=@CompanyId"
                : "UPDATE oem_companies SET status='DISABLED' WHERE id=@CompanyId", new { world.CompanyId });

        var decided = await world.Leader.PostAsync($"/api/v1/oem/approvals/{blockedTask.Id("taskId")}/approve",
            new { version = blockedTask["version"]!.GetValue<ulong>() }, ct).Ok();
        Assert.Equal("BLOCKED", decided["summary"]!["lifecycleStatus"]!.GetValue<string>());

        await using var verify = await host.OpenAsync(ct);
        var target = blockedId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(OemTransferProgression.RecipientUnavailableReason,
            await verify.ExecuteScalarAsync<string>("SELECT closed_reason FROM oem_transfers WHERE id=@blockedId", new { blockedId }));
        Assert.Null(await verify.ExecuteScalarAsync<DateTime?>("SELECT released_at FROM oem_transfers WHERE id=@blockedId", new { blockedId }));
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='OEM_TRANSFER_RELEASE' AND target_id=@target", new { target }));
        Assert.Contains(OemTransferProgression.RecipientUnavailableReason, await verify.ExecuteScalarAsync<string>(
            "SELECT detail FROM audit_logs WHERE action='OEM_TRANSFER_BLOCK' AND target_id=@target", new { target }));
        // The sender hears about it like any other blocked transfer, with the reason in the mail.
        var mail = await verify.QuerySingleAsync<(ulong RecipientAccountId, string Body)>(
            "SELECT recipient_account_id AS RecipientAccountId, body AS Body FROM email_outbox WHERE event_type=@EventType AND oem_transfer_id=@blockedId",
            new { EventType = OemMailEvents.TransferClosed, blockedId });
        Assert.Equal(world.SenderId, mail.RecipientAccountId);
        Assert.Contains(OemTransferProgression.RecipientUnavailableReason, mail.Body);
        Assert.NotNull(await verify.ExecuteScalarAsync<DateTime?>(
            "SELECT MIN(purge_due_at) FROM oem_transfer_files WHERE transfer_id=@blockedId", new { blockedId }));
    }

    [Fact(Timeout = 240_000)]
    public async Task TasksRecordWhenTheyBecomePendingOnActivationAdvanceAndReassignment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var reviewerId = await host.CreateInternalUserAsync("zp_reviewer", "Review#2026x", ["oem:flow_approve", "oem:transfer_view"], world.Org.Section, ct);
        var backupId = await host.CreateInternalUserAsync("zp_backup", "Backup#2026x", ["oem:flow_approve", "oem:transfer_view"], world.Org.Section, ct);
        await world.Admin.PostAsync("/api/v1/oem/flow-templates", new
        {
            name = "两级审批", isDefault = false, departmentIds = new[] { world.Org.Department },
            nodes = new object[]
            {
                new { name = "课别主管", approverSource = "SECTION_LEADER", approvalMode = "SINGLE", selfPolicy = "DESIGNATED", enabled = true, approverUserIds = Array.Empty<ulong>(), fallbackUserIds = new[] { backupId } },
                new { name = "质量确认", approverSource = "SPECIFIED_USERS", approvalMode = "ANY", selfPolicy = "BLOCK", enabled = true, approverUserIds = new[] { reviewerId }, fallbackUserIds = Array.Empty<ulong>() },
            },
        }, ct).Ok();
        var reviewer = await host.LoginInternalAsync("zp_reviewer", "Review#2026x", ct);
        var transferId = TransferId(await SendForApprovalAsync(world, "两级", ct));

        async Task<DateTime?> ActivatedAtAsync(ulong approverId)
        {
            await using var conn = await host.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<DateTime?>(
                "SELECT t.activated_at FROM oem_flow_tasks t JOIN oem_flow_instances i ON i.id=t.instance_id " +
                "WHERE i.transfer_id=@transferId AND t.approver_user_id=@approverId AND t.status IN ('WAITING','PENDING')", new { transferId, approverId });
        }

        // Tasks of later nodes are planned at send time but not active yet.
        Assert.Null(await ActivatedAtAsync(world.LeaderId));
        Assert.Null(await ActivatedAtAsync(reviewerId));
        await host.RunOemJobsAsync(ct);
        var first = Assert.Single(await PendingAsync(world.Leader, "", ct))!;
        var leaderActivatedAt = await ActivatedAtAsync(world.LeaderId);
        Assert.NotNull(leaderActivatedAt);
        Assert.Equal(leaderActivatedAt, first["activatedAt"]!.GetValue<DateTime>());
        Assert.Null(await ActivatedAtAsync(reviewerId));

        // Advancing to the next node stamps that node's tasks.
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{first.Id("taskId")}/approve", new { version = first["version"]!.GetValue<ulong>() }, ct).Ok();
        var second = Assert.Single(await PendingAsync(reviewer, "", ct))!;
        var reviewerActivatedAt = await ActivatedAtAsync(reviewerId);
        Assert.NotNull(reviewerActivatedAt);
        Assert.True(reviewerActivatedAt > leaderActivatedAt);
        Assert.Equal(reviewerActivatedAt, second["activatedAt"]!.GetValue<DateTime>());

        // A reassignment starts a new wait for the new approver.
        await world.Admin.PostAsync($"/api/v1/oem/approvals/{second.Id("taskId")}/reassign",
            new { newApproverUserId = backupId, reason = "出差", version = second["version"]!.GetValue<ulong>() }, ct).Ok();
        var backupActivatedAt = await ActivatedAtAsync(backupId);
        Assert.NotNull(backupActivatedAt);
        Assert.True(backupActivatedAt > reviewerActivatedAt);
        Assert.Empty(await PendingAsync(reviewer, "", ct));
    }

    [Fact(Timeout = 240_000)]
    public async Task PendingApprovalsArePagedInActivationOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        for (var i = 0; i < 3; i++) await SendForApprovalAsync(world, $"分页{i}", ct);
        await host.RunOemJobsAsync(ct);

        var all = await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok();
        Assert.Equal(3UL, all["total"]!.GetValue<ulong>());
        Assert.Equal(1UL, all["page"]!.GetValue<ulong>());
        Assert.Equal(20U, all["pageSize"]!.GetValue<uint>());
        // Make the activation order differ from the task-id order, with a tie broken by id.
        var ids = all["list"]!.AsArray().Select(task => task!.Id("taskId")).Order().ToArray();
        await using (var conn = await host.OpenAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE oem_flow_tasks SET activated_at='2026-01-02 00:00:00.000' WHERE id=@id", new { id = ids[0] });
            await conn.ExecuteAsync("UPDATE oem_flow_tasks SET activated_at='2026-01-01 00:00:00.000' WHERE id IN @ids", new { ids = ids[1..] });
        }

        var first = await world.Leader.GetAsync("/api/v1/oem/approvals/pending?page=1&pageSize=2", ct).Ok();
        Assert.Equal(3UL, first["total"]!.GetValue<ulong>());
        Assert.Equal(2U, first["pageSize"]!.GetValue<uint>());
        var second = await world.Leader.GetAsync("/api/v1/oem/approvals/pending?page=2&pageSize=2", ct).Ok();
        Assert.Equal(2UL, second["page"]!.GetValue<ulong>());
        var ordered = first["list"]!.AsArray().Concat(second["list"]!.AsArray()).Select(task => task!.Id("taskId")).ToArray();
        Assert.Equal([ids[1], ids[2], ids[0]], ordered);
        Assert.Empty(await PendingAsync(world.Leader, "?page=3&pageSize=2", ct));
        // The inbox is only for approvers.
        await world.Viewer.GetAsync("/api/v1/oem/approvals/pending", ct).Status(System.Net.HttpStatusCode.Forbidden);
    }
}
