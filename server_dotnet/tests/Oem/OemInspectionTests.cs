using System.IO.Compression;
using System.Text;
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

            Assert.Equal(ArchiveOutcome.NotArchive, (await ArchiveInspector.InspectAsync(Write(work, "a.pdf", OemTestHost.Pdf("x")), "pdf", Limits, work, ct)).Outcome);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void RarAndSevenZipEncryptionMarkersAreDetected()
    {
        var work = Directory.CreateTempSubdirectory("oem-rar-").FullName;
        try
        {
            // RAR4: marker block, then an archive header with the "headers encrypted" flag (0x0080).
            byte[] rar4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00, 0x00, 0x73, 0x80, 0x00, 0x0D, 0x00, 0, 0, 0, 0, 0, 0];
            Assert.Equal(ArchiveOutcome.Encrypted, ArchiveInspector.InspectRar(Write(work, "e.rar", rar4)).Outcome);
            byte[] rar4Plain = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00, 0x00, 0x73, 0x00, 0x00, 0x0D, 0x00, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x7B, 0x00, 0x40, 0x07, 0x00];
            Assert.Equal(ArchiveOutcome.Accepted, ArchiveInspector.InspectRar(Write(work, "p.rar", rar4Plain)).Outcome);

            // 7z: start header pointing at a next-header that names the AES coder.
            var header = new byte[] { 0x01, 0x04, 0x06, 0x00, 0x06, 0xF1, 0x07, 0x01, 0x00 };
            var sevenZip = new byte[32 + header.Length];
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }.CopyTo(sevenZip, 0);
            BitConverter.GetBytes(0UL).CopyTo(sevenZip, 12);
            BitConverter.GetBytes((ulong)header.Length).CopyTo(sevenZip, 20);
            header.CopyTo(sevenZip, 32);
            Assert.Equal(ArchiveOutcome.Encrypted, ArchiveInspector.InspectSevenZip(Write(work, "e.7z", sevenZip)).Outcome);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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
