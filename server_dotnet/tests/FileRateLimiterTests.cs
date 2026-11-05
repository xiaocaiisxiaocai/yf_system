using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class FileRateLimiterTests
{
    [Fact]
    public void EachAccountMaySixtyStartsPerMinuteAndEachScopeCountsSeparately()
    {
        var options = new AppOptions();
        Assert.Equal(60, options.FileTransferStartsPerMinutePerUser);
        var limiter = new FileRateLimiter(options);
        var start = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

        for (var call = 0; call < 60; call++)
            Assert.True(limiter.Allow(7, FileRateLimiter.FileUploadScope, start.AddSeconds(call / 2.0)));
        Assert.False(limiter.Allow(7, FileRateLimiter.FileUploadScope, start.AddSeconds(59)));

        // Image messages, downloads, other accounts and the next minute are unaffected.
        Assert.True(limiter.Allow(7, FileRateLimiter.MessageImageScope, start.AddSeconds(59)));
        Assert.True(limiter.Allow(7, FileRateLimiter.DownloadScope, start.AddSeconds(59)));
        Assert.True(limiter.Allow(8, FileRateLimiter.FileUploadScope, start.AddSeconds(59)));
        Assert.True(limiter.Allow(7, FileRateLimiter.FileUploadScope, start.AddMinutes(1)));
    }

    [Fact]
    public void ASpentBudgetAnswers429WithTheRateLimitCode()
    {
        var limiter = new FileRateLimiter(new AppOptions { FileTransferStartsPerMinutePerUser = 1 });
        limiter.Require(9, FileRateLimiter.FileUploadScope);
        var refused = Assert.Throws<ApiException>(() => limiter.Require(9, FileRateLimiter.FileUploadScope));
        Assert.Equal(429, refused.Status);
        Assert.Equal(42902, refused.Code);
        Assert.Contains("上传", refused.Message);
        limiter.Require(9, FileRateLimiter.DownloadScope);
        Assert.Contains("下载", Assert.Throws<ApiException>(() => limiter.Require(9, FileRateLimiter.DownloadScope)).Message);
    }
}
