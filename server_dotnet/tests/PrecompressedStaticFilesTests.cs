using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class PrecompressedStaticFilesTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("yf_precompressed_").FullName;

    [Fact]
    public async Task ChoosesHighestQualityAvailableEncodingAndKeepsOriginalMetadata()
    {
        var source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("export const value = 'compressible';\n", 200)));
        var representations = WriteAsset("assets/app.js", source);
        var context = Request("/assets/app.js", "gzip;q=0.4, br;q=0.8, identity;q=0.1");
        context.Response.Headers.Vary = "Origin";

        var nextCalled = await InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("br", context.Response.Headers.ContentEncoding);
        Assert.Equal("text/javascript", context.Response.ContentType);
        Assert.Equal(representations.Brotli.Length, context.Response.ContentLength);
        Assert.Equal("Origin, Accept-Encoding", context.Response.Headers.Vary);
        Assert.Equal(representations.Brotli, ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("public,max-age=31536000,immutable", context.Response.Headers.CacheControl);

        var gzip = Request("/assets/app.js", "gzip;q=1, br;q=0.5, identity;q=0");
        Assert.False(await InvokeAsync(gzip));
        Assert.Equal("gzip", gzip.Response.Headers.ContentEncoding);
        Assert.Equal(representations.Gzip, ((MemoryStream)gzip.Response.Body).ToArray());
    }

    [Fact]
    public async Task HonorsWildcardExplicitIdentityAndQualityZero()
    {
        WriteAsset("data/catalog.json", Encoding.UTF8.GetBytes(new string('a', 4096)));
        var wildcard = Request("/data/catalog.json", "gzip;q=0.7, *;q=0.8, identity;q=0");
        Assert.False(await InvokeAsync(wildcard));
        Assert.Equal("br", wildcard.Response.Headers.ContentEncoding);

        var identityWins = Request("/data/catalog.json", "br;q=0.2");
        Assert.True(await InvokeAsync(identityWins));
        Assert.True(StringValues.IsNullOrEmpty(identityWins.Response.Headers.ContentEncoding));

        var unacceptable = Request("/data/catalog.json", "br;q=0, gzip;q=0, *;q=0");
        Assert.False(await InvokeAsync(unacceptable));
        Assert.Equal(StatusCodes.Status406NotAcceptable, unacceptable.Response.StatusCode);
        Assert.Contains("Accept-Encoding", unacceptable.Response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task HeadAndConditionalRequestsUseTheSelectedRepresentationEtag()
    {
        WriteAsset("index.html", Encoding.UTF8.GetBytes("<!doctype html>" + new string('x', 4096)));
        var initial = Request("/index.html", "br, identity;q=0");
        Assert.False(await InvokeAsync(initial));
        var etag = initial.Response.Headers.ETag.ToString();
        Assert.NotEmpty(etag);
        Assert.Equal("no-cache", initial.Response.Headers.CacheControl);

        var head = Request("/index.html", "br, identity;q=0", HttpMethods.Head);
        Assert.False(await InvokeAsync(head));
        Assert.True(head.Response.ContentLength > 0);
        Assert.Empty(((MemoryStream)head.Response.Body).ToArray());

        var conditional = Request("/index.html", "br, identity;q=0");
        conditional.Request.Headers.IfNoneMatch = "W/" + etag;
        Assert.False(await InvokeAsync(conditional));
        Assert.Equal(StatusCodes.Status304NotModified, conditional.Response.StatusCode);
        Assert.Null(conditional.Response.ContentLength);
        Assert.Empty(((MemoryStream)conditional.Response.Body).ToArray());

        var failedPrecondition = Request("/index.html", "br, identity;q=0");
        failedPrecondition.Request.Headers.IfMatch = "\"different-representation\"";
        Assert.False(await InvokeAsync(failedPrecondition));
        Assert.Equal(StatusCodes.Status412PreconditionFailed, failedPrecondition.Response.StatusCode);
        Assert.Empty(((MemoryStream)failedPrecondition.Response.Body).ToArray());
    }

    [Fact]
    public async Task RangeRequestsFallBackToIdentityOrReturn406WithoutContentRange()
    {
        WriteAsset("assets/app.css", Encoding.UTF8.GetBytes(new string('z', 4096)));
        var fallback = Request("/assets/app.css", "br");
        fallback.Request.Headers.Range = "bytes=0-10";
        Assert.True(await InvokeAsync(fallback));
        Assert.True(StringValues.IsNullOrEmpty(fallback.Response.Headers.ContentEncoding));
        Assert.True(StringValues.IsNullOrEmpty(fallback.Response.Headers.ContentRange));
        Assert.Contains("Accept-Encoding", fallback.Response.Headers.Vary.ToString());

        var forbiddenIdentity = Request("/assets/app.css", "br, identity;q=0");
        forbiddenIdentity.Request.Headers.Range = "bytes=0-10";
        Assert.False(await InvokeAsync(forbiddenIdentity));
        Assert.Equal(StatusCodes.Status406NotAcceptable, forbiddenIdentity.Response.StatusCode);
        Assert.True(StringValues.IsNullOrEmpty(forbiddenIdentity.Response.Headers.ContentRange));
    }

    [Fact]
    public async Task OnlySafePublicStaticPathsAreHandled()
    {
        WriteAsset("assets/app.js", Encoding.UTF8.GetBytes(new string('x', 4096)));
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{}");

        foreach (var path in new[] { "/api/assets/app.js", "/health" })
            Assert.True(await InvokeAsync(Request(path, "br")));

        foreach (var path in new[] { "/appsettings.json", "/../secret.js", "/..%2fsecret.js", "/.precompressed-assets.json",
                     "/privateuploads/file.js", "/private%75ploads/file.js" })
        {
            var denied = Request(path, "br");
            Assert.False(await InvokeAsync(denied));
            Assert.Equal(StatusCodes.Status404NotFound, denied.Response.StatusCode);
        }
    }

    [Fact]
    public async Task DefaultDocumentIsPrecompressedBeforeFallbackRouting()
    {
        var source = Encoding.UTF8.GetBytes("<!doctype html>" + new string('r', 4096));
        var representations = WriteAsset("index.html", source);
        var environment = new TestEnvironment(root);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddRouting();
        var diagnostics = new DiagnosticListener("Yf.Api.Tests.PrecompressedStaticFiles");
        serviceCollection.AddSingleton(diagnostics);
        serviceCollection.AddSingleton<DiagnosticSource>(diagnostics);
        serviceCollection.AddSingleton<IWebHostEnvironment>(environment);
        using var services = serviceCollection.BuildServiceProvider();
        var application = new ApplicationBuilder(services);

        ApiApplication.UsePublicStaticFilesBeforeRouting(application);
        var fallbackCalled = false;
        application.UseEndpoints(endpoints => endpoints.MapFallback(async context =>
        {
            fallbackCalled = true;
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(source);
        }));

        var context = Request("/", "br, identity;q=0");
        context.RequestServices = services;
        await application.Build()(context);

        Assert.False(fallbackCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("br", context.Response.Headers.ContentEncoding);
        Assert.Equal("text/html", context.Response.ContentType);
        Assert.Equal(representations.Brotli, ((MemoryStream)context.Response.Body).ToArray());
    }

    private async Task<bool> InvokeAsync(DefaultHttpContext context)
    {
        var nextCalled = false;
        var middleware = new PrecompressedStaticFileMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new TestEnvironment(root));
        await middleware.InvokeAsync(context);
        return nextCalled;
    }

    private static DefaultHttpContext Request(string path, string acceptEncoding, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private (byte[] Brotli, byte[] Gzip) WriteAsset(string relativePath, byte[] source)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, source);
        var timestamp = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fullPath, timestamp);
        var brotli = Compress(source, true);
        var gzip = Compress(source, false);
        File.WriteAllBytes(fullPath + ".br", brotli);
        File.WriteAllBytes(fullPath + ".gz", gzip);
        File.SetLastWriteTimeUtc(fullPath + ".br", timestamp.AddSeconds(1));
        File.SetLastWriteTimeUtc(fullPath + ".gz", timestamp.AddSeconds(1));
        return (brotli, gzip);
    }

    private static byte[] Compress(byte[] source, bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream compressor = brotli
            ? new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(source);
        return output.ToArray();
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class TestEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Yf.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
        public string WebRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
    }
}
