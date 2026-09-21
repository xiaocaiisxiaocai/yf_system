using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;

namespace Yf.Api.Tests.Oem;

/// <summary>Opt-in integration against a real, explicitly provided loopback ClamAV daemon.</summary>
public sealed class OemClamAvIntegrationTests
{
    private static int RealPort()
    {
        var raw = Environment.GetEnvironmentVariable("YF_TEST_CLAMAV_PORT");
        if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_CLAMAV_PORT is not set; real ClamAV integration was not run");
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535) throw new InvalidOperationException("Invalid YF_TEST_CLAMAV_PORT");
        return port;
    }

    private static byte[] EicarZip()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("eicar.com").Open();
            entry.Write(Encoding.ASCII.GetBytes(FakeFileScanner.EicarSignature));
        }
        return bytes.ToArray();
    }

    [Fact(Timeout = 120_000)]
    public async Task RealEngineScansCleanContentAndDetectsEicarInsideArchive()
    {
        var port = RealPort();
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "yf_clamav_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var scanner = new ClamAvFileScanner(new OemClamAvOptions { Port = port });
            foreach (var (name, bytes, expected) in new[] {
                ("drawing.pdf", OemTestHost.Pdf("ordinary drawing"), ScanVerdict.Clean),
                ("test.zip", EicarZip(), ScanVerdict.Infected),
            })
            {
                var path = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(path, bytes, ct);
                var result = await scanner.ScanAsync(new ScanTarget(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), (ulong)bytes.Length), ct);
                Assert.Equal(expected, result.Verdict);
                Assert.Equal("ClamAV", result.EngineName);
                Assert.False(string.IsNullOrWhiteSpace(result.EngineVersion));
                Assert.False(string.IsNullOrWhiteSpace(result.SignatureVersion));
                if (expected == ScanVerdict.Infected) Assert.Contains("Eicar", result.ThreatName!, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact(Timeout = 240_000)]
    public async Task RealEngineControlsPromotionAndBlocksInfectedTransfer()
    {
        var port = RealPort();
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct, args =>
        {
            args.Add("--App:OemScanner:Engine=ClamAV");
            args.Add("--App:OemScanner:ClamAv:Port=" + port);
        });
        var world = await OemTransferFlowTests.OutboundWorldAsync(host, ct);
        // The configured engine supports this gate; the daemon must have current official definitions.
        await world.Admin.PutAsync("/api/v1/oem/file-policies", new
        {
            items = new[] { new { key = OemSettingCatalog.BlockOnStaleSignatures, value = "true" } },
        }, ct).Ok();
        foreach (var (name, bytes, verdict, lifecycle) in new[] {
            ("safe.pdf", OemTestHost.Pdf("ordinary drawing"), "CLEAN", "RELEASED"),
            ("unsafe.zip", EicarZip(), "INFECTED", "BLOCKED"),
        })
        {
            var draft = await world.Vendor.PostAsync("/api/v1/oem/transfers",
                new { title = "真实 ClamAV " + name, retentionTemplateId = world.KeepTemplateId }, ct).Ok();
            var id = OemTransferFlowTests.TransferId(draft);
            var file = await host.UploadAsync(world.Vendor, id, name, bytes, ct);
            await world.Vendor.PostAsync($"/api/v1/oem/transfers/{id}/send",
                new { version = OemTransferFlowTests.Version(draft) + 1 }, ct).Ok();
            await host.RunOemJobsAsync(ct);
            var detail = await world.Vendor.GetAsync($"/api/v1/oem/transfers/{id}", ct).Ok();
            Assert.Equal(lifecycle, detail["summary"]!["lifecycleStatus"]!.GetValue<string>());
            Assert.Equal(verdict, detail["files"]![0]!["scanStatus"]!.GetValue<string>());
            await using var conn = await host.OpenAsync(ct);
            var persisted = await conn.QuerySingleAsync<(string Engine, string Version, string Signature)>(
                "SELECT engine_name,engine_version,signature_version FROM oem_file_scan_jobs WHERE file_id=@id", new { id = file.Id() });
            Assert.Equal("ClamAV", persisted.Engine);
            Assert.False(string.IsNullOrWhiteSpace(persisted.Version));
            Assert.False(string.IsNullOrWhiteSpace(persisted.Signature));
        }
    }
}
