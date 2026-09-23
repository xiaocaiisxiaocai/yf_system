using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class FilesStorageSafetyTests
{
    [Theory]
    [InlineData("drawing.PDF", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("pdf", "")]
    [InlineData("README", "")]
    [InlineData("trailing.", "")]
    public void ExtensionComesOnlyFromTheLastDotSuffix(string name, string expected)
    {
        Assert.Equal(expected, UploadService.ExtensionOf(name));
    }

    [Fact]
    public async Task PendingFinalMarkerIsPublishedAtomicallyAfterPayloadIsDurable()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "yf_file_marker_" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(sandbox, "storage");
        var sessionId = Guid.NewGuid().ToString("D");
        var directory = FileStorage.SessionDirectory(root, sessionId);
        Directory.CreateDirectory(directory);
        var relativePath = $"files/2026/09/{Guid.NewGuid():D}.pdf";

        try
        {
            await UploadService.WritePendingFinalMarkerAsync(
                FileStorage.Root(root), sessionId, relativePath, TestContext.Current.CancellationToken);

            var markers = Directory.GetFiles(directory, UploadService.PendingFinalMarkerPrefix + "*");
            Assert.Single(markers);
            Assert.Equal(relativePath, await File.ReadAllTextAsync(
                markers[0], TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, UploadService.PendingFinalStagingPrefix + "*"));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task RecursiveDeleteIsIdempotentAndRejectsTempLinkEscapingStorageRoot()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "yf_file_delete_" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(sandbox, "storage");
        var outside = Path.Combine(sandbox, "outside");
        var tempLink = Path.Combine(root, "tmp");
        var sessionId = Guid.NewGuid().ToString("D");
        var outsideSession = Path.Combine(outside, sessionId);
        var outsidePayload = Path.Combine(outsideSession, "keep.part");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideSession);
        await File.WriteAllTextAsync(outsidePayload, "must survive", TestContext.Current.CancellationToken);

        try
        {
            var candidate = FileStorage.SessionDirectory(FileStorage.Root(root), sessionId);
            Assert.False(FileStorage.DeleteDirectoryTree(
                root, candidate, CancellationToken.None));

            Directory.CreateSymbolicLink(tempLink, outside);

            Assert.Throws<InvalidOperationException>(() =>
                FileStorage.DeleteDirectoryTree(root, candidate, CancellationToken.None));

            Assert.True(File.Exists(outsidePayload));
            Assert.Equal("must survive", await File.ReadAllTextAsync(outsidePayload, TestContext.Current.CancellationToken));
        }
        finally
        {
            // Remove the link itself without recursive deletion, then clean the owned sandbox.
            if (Directory.Exists(tempLink)
                && (new DirectoryInfo(tempLink).Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(tempLink, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: false);
        }
    }

    [Fact]
    public void DirectoryCreationRejectsEscapingLinkBeforeCreatingOutsideStorageRoot()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "yf_file_create_" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(sandbox, "storage");
        var outside = Path.Combine(sandbox, "outside");
        var tempLink = Path.Combine(root, "tmp");
        var sessionId = Guid.NewGuid().ToString("D");
        var outsideSession = Path.Combine(outside, sessionId);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(tempLink, outside);

            Assert.Throws<InvalidOperationException>(() => FileStorage.CreateDirectoryWithin(
                root, FileStorage.SessionDirectory(FileStorage.Root(root), sessionId), CancellationToken.None));

            Assert.False(Directory.Exists(outsideSession));
        }
        finally
        {
            if (Directory.Exists(tempLink)
                && (new DirectoryInfo(tempLink).Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(tempLink, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: false);
        }
    }
}
