using Dapper;
using System.Net;
using System.Text.Json.Nodes;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Tests.Oem;

public sealed class OemScannerPolicyTests
{
    [Fact(Timeout = 240_000)]
    public async Task ClamAvEnablesFreshnessPolicyAndRejectsOversizedUploadBeforeReservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct, args =>
        {
            args.Add("--App:OemScanner:Engine=ClamAV");
            args.Add("--App:OemScanner:ClamAv:MaxStreamBytes=1048576");
        });
        var world = await OemTransferFlowTests.OutboundWorldAsync(host, ct);
        var settings = (await world.Admin.GetAsync("/api/v1/oem/file-policies", ct).Ok()).AsArray();
        foreach (var key in new[] { OemSettingCatalog.MaxSignatureAgeHours, OemSettingCatalog.BlockOnStaleSignatures })
        {
            var item = settings.Single(item => item!["key"]!.GetValue<string>() == key)!;
            Assert.False(item["readOnly"]!.GetValue<bool>());
            Assert.Null(item["unsupportedReason"]);
        }
        var saved = (await world.Admin.PutAsync("/api/v1/oem/file-policies", new
        {
            items = new[] {
                new { key = OemSettingCatalog.MaxSignatureAgeHours, value = "24" },
                new { key = OemSettingCatalog.BlockOnStaleSignatures, value = "true" },
            },
        }, ct).Ok()).AsArray();
        Assert.Equal("true", saved.Single(item => item!["key"]!.GetValue<string>() == OemSettingCatalog.BlockOnStaleSignatures)!["value"]!.GetValue<string>());
        Assert.Equal("24", saved.Single(item => item!["key"]!.GetValue<string>() == OemSettingCatalog.MaxSignatureAgeHours)!["value"]!.GetValue<string>());
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers", new { title = "扫描上限", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var id = OemTransferFlowTests.TransferId(draft);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/uploads/init", new { fileName = "drawing.pdf", fileSize = 1048577 }, ct)
            .Status(HttpStatusCode.BadRequest);
        await using var conn = await host.OpenAsync(ct);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_upload_sessions WHERE transfer_id=@id", new { id }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_transfer_files WHERE transfer_id=@id", new { id }));
    }

    [Fact(Timeout = 240_000)]
    public async Task LegacySignatureFreshnessGateKeepsFilesQuarantinedUntilExplicitlyDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var world = await OemTransferFlowTests.OutboundWorldAsync(host, ct);
        await using var conn = await host.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE system_configs SET cfg_value='true' WHERE cfg_key=@key",
            new { key = OemSettingCatalog.BlockOnStaleSignatures });
        var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers",
            new { title = "病毒库策略回归", retentionTemplateId = world.KeepTemplateId }, ct).Ok();
        var id = OemTransferFlowTests.TransferId(draft);
        var file = await host.UploadAsync(world.Vendor, id, "drawing.pdf", OemTestHost.Pdf("drawing"), ct);
        await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/send",
            new { version = OemTransferFlowTests.Version(draft) + 1 }, ct).Ok();

        await host.RunOemJobsAsync(ct);
        var pending = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("SEALED", pending["summary"]!["lifecycleStatus"]!.GetValue<string>());
        Assert.Equal("QUARANTINED", pending["files"]![0]!["payloadStatus"]!.GetValue<string>());
        Assert.Equal("PENDING", pending["files"]![0]!["scanStatus"]!.GetValue<string>());
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT attempt_count FROM oem_file_scan_jobs WHERE file_id=@id", new { id = file.Id() }));

        await world.Admin.PutAsync("/api/v1/oem/file-policies", new
        {
            items = new[] { new { key = OemSettingCatalog.BlockOnStaleSignatures, value = "false" } },
        }, ct).Ok();
        await conn.ExecuteAsync("UPDATE oem_file_scan_jobs SET next_attempt_at=UTC_TIMESTAMP(3) WHERE file_id=@id", new { id = file.Id() });
        await host.RunOemJobsAsync(ct);
        var released = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
        Assert.Equal("RELEASED", released["summary"]!["lifecycleStatus"]!.GetValue<string>());
    }
}
