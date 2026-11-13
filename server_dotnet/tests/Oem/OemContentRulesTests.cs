using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Uploads;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Tests.Oem;

/// <summary>File-name rules and the validation queue order (pure, no database).</summary>
public sealed class OemContentRulesTests
{
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
}
