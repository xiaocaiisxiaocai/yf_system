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
    public void ContentPolicyMatchesCaseInsensitiveRoutes(string path, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Path = path;
        Assert.Equal(expected, ApiRequestPolicy.IsFileContent(request));
    }
}
