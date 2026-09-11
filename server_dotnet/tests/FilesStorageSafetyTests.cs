using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class FilesStorageSafetyTests
{
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
            Assert.False(await FileStorage.DeleteDirectoryTreeAsync(
                root, candidate, CancellationToken.None));

            Directory.CreateSymbolicLink(tempLink, outside);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FileStorage.DeleteDirectoryTreeAsync(root, candidate, CancellationToken.None));

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
}
