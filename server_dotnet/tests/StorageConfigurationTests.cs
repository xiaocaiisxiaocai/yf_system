using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class StorageConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALinkedAncestorCannotPlaceStorageInsideTheApplication(bool existingLeaf)
    {
        var sandbox = Directory.CreateTempSubdirectory("yf_storage_config_").FullName;
        var app = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
        Directory.CreateDirectory(Path.Combine(app, "nested"));
        var alias = Path.Combine(sandbox, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, app);
            var suffix = existingLeaf ? "payload" : Path.Combine("new", "payload");
            if (existingLeaf) Directory.CreateDirectory(Path.Combine(app, "nested", suffix));
            var options = new AppOptions { StorageRoot = Path.Combine(alias, "nested", suffix) };
            Assert.Throws<InvalidOperationException>(() => options.ValidateStorageLocation(app));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias, recursive: false);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void ALinkedAncestorCannotPutOemContentInBackedUpStorage()
    {
        var sandbox = Directory.CreateTempSubdirectory("yf_oem_storage_config_").FullName;
        var app = Directory.CreateDirectory(Path.Combine(sandbox, "app")).FullName;
        var collaboration = Directory.CreateDirectory(Path.Combine(sandbox, "collaboration")).FullName;
        Directory.CreateDirectory(Path.Combine(collaboration, "nested"));
        var alias = Path.Combine(sandbox, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, collaboration);
            var options = new AppOptions { StorageRoot = collaboration, OemStorageRoot = Path.Combine(alias, "nested", "oem") };
            Assert.Throws<InvalidOperationException>(() => options.ValidateStorageLocation(app));

            options.OemStorageRoot = Path.Combine(sandbox, "separate-oem");
            options.ValidateStorageLocation(app);
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias, recursive: false);
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
