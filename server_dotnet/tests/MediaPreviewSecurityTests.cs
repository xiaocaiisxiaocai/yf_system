using Microsoft.AspNetCore.Http;
using System.Text;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class MediaPreviewSecurityTests
{
    private static readonly AppOptions Options = new()
    {
        JwtSecret = "media-preview-test-secret-with-at-least-32-bytes",
        AccessTtlMinutes = 30
    };

    [Fact]
    public void MediaGrantIsFileAndSessionScopedAndRejectsTampering()
    {
        var service = new MediaGrantService(Options);
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var token = service.Issue(42, "session-family", 9001, now, MediaGrantService.LifetimeSeconds);

        var parsed = service.Parse(token, now.AddSeconds(299));
        Assert.Equal((ulong)42, parsed.UserId);
        Assert.Equal("session-family", parsed.SessionId);
        Assert.Equal((ulong)9001, parsed.FileId);
        Assert.Equal(now.AddSeconds(MediaGrantService.LifetimeSeconds).ToUnixTimeSeconds(), parsed.ExpiresAt);
        Assert.Throws<ApiException>(() => service.Parse(token, now.AddSeconds(MediaGrantService.LifetimeSeconds)));

        var parts = token.Split('.');
        parts[1] = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        Assert.Throws<ApiException>(() => service.Parse(string.Join('.', parts), now));
    }

    [Fact]
    public void AccessTokenCannotBeUsedAsMediaGrant()
    {
        var access = new TokenService(Options).IssueAccess(42, "E00042", "session-family").Token;
        Assert.Throws<ApiException>(() => new MediaGrantService(Options).Parse(access));
    }

    [Fact]
    public void MediaSigningKeyIsDomainSeparatedFromJwtSigningKey()
    {
        var derived = MediaGrantService.DeriveKey(Options.JwtSecret);
        Assert.False(derived.SequenceEqual(Encoding.UTF8.GetBytes(Options.JwtSecret)));
    }

    [Theory]
    [InlineData("https://app.example", "same-origin", "https://app.example", true)]
    [InlineData("", "same-origin", "https://app.example", true)]
    [InlineData("", "", "https://app.example", true)]
    [InlineData("https://evil.example", "cross-site", "https://app.example", false)]
    [InlineData("https://evil.example", "", "https://app.example", false)]
    public void DownloadOriginCheckRejectsExplicitCrossOriginRequestsButKeepsNonBrowserClients(
        string origin, string fetchSite, string configuredOrigin, bool expected)
    {
        Assert.Equal(expected, FileService.IsSameOriginRequest(origin, fetchSite, configuredOrigin));
    }

    [Fact]
    public void FileCookiesShareStrictScopedSecurityOptions()
    {
        var persistent = FileService.CreateScopedCookieOptions(
            secure: true, "/api/v1/files/7/media", lifetimeSeconds: 300);
        var deletion = FileService.CreateScopedCookieOptions(
            secure: false, "/api/v1/files/7/native-download/handle", lifetimeSeconds: null);

        Assert.True(persistent.HttpOnly);
        Assert.True(persistent.Secure);
        Assert.Equal(SameSiteMode.Strict, persistent.SameSite);
        Assert.Equal("/api/v1/files/7/media", persistent.Path);
        Assert.Equal(TimeSpan.FromSeconds(300), persistent.MaxAge);
        Assert.True(deletion.HttpOnly);
        Assert.False(deletion.Secure);
        Assert.Equal(SameSiteMode.Strict, deletion.SameSite);
        Assert.Equal("/api/v1/files/7/native-download/handle", deletion.Path);
        Assert.Null(deletion.MaxAge);
    }

    [Theory]
    [InlineData("GET", "/api/v1/files/1/media", true)]
    [InlineData("GET", "/api/v1/files/9223372036854775807/media", true)]
    [InlineData("GET", "/api/v1/files/18446744073709551615/media", false)]
    [InlineData("POST", "/api/v1/files/1/media", false)]
    [InlineData("GET", "/api/v1/files/1/media/extra", false)]
    [InlineData("GET", "/api/v1/files/not-a-number/media", false)]
    [InlineData("GET", "/api/v1/files/1/media-session", false)]
    public void IdentityBypassIsLimitedToExactMediaGet(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(expected, IdentityMiddleware.IsMediaRequest(context.Request));
    }

    [Theory]
    [InlineData("mp4", "video/mp4")]
    [InlineData("MP4", "video/mp4")]
    [InlineData("webm", "video/webm")]
    [InlineData("ogv", "video/ogg")]
    [InlineData("mov", null)]
    public void VideoTypesUseCanonicalMime(string extension, string? expected)
    {
        Assert.Equal(expected, FileService.MediaMimeType(extension));
        Assert.Equal(expected is not null, FileService.IsVideo(extension));
    }

    [Fact]
    public void PptxAndVideoMimeMappingsAreCanonical()
    {
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation",
            FileStorage.MimeType("slides.pptx"));
        Assert.Equal("video/mp4", FileStorage.MimeType("video.mp4"));
        Assert.Equal("video/webm", FileStorage.MimeType("video.webm"));
        Assert.Equal("video/ogg", FileStorage.MimeType("video.ogv"));
    }

    [Theory]
    [InlineData("image.png", "image/png")]
    [InlineData("image.jpg", "image/jpeg")]
    [InlineData("image.jpeg", "image/jpeg")]
    [InlineData("image.gif", "image/gif")]
    [InlineData("image.webp", "image/webp")]
    [InlineData("image.bmp", "image/bmp")]
    public void ImageMimeMappingsAreCanonical(string fileName, string expected)
    {
        Assert.Equal(expected, FileStorage.MimeType(fileName));
    }
}
