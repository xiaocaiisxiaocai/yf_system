using Dapper;
using static Yf.Api.Tests.Oem.OemTransferFlowTests;

namespace Yf.Api.Tests.Oem;

/// <summary>What an OEM account may learn from the transfer summary about the company's internal side.</summary>
public sealed class OemVendorViewTests
{
    [Fact(Timeout = 240_000)]
    public async Task VendorSummariesHideApprovalDetailsAndTheInternalSendersEmployeeNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OutboundWorldAsync(host, ct);
        var draft = await CreateOutboundAsync(world, "厂商视角", ct);
        var id = TransferId(draft);
        await host.UploadAsync(world.Sender, id, "view.pdf", OemTestHost.Pdf("view"), ct);
        draft = await world.Sender.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        await world.Sender.PostAsync($"/api/v1/oem/transfers/{id}/send", new { version = Version(draft) }, ct).Ok();
        await host.RunOemJobsAsync(ct);
        var task = Assert.Single((await world.Leader.GetAsync("/api/v1/oem/approvals/pending", ct).Ok())["list"]!.AsArray())!;
        await world.Leader.PostAsync($"/api/v1/oem/approvals/{task.Id("taskId")}/approve", new { version = task["version"]!.GetValue<ulong>() }, ct).Ok();

        async Task<(string? Approval, string EmployeeNo, string RealName)> ViewAsync(ApiClient client)
        {
            var summary = (await client.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok())["summary"]!;
            var listed = (await client.GetAsync("/api/v1/oem/transfers", ct).Ok())["list"]!.AsArray()
                .Single(row => row!.Id() == id)!["sender"]!;
            // The list and the detail agree on the sender.
            Assert.Equal(summary["sender"]!["employeeNo"]!.GetValue<string>(), listed["employeeNo"]!.GetValue<string>());
            return (summary["approvalStatus"]?.GetValue<string>(),
                summary["sender"]!["employeeNo"]!.GetValue<string>(), summary["sender"]!["realName"]!.GetValue<string>());
        }

        // Staff see the full picture.
        var staff = await ViewAsync(world.Viewer);
        Assert.Equal("APPROVED", staff.Approval);
        Assert.Equal("zp_sender", staff.EmployeeNo);

        // The vendor sees only the sender's name.
        var vendor = await ViewAsync(world.Vendor);
        Assert.Equal("APPROVED", vendor.Approval);
        Assert.Equal(string.Empty, vendor.EmployeeNo);
        Assert.Equal(staff.RealName, vendor.RealName);
        Assert.False(string.IsNullOrWhiteSpace(vendor.RealName));

        // A transfer whose approval was skipped reads as approved to the vendor, but staff still see SKIPPED.
        await using (var conn = await host.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_flow_instances SET status='SKIPPED' WHERE transfer_id=@id", new { id });
        Assert.Equal("SKIPPED", (await ViewAsync(world.Viewer)).Approval);
        Assert.Equal("APPROVED", (await ViewAsync(world.Vendor)).Approval);
    }
}
