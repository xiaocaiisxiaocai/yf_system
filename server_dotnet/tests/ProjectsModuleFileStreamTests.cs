using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectsModuleFileStreamTests
{
    [Fact]
    public void MissingMessageImageFileOrDirectoryMapsToNotFound()
    {
        var root = Path.Combine(Path.GetTempPath(), "yf_missing_message_image_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AssertNotFound(Path.Combine(root, "missing.png"));
        }
        finally
        {
            Directory.Delete(root);
        }

        AssertNotFound(Path.Combine(root, "missing.png"));
    }

    [Fact]
    public async Task OpenedMessageImageStreamIsReturnedToAndOwnedByTheCaller()
    {
        var path = Path.Combine(Path.GetTempPath(), "yf_message_image_" + Guid.NewGuid().ToString("N") + ".png");
        var expected = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, expected, TestContext.Current.CancellationToken);
        try
        {
            await using var stream = ProjectsModule.OpenMessageImageStream(path);
            var actual = new byte[expected.Length];
            await stream.ReadExactlyAsync(actual, TestContext.Current.CancellationToken);
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertNotFound(string path)
    {
        var error = Assert.Throws<ApiException>(() => ProjectsModule.OpenMessageImageStream(path));
        Assert.Equal(404, error.Status);
        Assert.Equal(40401, error.Code);
    }
}
