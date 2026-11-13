using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Validation;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Tests.Oem;

/// <summary>Pure rules: who can see/read a transfer, content signatures and file validation.</summary>
public sealed class OemInspectionTests
{
    private const ulong Company = 5;

    private static TransferFacts Outbound(string lifecycle) => new(TransferDirections.InternalToOem, Company, lifecycle, 1, null);
    private static TransferFacts Inbound(string lifecycle) => new(TransferDirections.OemToInternal, Company, lifecycle, null, 7);
    private static ActorFacts Vendor(ulong id = 8, ulong company = Company) => new(OemRealms.Oem, id, company, false, false, false, false, false);
    private static ActorFacts Staff(ulong id, bool view = false, bool download = false, bool create = false,
        bool activeTask = false, bool anyTask = false, bool recover = false, bool admin = false, bool notified = false, bool scope = false) =>
        new(OemRealms.Internal, id, null, view, download, create, activeTask, anyTask, recover, admin, notified, scope);

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
        // An approver of an earlier node keeps reading while later nodes decide.
        Assert.Equal(ContentPurpose.Review, TransferAccess.Evaluate(sealedTransfer, Staff(3, anyTask: true)).ContentAccess);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(sealedTransfer, Staff(4, view: true, download: true, admin: true)).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(sealedTransfer, Staff(1)).ContentAccess);

        var released = Outbound(TransferLifecycle.Released);
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(released, Vendor()).ContentAccess);
        Assert.False(TransferAccess.Evaluate(released, Vendor(9, company: 6)).View);
        // After release the company side can still check what went out; none of it is a vendor receipt.
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(released, Staff(1)).ContentAccess);
        Assert.Equal(ContentPurpose.Review, TransferAccess.Evaluate(released, Staff(3, anyTask: true)).ContentAccess);
        // Only administrators read other people's outbound transfers; plain view + download staff do not even see them.
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(released, Staff(4, view: true, download: true, admin: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(released, Staff(4, view: true, download: true)).View);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(released, Staff(5, view: true, admin: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(released, Staff(1)).IsRecipient);

        // Transfers closed without release stay unreadable.
        foreach (var closed in new[] { TransferLifecycle.Rejected, TransferLifecycle.Blocked, TransferLifecycle.Cancelled })
            Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Outbound(closed), Staff(3, view: true, download: true, anyTask: true)).ContentAccess);
    }

    [Fact]
    public void RecoveryPermissionSeesOnlyBlockedApprovalMetadataWithoutFileContent()
    {
        var blocked = new TransferFacts(TransferDirections.InternalToOem, Company, TransferLifecycle.Sealed, 1, null,
            ApprovalBlocked: true);
        var capability = TransferAccess.Evaluate(blocked, Staff(9, recover: true));
        Assert.True(capability.View);
        Assert.True(capability.RecoveryOnly);
        Assert.Equal(ContentPurpose.None, capability.ContentAccess);

        Assert.False(TransferAccess.Evaluate(Outbound(TransferLifecycle.Sealed), Staff(9, recover: true)).View);
        Assert.False(TransferAccess.Evaluate(Outbound(TransferLifecycle.Released), Staff(9, recover: true)).View);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Staff(9, recover: true)).View);
    }

    [Fact]
    public void InboundContentIsSharedInsideTheVendorAfterRelease()
    {
        Assert.True(TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor()).View);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor()).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(Inbound(TransferLifecycle.Sealed), Vendor(7)).ContentAccess);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Vendor()).ContentAccess);
        // Inbound: the staff the vendor chose to notify and administrators see it; other view + download staff do not.
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true, download: true, notified: true)).ContentAccess);
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true, download: true, admin: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true, download: true)).View);
        Assert.Equal(ContentPurpose.None, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, view: true, notified: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(2, download: true, notified: true, admin: true)).View);
        // A supervisor whose organisation contains the sender or a notified person sees it like that person, but needs the view permission.
        Assert.Equal(ContentPurpose.Recipient, TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(6, view: true, download: true, scope: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(Inbound(TransferLifecycle.Released), Staff(6, download: true, scope: true)).View);
        Assert.Equal(ContentPurpose.Sender, TransferAccess.Evaluate(Outbound(TransferLifecycle.Released), Staff(6, view: true, download: true, scope: true)).ContentAccess);
        Assert.False(TransferAccess.Evaluate(Outbound(TransferLifecycle.Released), Staff(6, view: true, download: true)).View);
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
    public void ValidationSummaryRequiresEveryFileToBeValidAndFailsUnknownLegacyValuesClosed()
    {
        Assert.Null(OemTransferReader.ValidationSummary([]));
        Assert.Equal(ValidationStatuses.Valid,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, ValidationStatuses.Valid]));
        Assert.Equal(ValidationStatuses.Invalid,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, ValidationStatuses.Error, ValidationStatuses.Invalid]));
        Assert.Equal(ValidationStatuses.Error,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, ValidationStatuses.Validating, ValidationStatuses.Error]));
        Assert.Equal(ValidationStatuses.Validating,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, ValidationStatuses.Pending, ValidationStatuses.Validating]));
        Assert.Equal(ValidationStatuses.Pending,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, ValidationStatuses.Pending]));
        Assert.Equal(ValidationStatuses.Error,
            OemTransferReader.ValidationSummary([ValidationStatuses.Valid, "CLEAN"]));
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
    public void RowLocksResolveTheOemTableAndKeyFromTheEfModel()
    {
        var options = new DbContextOptionsBuilder<Yf.Api.Infrastructure.YfDbContext>()
            .UseMySql("Server=127.0.0.1;Database=offline", new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        using var db = new Yf.Api.Infrastructure.YfDbContext(options);

        Assert.Equal(("oem_transfers", "id"), OemLocks.Target<Yf.Api.Modules.Oem.Data.OemTransfer>(db));
        Assert.Equal(("oem_transfer_files", "id"), OemLocks.Target<Yf.Api.Modules.Oem.Data.OemTransferFile>(db));
        Assert.Equal(("oem_upload_sessions", "id"), OemLocks.Target<Yf.Api.Modules.Oem.Data.OemUploadSession>(db));
        Assert.Equal(("oem_download_leases", "id"), OemLocks.Target<Yf.Api.Modules.Oem.Data.OemDownloadLease>(db));

        var sql = EntityFrameworkQueryableExtensions.ToQueryString(
            OemLocks.ForUpdate<Yf.Api.Modules.Oem.Data.OemTransferFile>(db, 42UL));
        // The key is a parameter, never spliced into the SQL text.
        Assert.Contains("SELECT * FROM `oem_transfer_files` WHERE `id` = @p0 FOR UPDATE", sql);

        // Types outside the EF model, and mapped tables outside the OEM module, are refused.
        Assert.Throws<InvalidOperationException>(() => OemLocks.Target<OemInspectionTests>(db));
        Assert.Throws<InvalidOperationException>(() => OemLocks.Target<Yf.Api.Infrastructure.Entities.User>(db));
        Assert.Throws<InvalidOperationException>(() => OemLocks.ForUpdate<Yf.Api.Infrastructure.Entities.User>(db, 1UL));
    }

    [Fact]
    public void ArchiveMagicTakesPrecedenceOverAnEmbeddedPdfMarker()
    {
        var bytes = Zip(("%PDF-drawing.txt", "payload"u8.ToArray()));
        Assert.Equal("zip", FileSignatureInspector.Detect(bytes));
        Assert.False(FileSignatureInspector.Inspect(bytes, "pdf").Accepted);
    }

    [Fact]
    public async Task ValidationPipelineRejectsTamperedDigestAndDisguisedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-validation-").FullName;
        try
        {
            var pipeline = new OemFileValidationPipeline();
            var validBytes = OemTestHost.Pdf("drawing");
            var validPath = Write(work, "valid.pdf", validBytes);
            Assert.Equal(ValidationVerdict.Valid, (await pipeline.RunAsync(Target(validPath, validBytes), "pdf", ct)).Verdict);

            var corruptBytes = validBytes.ToArray();
            corruptBytes[^2] ^= 0x3C;
            var corruptPath = Write(work, "corrupt.pdf", corruptBytes);
            var tampered = await pipeline.RunAsync(Target(validPath, validBytes) with { Path = corruptPath }, "pdf", ct);
            Assert.Equal(ValidationVerdict.Invalid, tampered.Verdict);
            Assert.Contains("SHA-256", tampered.Message!);

            byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x6E, 0x6F, 0x74];
            var disguised = await pipeline.RunAsync(Target(Write(work, "disguised.pdf", executable), executable), "pdf", ct);
            Assert.Equal(ValidationVerdict.Invalid, disguised.Verdict);
            Assert.Contains("可执行程序", disguised.Message!);

            // An Office extension still needs a ZIP container; an OLE2 (encrypted or legacy) file under .docx does not match.
            byte[] ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];
            var wrongOffice = await pipeline.RunAsync(Target(Write(work, "encrypted.docx", ole2), ole2), "docx", ct);
            Assert.Equal((ValidationVerdict.Invalid, "文件内容与扩展名 .docx 不符"), (wrongOffice.Verdict, wrongOffice.Message));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    /// <summary>Since 2026-11-13 only the format is checked: archive and Office contents are never opened.</summary>
    [Theory]
    [InlineData("zip")]
    [InlineData("docx")]
    public async Task ArchiveAndOfficeContentsAreNotInspected(string extension)
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-format-only-").FullName;
        try
        {
            var pipeline = new OemFileValidationPipeline();
            byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x6E, 0x6F, 0x74];
            var random = new byte[256 * 1024];
            new Random(7).NextBytes(random);
            // Previously refused: an encrypted entry, an executable, an archive hidden behind leading bytes, unsafe names.
            foreach (var content in new[]
            {
                EncryptedZip(),
                Zip(("tools/setup.dat", executable)),
                Zip(("drawing.bin", [.. random, .. EncryptedZip(), .. "trailing"u8.ToArray()])),
                Zip(("../escape.txt", "x"u8.ToArray()), ("Same.txt", "x"u8.ToArray()), ("SAME.txt", "x"u8.ToArray())),
                Zip(("inner.zip", Zip(("deeper.zip", Zip(("a.txt", "x"u8.ToArray())))))),
            })
            {
                var result = await pipeline.RunAsync(Target(Write(work, "package." + extension, content), content), extension, ct);
                Assert.Equal(ValidationVerdict.Valid, result.Verdict);
            }
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Theory]
    [InlineData("rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 0xFF })]
    [InlineData("7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, 0xFF })]
    public async Task RarAndSevenZipOnlyNeedTheirSignature(string extension, byte[] content)
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-format-only-").FullName;
        try
        {
            var pipeline = new OemFileValidationPipeline();
            Assert.Equal(ValidationVerdict.Valid,
                (await pipeline.RunAsync(Target(Write(work, "a." + extension, content), content), extension, ct)).Verdict);
            var zip = Zip(("a.txt", "x"u8.ToArray()));
            var mismatch = await pipeline.RunAsync(Target(Write(work, "b." + extension, zip), zip), extension, ct);
            Assert.Equal((ValidationVerdict.Invalid, $"文件内容与扩展名 .{extension} 不符"), (mismatch.Verdict, mismatch.Message));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task OtherFilesMayCarryArchiveBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-format-only-").FullName;
        try
        {
            byte[] pdf = [.. OemTestHost.Pdf("cover"), .. EncryptedZip(), 0x0A];
            var result = await new OemFileValidationPipeline().RunAsync(Target(Write(work, "cover.pdf", pdf), pdf), "pdf", ct);
            Assert.Equal(ValidationVerdict.Valid, result.Verdict);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static ValidationTarget Target(string path, byte[] content) => new(path,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), (ulong)content.Length);

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
