using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class BatchDownloadStreamingTests
{
    [Fact]
    public async Task ArchiveStreamsEveryFileWithUniqueNamesAndReleasesTheLease()
    {
        var directory = Directory.CreateTempSubdirectory("yf_zip_stream_");
        try
        {
            var large = new byte[3 * 1024 * 1024 + 17];
            Random.Shared.NextBytes(large);
            var first = Path.Combine(directory.FullName, "a.bin");
            var second = Path.Combine(directory.FullName, "b.bin");
            await File.WriteAllBytesAsync(first, large, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "second", TestContext.Current.CancellationToken);
            var lease = new CountingLease();
            var result = new FileService.ZipStreamResult(
                [new(first, "报告.bin"), new(second, "报告.bin"), new(second, "notes.txt")], "yf_files_test.zip", lease);
            var context = new DefaultHttpContext();
            using var body = new MemoryStream();
            context.Response.Body = body;

            await result.ExecuteAsync(context);

            Assert.Equal("application/zip", context.Response.ContentType);
            Assert.Contains("yf_files_test.zip", context.Response.Headers.ContentDisposition.ToString());
            Assert.Equal(1, lease.Disposals);
            body.Position = 0;
            using var archive = new ZipArchive(body, ZipArchiveMode.Read);
            Assert.Equal(["报告.bin", "报告 (2).bin", "notes.txt"], archive.Entries.Select(entry => entry.FullName));
            using var copy = new MemoryStream();
            await using (var entry = archive.Entries[0].Open()) await entry.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(large, copy.ToArray());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task AFailureMidStreamAbortsTheConnectionInsteadOfEndingTheDownload()
    {
        var lease = new CountingLease();
        var result = new FileService.ZipStreamResult(
            [new(Path.Combine(Path.GetTempPath(), "yf-missing-" + Guid.NewGuid().ToString("N")), "gone.bin")],
            "yf_files_test.zip", lease);
        var aborted = new AbortRecorder();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(aborted);
        context.Response.Body = new MemoryStream();

        await Assert.ThrowsAnyAsync<IOException>(() => result.ExecuteAsync(context));
        Assert.True(aborted.Aborted);
        Assert.Equal(1, lease.Disposals);
    }

    private sealed class CountingLease : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}
