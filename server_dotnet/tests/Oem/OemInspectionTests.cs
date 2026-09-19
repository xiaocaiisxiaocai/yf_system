using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Tests.Oem;

/// <summary>Pure rules: who can see/read a transfer, content signatures and archive inspection.</summary>
public sealed class OemInspectionTests
{
    private const ulong Company = 5;
    private static readonly ArchiveLimits Limits = new(100, 3, 10 * 1024 * 1024, 100);

    private static TransferFacts Outbound(string lifecycle) => new(TransferDirections.InternalToOem, Company, lifecycle, 1, null);
    private static TransferFacts Inbound(string lifecycle) => new(TransferDirections.OemToInternal, Company, lifecycle, null, 7);
    private static ActorFacts Vendor(ulong id = 8, ulong company = Company) => new(OemRealms.Oem, id, company, false, false, false, false, false);
    private static ActorFacts Staff(ulong id, bool view = false, bool download = false, bool create = false, bool activeTask = false, bool anyTask = false) =>
        new(OemRealms.Internal, id, null, view, download, create, activeTask, anyTask);

    [Fact]
    public void DraftsArePrivateToTheirAuthor()
    {
        Assert.True(TransferAccess.Evaluate(Outbound(TransferLifecycle.Draft), Staff(1)).EditDraft);
        Assert.False(TransferAccess.Evaluate(Outbound(TransferLifecycle.Draft), Staff(2, view: true, download: true)).View);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Draft), Vendor()).View);
        Assert.True(TransferAccess.Evaluate(Inbound(TransferLifecycle.Draft), Vendor(7)).EditDraft);
    }

    [Fact]
    public void OutboundContentIsGatedByReviewAndRelease()
    {
        var sealedTransfer = Outbound(TransferLifecycle.Sealed);
        Assert.False(TransferAccess.Evaluate(sealedTransfer, Vendor()).View);
        Assert.Equal(ContentPurpose.Review, TransferAccess.Evaluate(sealedTransfer, Staff(3, activeTask: true, anyTask: true)).ContentAccess);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(sealedTransfer, Staff(3, anyTask: true)).ContentAccess);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(sealedTransfer, Staff(4, view: true, download: true)).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(sealedTransfer, Staff(1)).ContentAccess);

        var released = Outbound(TransferLifecycle.Released);
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(released, Vendor()).ContentAccess);
        Assert.False(TransferAccess.Evaluate(released, Vendor(9, company: 6)).View);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(released, Staff(1)).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(released, Staff(1, view: true, download: true)).ContentAccess);
    }

    [Fact]
    public void InboundContentIsSharedInsideTheVendorAfterRelease()
    {
        Assert.True(TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor()).View);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor()).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor(7)).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Vendor()).ContentAccess);
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true, download: true)).ContentAccess);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, download: true)).View);
    }

    [Fact]
    public void ClosedTransfersNeverExposeContent()
    {
        foreach (var lifecycle in new[] { TransferLifecycle.Blocked, TransferLifecycle.Rejected, TransferLifecycle.Cancelled, TransferLifecycle.Abandoned })
        {
            Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Outbound(lifecycle), Staff(1, view: true, download: true)).ContentAccess);
            Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Inbound(lifecycle), Vendor(7)).ContentAccess);
        }
    }

    [Fact]
    public void SignaturesMustMatchTheExtensionAndExecutablesAreRefused()
    {
        Assert.True(FileSignatureInspector.Inspect("%PDF-1.7\n"u8, "pdf").Accepted);
        Assert.False(FileSignatureInspector.Inspect([0x50, 0x4B, 0x03, 0x04], "pdf").Accepted);
        Assert.True(FileSignatureInspector.Inspect([0x50, 0x4B, 0x03, 0x04, 0x14], "docx").Accepted);
        Assert.False(FileSignatureInspector.Inspect("MZ-some-binary"u8, "stl").Accepted);
        Assert.False(FileSignatureInspector.Inspect("#!/bin/sh\necho"u8, "obj").Accepted);
        Assert.True(FileSignatureInspector.Inspect("ISO-10303-21;\nHEADER;"u8, "step").Accepted);
        Assert.False(FileSignatureInspector.Inspect("not a step file"u8, "stp").Accepted);
        Assert.True(FileSignatureInspector.Inspect("AC1032"u8, "dwg").Accepted);
    }

    [Fact]
    public async Task ZipArchivesAreBoundedAndEncryptedArchivesRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-inspect-").FullName;
        try
        {
            var plain = Write(work, "plain.zip", Zip(("a.txt", "hello"u8.ToArray())));
            Assert.Equal(ArchiveOutcome.Accepted, (await ArchiveInspector.InspectAsync(plain, "zip", Limits, work, ct)).Outcome);

            var encrypted = Write(work, "encrypted.zip", EncryptedZip());
            Assert.Equal(ArchiveOutcome.Encrypted, (await ArchiveInspector.InspectAsync(encrypted, "zip", Limits, work, ct)).Outcome);

            var bomb = Write(work, "bomb.zip", Zip(("zeros.bin", new byte[2 * 1024 * 1024])));
            Assert.Equal(ArchiveOutcome.LimitExceeded, (await ArchiveInspector.InspectAsync(bomb, "zip", Limits, work, ct)).Outcome);

            var many = Write(work, "many.zip", Zip(Enumerable.Range(0, 101).Select(i => ($"f{i}.txt", Encoding.ASCII.GetBytes("x" + i))).ToArray()));
            Assert.Equal(ArchiveOutcome.LimitExceeded, (await ArchiveInspector.InspectAsync(many, "zip", Limits, work, ct)).Outcome);

            // Nesting: depth 3 allowed, depth 4 refused; an encrypted archive hidden inside is still found.
            var level1 = Zip(("leaf.txt", "x"u8.ToArray()));
            var level2 = Zip(("l2.zip", level1));
            var level3 = Zip(("l3.zip", level2));
            Assert.Equal(ArchiveOutcome.Accepted, (await ArchiveInspector.InspectAsync(Write(work, "d3.zip", level3), "zip", Limits, work, ct)).Outcome);
            var level4 = Zip(("l4.zip", level3));
            Assert.Equal(ArchiveOutcome.LimitExceeded, (await ArchiveInspector.InspectAsync(Write(work, "d4.zip", level4), "zip", Limits, work, ct)).Outcome);
            var hidden = Zip(("inner.zip", EncryptedZip()));
            Assert.Equal(ArchiveOutcome.Encrypted, (await ArchiveInspector.InspectAsync(Write(work, "hidden.zip", hidden), "zip", Limits, work, ct)).Outcome);

            foreach (var name in new[] { "inner.bin", "inner", "drawing.pdf" })
            {
                var renamed = Zip((name, EncryptedZip()));
                Assert.Equal(ArchiveOutcome.Encrypted, (await ArchiveInspector.InspectAsync(Write(work, "renamed.zip", renamed), "zip", Limits, work, ct)).Outcome);
            }
            var renamedDeep = Zip(("l4.bin", Zip(("l3.bin", Zip(("l2.bin", level1))))));
            Assert.Equal(ArchiveOutcome.LimitExceeded, (await ArchiveInspector.InspectAsync(Write(work, "renamed-deep.zip", renamedDeep), "zip", Limits, work, ct)).Outcome);

            Assert.Equal(ArchiveOutcome.NotArchive, (await ArchiveInspector.InspectAsync(Write(work, "a.pdf", OemTestHost.Pdf("x")), "pdf", Limits, work, ct)).Outcome);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task RarAndSevenZipArchivesAreFullyInspected()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-rar-").FullName;
        try
        {
            var sevenZip = Write(work, "plain.7z", SevenZip(("a.txt", "hello"u8.ToArray())));
            Assert.Equal(ArchiveOutcome.Accepted, (await ArchiveInspector.InspectAsync(sevenZip, "7z", Limits, work, ct)).Outcome);

            var random = new byte[4096];
            new Random(42).NextBytes(random);
            var random2 = new byte[4096];
            new Random(43).NextBytes(random2);
            var incompressible = Write(work, "incompressible.7z", SevenZip(("a.bin", random), ("b.bin", random2)));
            Assert.Equal(ArchiveOutcome.Accepted,
                (await ArchiveInspector.InspectAsync(incompressible, "7z", Limits, work, ct)).Outcome);

            var many = Write(work, "many.7z", SevenZip(Enumerable.Range(0, 101).Select(i => ($"f{i}.txt", Encoding.ASCII.GetBytes("x" + i))).ToArray()));
            Assert.Equal(ArchiveOutcome.LimitExceeded, (await ArchiveInspector.InspectAsync(many, "7z", Limits, work, ct)).Outcome);

            var large = Write(work, "large.7z", SevenZip(("large.bin", Enumerable.Repeat((byte)0x5A, 1024).ToArray())));
            Assert.Equal(ArchiveOutcome.LimitExceeded,
                (await ArchiveInspector.InspectAsync(large, "7z", Limits with { MaxExpandedBytes = 512, MaxRatio = long.MaxValue }, work, ct)).Outcome);

            var highlyCompressed = Write(work, "ratio.7z", SevenZip(("zeros.bin", new byte[2 * 1024 * 1024])));
            Assert.Equal(ArchiveOutcome.LimitExceeded,
                (await ArchiveInspector.InspectAsync(highlyCompressed, "7z", Limits, work, ct)).Outcome);

            var level1 = SevenZip(("leaf.txt", "x"u8.ToArray()));
            var level2 = SevenZip(("l2.bin", level1));
            var level3 = SevenZip(("l3.bin", level2));
            Assert.Equal(ArchiveOutcome.Accepted,
                (await ArchiveInspector.InspectAsync(Write(work, "d3.7z", level3), "7z", Limits, work, ct)).Outcome);
            var level4 = SevenZip(("l4.bin", level3));
            Assert.Equal(ArchiveOutcome.LimitExceeded,
                (await ArchiveInspector.InspectAsync(Write(work, "d4.7z", level4), "7z", Limits, work, ct)).Outcome);

            // Apache Commons Compress test fixture, Apache-2.0:
            // https://github.com/apache/commons-compress/blob/master/src/test/resources/bla.encrypted.7z
            var encryptedSevenZip = Convert.FromBase64String(
                "N3q8ryccAAPOr6vM4AEAAAAAAAA2AAAAAAAAAMPV/c15gWF4/yjZnVseP50CUVynWCTYmSp6Ke098yYgF/M6rarfE3VIk5F/VKhRnpC3g8FD942gEXEDLnd12CRgkhC4qrpznDYACE9oBNV+7PMngR+vT6qzXjm/S08zuAzS7kP6aINNqGgZl3g9JTwGgqDCfkC35h3sjatUkdayb13cMgUiSA0sB7tR2E2lxhH/Hm30QeqD4dRDXT6l81+9PEOeQSNNZkrZfSy8lRJT5MKMAQtwUajG+BlU19/tUokPbvptGnP5PjZAHGEQrlK/avb4U0GKB12F4KU4v0zskuJwfX84UYn8fymldsL2Bn9pKrRQWx9b2AxZ97CUxdydqSv8Cc2U8gw1gJBMqOy7Af8JFTjOleTEou4N+ZdZw29Sf2QM1jKMJPSjGoVkwe2T8BmB4jM1PV+Hn8PAsrwN1K7vIMhOZEQyvSmXQFDSfDudsK6kFCdyZmqJjREA5baUovT+CsnOtEOy65EqnQmHtOh0ZYHPNn2UMuvzje+LVpw5OQaHXLyqSgA9PYSEuy9fqr+exsGfYZ6FE+K7UKPc5XLI60V7z6+4Z7em7vD6qG8IQYWNdQVJDXZbTmpDw/7nk+tXITiSCnowCfCNmLHbVSAubMECBTPUE2kDx01w49txxyIXBoFgAQmAgAAHCwEAAiQG8QcBClMHznmlTwXNceUjAwEBBV0AEAAAAQAMd4CRCgFx7ooQAAA=");
            Assert.Equal(ArchiveOutcome.Encrypted,
                (await ArchiveInspector.InspectAsync(Write(work, "encrypted.7z", encryptedSevenZip), "7z", Limits, work, ct)).Outcome);
            Assert.Equal(ArchiveOutcome.Encrypted,
                (await ArchiveInspector.InspectAsync(Write(work, "hidden.7z", SevenZip(("payload.bin", encryptedSevenZip))), "7z", Limits, work, ct)).Outcome);

            // Minimal RAR5 fixture from ssokolow/rar-test-files, CC0-1.0:
            // https://github.com/ssokolow/rar-test-files/blob/master/build/testfile.rar5.rar
            var tinyRar = Convert.FromBase64String("UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAAkmeyhIgICjAAGjAC2gwLQDlA6/o/BboAAAQx0ZXN0ZmlsZS50eHRUZXN0aW5nIDEyMwodd1ZRAwUEAA==");
            var rar = Write(work, "tiny.rar", tinyRar);
            Assert.Equal(ArchiveOutcome.Accepted, (await ArchiveInspector.InspectAsync(rar, "rar", Limits, work, ct)).Outcome);
            Assert.Equal(ArchiveOutcome.LimitExceeded,
                (await ArchiveInspector.InspectAsync(rar, "rar", Limits with { MaxEntries = 0 }, work, ct)).Outcome);
            Assert.Equal(ArchiveOutcome.LimitExceeded,
                (await ArchiveInspector.InspectAsync(rar, "rar", Limits with { MaxExpandedBytes = 5 }, work, ct)).Outcome);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ArchiveMagicTakesPrecedenceOverAnEmbeddedPdfMarker()
    {
        var bytes = Zip(("%PDF-drawing.txt", "payload"u8.ToArray()));
        Assert.Equal("zip", FileSignatureInspector.Detect(bytes));
        Assert.False(FileSignatureInspector.Inspect(bytes, "pdf").Accepted);
    }

    [Fact]
    public async Task ADirectoryNameCannotHideNestedArchiveContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-directory-entry-").FullName;
        try
        {
            var path = Write(work, "directory.zip", Zip(("hidden/", EncryptedZip())));
            Assert.Equal(ArchiveOutcome.Corrupt,
                (await ArchiveInspector.InspectAsync(path, "zip", Limits, work, ct)).Outcome);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task FakeEngineFindsTheEicarStringAcrossReadBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-eicar-").FullName;
        try
        {
            var prefix = new byte[1024 * 1024 - 10];
            var infected = prefix.Concat(Encoding.ASCII.GetBytes(FakeFileScanner.EicarSignature)).ToArray();
            Assert.Equal(ScanVerdict.Infected, (await new FakeFileScanner().ScanAsync(new ScanTarget(Write(work, "x.bin", infected), "", 0), ct)).Verdict);
            Assert.Equal(ScanVerdict.Clean, (await new FakeFileScanner().ScanAsync(new ScanTarget(Write(work, "y.bin", prefix), "", 0), ct)).Verdict);
            Assert.Equal(ScanVerdict.EngineUnavailable, (await new UnavailableFileScanner().ScanAsync(new ScanTarget(Write(work, "z.bin", prefix), "", 0), ct)).Verdict);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    internal static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                stream.Write(content);
            }
        return buffer.ToArray();
    }

    internal static byte[] SevenZip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(buffer, ArchiveType.SevenZip, new SevenZipWriterOptions { LeaveStreamOpen = true }))
            foreach (var (name, content) in entries)
                writer.Write(name, new MemoryStream(content), null);
        return buffer.ToArray();
    }

    /// <summary>A valid ZIP whose single entry carries the "encrypted" general-purpose flag in both headers.</summary>
    internal static byte[] EncryptedZip()
    {
        var bytes = Zip(("secret.txt", "classified"u8.ToArray()));
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) bytes[i + 6] |= 0x01;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) bytes[i + 8] |= 0x01;
        }
        return bytes;
    }

    private static string Write(string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
