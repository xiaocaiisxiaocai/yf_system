using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Uploads;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Tests.Oem;

/// <summary>Archive entry-name, link, duplicate and dictionary rules, file-name rules and the validation queue order (pure, no database).</summary>
public sealed class OemContentRulesTests
{
    private static readonly ArchiveLimits Limits = new(100, 3, 10 * 1024 * 1024, 100);
    private static readonly byte[] Content = "x"u8.ToArray();

    [Theory]
    [InlineData("../evil.txt", true)]
    [InlineData("a/../b.txt", true)]
    [InlineData("/abs.txt", true)]
    [InlineData("C:/x.txt", true)]
    [InlineData("report.txt:hidden", true)]
    [InlineData("dir\\file.txt:stream", true)]
    [InlineData("con", true)]
    [InlineData("CON.txt", true)]
    [InlineData("docs/nul.tar.gz", true)]
    [InlineData("Aux ", true)]
    [InlineData("lpt1.log", true)]
    [InlineData("COM9.dat", true)]
    [InlineData("name.", true)]
    [InlineData("dir./file.txt", true)]
    [InlineData("name ", true)]
    [InlineData("...", true)]
    [InlineData("a/com0.txt", false)]
    [InlineData("console.txt", false)]
    [InlineData("drawing.pdf", false)]
    [InlineData("folder/", false)]
    [InlineData("folder/sub/part-1.step", false)]
    [InlineData("./local.txt", false)]
    [InlineData(".hidden", false)]
    public void EntryNamesAreCheckedSegmentBySegment(string name, bool unsafeName) =>
        Assert.Equal(unsafeName, ArchiveInspector.UnsafeEntryName(name));

    [Fact]
    public async Task ZipEntriesWithUnsafeNamesDuplicatesOrLinksAreForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-entry-rules-").FullName;
        try
        {
            async Task<ArchiveVerdict> InspectAsync(byte[] bytes)
            {
                var path = Path.Combine(work, Guid.NewGuid().ToString("N") + ".zip");
                await File.WriteAllBytesAsync(path, bytes, ct);
                return await ArchiveInspector.InspectAsync(path, "zip", Limits, work, ct);
            }

            Assert.Equal(ArchiveOutcome.Accepted,
                (await InspectAsync(OemInspectionTests.Zip(("a/b.txt", Content), ("c.txt", Content), ("folder/", Array.Empty<byte>())))).Outcome);

            foreach (var name in new[] { "payload.txt:stream", "CON.txt", "dir/NUL", "tail.", "dir /x.txt" })
                Assert.Equal(ArchiveOutcome.Forbidden, (await InspectAsync(OemInspectionTests.Zip((name, Content)))).Outcome);

            // Entries that would overwrite each other on a case-insensitive or normalising file system.
            foreach (var pair in new[]
            {
                ("Readme.txt", "README.TXT"),
                ("caf\u00E9.txt", "cafe\u0301.txt"),
                ("dir/a.txt", "dir\\a.txt"),
            })
            {
                var verdict = await InspectAsync(OemInspectionTests.Zip((pair.Item1, Content), (pair.Item2, Content)));
                Assert.Equal(ArchiveOutcome.Forbidden, verdict.Outcome);
                Assert.Contains("重复", verdict.Reason!);
            }

            var withLink = await InspectAsync(ZipWithMode("link-to-secret", unchecked((int)0xA1FF0000)));
            Assert.Equal(ArchiveOutcome.Forbidden, withLink.Outcome);
            Assert.Contains("链接", withLink.Reason!);
            // A regular file carrying a Unix mode is fine.
            Assert.Equal(ArchiveOutcome.Accepted, (await InspectAsync(ZipWithMode("regular.txt", unchecked((int)0x81A40000)))).Outcome);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public void LinkAttributesAreRecognisedForZipAndSevenZip()
    {
        Assert.False(ArchiveInspector.IsLinkAttributes(null, sevenZip: false));
        Assert.False(ArchiveInspector.IsLinkAttributes(unchecked((int)0x81A40000), sevenZip: false));
        Assert.True(ArchiveInspector.IsLinkAttributes(unchecked((int)0xA1FF0000), sevenZip: false));
        // 7z: a Unix mode only counts together with its 0x8000 extension flag; a reparse point is a link by itself.
        Assert.False(ArchiveInspector.IsLinkAttributes(unchecked((int)0xA1FF0020), sevenZip: true));
        Assert.True(ArchiveInspector.IsLinkAttributes(unchecked((int)0xA1FF8020), sevenZip: true));
        Assert.True(ArchiveInspector.IsLinkAttributes(0x420, sevenZip: true));
        Assert.False(ArchiveInspector.IsLinkAttributes(0x20, sevenZip: true));
        Assert.False(ArchiveInspector.IsLinkAttributes(unchecked((int)0x81A48020), sevenZip: true));
    }

    [Fact]
    public async Task SevenZipEntriesWithUnsafeNamesOrDuplicatesAreForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-7z-entry-rules-").FullName;
        try
        {
            async Task<ArchiveVerdict> InspectAsync(byte[] bytes)
            {
                var path = Path.Combine(work, Guid.NewGuid().ToString("N") + ".7z");
                await File.WriteAllBytesAsync(path, bytes, ct);
                return await ArchiveInspector.InspectAsync(path, "7z", Limits, work, ct);
            }

            Assert.Equal(ArchiveOutcome.Accepted, (await InspectAsync(OemInspectionTests.SevenZip(("a.txt", Content), ("b.txt", Content)))).Outcome);
            Assert.Equal(ArchiveOutcome.Forbidden, (await InspectAsync(OemInspectionTests.SevenZip(("aux.txt", Content)))).Outcome);
            Assert.Equal(ArchiveOutcome.Forbidden, (await InspectAsync(OemInspectionTests.SevenZip(("a.txt:ads", Content)))).Outcome);
            Assert.Equal(ArchiveOutcome.Forbidden,
                (await InspectAsync(OemInspectionTests.SevenZip(("Same.txt", Content), ("SAME.txt", Content)))).Outcome);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public void RarWindowSizeIsReadableFromTheEntryHeader()
    {
        var work = Directory.CreateTempSubdirectory("oem-rar-window-").FullName;
        try
        {
            // The same RAR3 solid and RAR5 fixtures as in OemInspectionTests (ssokolow/rar-test-files, CC0-1.0).
            var fixtures = new (string Name, string Base64)[]
            {
                ("rar3-solid.rar", "UmFyIRoHADvQcwgADQAAAAAAAAALIXSAgCwAGwAAAAwAAAAA/o/BbgAAISodNQwAIAAAAHRlc3RmaWxlLnR4dKcYVBNehL0MdSfwO2HcUnaQEg0Av4hn9qn/1MQ9ewBABwA="),
                ("rar5.rar", "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAAkmeyhIgICjAAGjAC2gwLQDlA6/o/BboAAAQx0ZXN0ZmlsZS50eHRUZXN0aW5nIDEyMwodd1ZRAwUEAA=="),
            };
            foreach (var (name, base64) in fixtures)
            {
                var path = Path.Combine(work, name);
                File.WriteAllBytes(path, Convert.FromBase64String(base64));
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(path, SharpCompress.Readers.ReaderOptions.ForFilePath);
                var entry = Assert.Single(archive.Entries, item => !item.IsDirectory);
                // If this fails after a SharpCompress upgrade the dictionary check silently does nothing.
                var window = ArchiveInspector.RarWindowSize(entry);
                Assert.NotNull(window);
                Assert.InRange(window.Value, 1L, ArchiveInspector.MaxDecoderDictionaryBytes);
            }
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Theory]
    [InlineData("invoice\u202Egpj.exe")]
    [InlineData("a\u2066b.pdf")]
    [InlineData("a\u200Eb.pdf")]
    [InlineData("a\u200Fb.pdf")]
    [InlineData("a\u200Bb.pdf")]
    [InlineData("a\uFEFFb.pdf")]
    [InlineData("a\U000E0041b.pdf")]
    public void FileNamesWithInvisibleFormatCharactersAreRefused(string name)
    {
        var error = Assert.Throws<ApiException>(() => OemUploadService.ValidateFileName(name));
        Assert.Equal(400, error.Status);
        Assert.Contains("格式控制字符", error.Message);
    }

    [Fact]
    public void OrdinaryUnicodeFileNamesStillPass()
    {
        Assert.Equal("设计图 v2.pdf", OemUploadService.ValidateFileName("  设计图 v2.pdf "));
        Assert.Equal("部件😀.step", OemUploadService.ValidateFileName("部件😀.step"));
        Assert.Throws<ApiException>(() => OemUploadService.ValidateFileName("a/b.pdf"));
        Assert.Throws<ApiException>(() => OemUploadService.ValidateFileName("noextension"));
    }

    [Fact]
    public void ValidationJobsAreTakenRoundRobinByVendor()
    {
        // Vendor 1 queued jobs 1-6 before vendor 2 (7, 8) and vendor 3 (9).
        var jobs = new (ulong JobId, ulong CompanyId)[] { (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1), (7, 2), (8, 2), (9, 3) };

        Assert.Equal(new ulong[] { 1, 7, 9, 2 }, OemFileValidationService.RoundRobinByCompany(jobs, 4));
        Assert.Equal(new ulong[] { 1, 7, 9, 2, 8, 3 }, OemFileValidationService.RoundRobinByCompany(jobs, 6));
        // One vendor alone keeps its own order; nothing queued yields nothing.
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, OemFileValidationService.RoundRobinByCompany(jobs.Where(job => job.CompanyId == 1), 4));
        Assert.Empty(OemFileValidationService.RoundRobinByCompany(Array.Empty<(ulong, ulong)>(), 4));
    }

    /// <summary>A ZIP whose single entry carries <paramref name="externalAttributes"/> (the Unix mode sits in the high 16 bits).</summary>
    private static byte[] ZipWithMode(string name, int externalAttributes)
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = externalAttributes;
            using var stream = entry.Open();
            stream.Write(Content);
        }
        return buffer.ToArray();
    }
}
