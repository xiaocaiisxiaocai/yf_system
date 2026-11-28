using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class ApiRequestPolicyTests
{
    [Theory]
    [InlineData("PUT", "/api/v1/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0", true)]
    [InlineData("PUT", "/API/V1/Uploads/01234567-89ab-cdef-0123-456789abcdef/Chunks/0", true)]
    [InlineData("POST", "/api/v1/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0", false)]
    [InlineData("PUT", "/api/v1/projects/chunks/1", false)]
    [InlineData("PUT", "/api/v1/uploads/invalid/chunks/1", false)]
    [InlineData("PUT", "/api/v1/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/-1", false)]
    [InlineData("PUT", "/api/v1/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0/extra", false)]
    [InlineData("PUT", "/api/v1/oem/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0", true)]
    [InlineData("PUT", "/API/V1/OEM/UPLOADS/01234567-89ab-cdef-0123-456789abcdef/CHUNKS/2/", true)]
    [InlineData("POST", "/api/v1/oem/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0", false)]
    [InlineData("PUT", "/api/v1/oem/uploads/invalid/chunks/0", false)]
    [InlineData("PUT", "/api/v1/oem/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/-1", false)]
    [InlineData("PUT", "/api/v1/oem/uploads/01234567-89ab-cdef-0123-456789abcdef/chunks/0/extra", false)]
    public void OnlyAnActualChunkRouteReceivesTheLargeBodyLimit(string method, string path, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        Assert.Equal(expected, ApiRequestPolicy.IsChunkUpload(request));
    }

    [Theory]
    [InlineData("/api/v1/files/1/content", true)]
    [InlineData("/API/V1/Files/1/Content", true)]
    [InlineData("/API/V1/Files/1/Content/", true)]
    [InlineData("/api/v1/files/1/MEDIA", true)]
    [InlineData("/api/v1/files/1/media-session", false)]
    [InlineData("/api/v1/files/1/content/extra", false)]
    [InlineData("/api/v1/oem/files/1/content", true)]
    [InlineData("/API/V1/OEM/FILES/1/DOWNLOAD/", true)]
    [InlineData("/api/v1/oem/files/1/download-status", false)]
    [InlineData("/api/v1/oem/files/1/download/extra", false)]
    [InlineData("/api/v1/oem/files/not-an-id/content", false)]
    [InlineData("/api/v1/messages/1/images/2", true)]
    [InlineData("/API/V1/Messages/1/Images/2/", true)]
    [InlineData("/api/v1/messages/not-an-id/images/2", false)]
    [InlineData("/api/v1/messages/1/images/not-an-id", false)]
    [InlineData("/api/v1/messages/1/images/2/extra", false)]
    [InlineData("/api/v1/messages/1/reads", false)]
    [InlineData("/api/v1/announcements/12/assets/34", true)]
    [InlineData("/api/v1/announcements/manage/12/assets/34", true)]
    [InlineData("/API/V1/OEM/Announcements/12/Assets/34/", true)]
    [InlineData("/api/v1/announcements/12/assets", false)]
    [InlineData("/api/v1/oem/announcements/12/assets/missing", false)]
    [InlineData("/api/v1/announcements/manage/12/assets/34/other", false)]
    public void ContentPolicyMatchesCaseInsensitiveRoutes(string path, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Path = path;
        Assert.Equal(expected, ApiRequestPolicy.IsFileContent(request));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    public void MessageImageContentPolicyOnlyAppliesToReadMethods(string method, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = "/api/v1/messages/10/images/20";
        Assert.Equal(expected, ApiRequestPolicy.IsFileContent(request));
    }
}
