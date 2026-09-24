using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Yf.Api.Infrastructure;

public static class PrecompressedStaticFileExtensions
{
    public static IApplicationBuilder UsePrecompressedStaticFiles(this IApplicationBuilder app) =>
        app.UseMiddleware<PrecompressedStaticFileMiddleware>();
}

internal sealed class PrecompressedStaticFileMiddleware
{
    private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".eot", ".html", ".js", ".json", ".map", ".mjs", ".otf", ".pfb", ".svg",
        ".ttf", ".txt", ".wasm", ".webmanifest", ".woff", ".woff2", ".xml",
    };
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    private readonly RequestDelegate next;
    private readonly IFileProvider files;

    public PrecompressedStaticFileMiddleware(RequestDelegate next, IWebHostEnvironment environment)
    {
        this.next = next;
        files = PublicStaticFileProvider.Create(environment);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            || context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }
        if (context.Request.Path.StartsWithSegments("/privateuploads"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var requestPath = context.Request.Path.Value;
        if (!PublicStaticFileProvider.IsSafePath(requestPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (string.IsNullOrEmpty(requestPath) || !CompressibleExtensions.Contains(Path.GetExtension(requestPath)))
        {
            await next(context);
            return;
        }

        var source = files.GetFileInfo(requestPath);
        if (!source.Exists)
        {
            await next(context);
            return;
        }

        var br = Companion(requestPath, source, ".br");
        var gzip = Companion(requestPath, source, ".gz");
        var accepted = AcceptedEncodings.Parse(context.Request.Headers.AcceptEncoding);
        var hasRange = !StringValues.IsNullOrEmpty(context.Request.Headers.Range);
        if (hasRange)
        {
            AppendVary(context.Response.Headers, HeaderNames.AcceptEncoding);
            if (accepted.IdentityQuality <= 0)
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }
            await next(context);
            return;
        }

        var selected = accepted.Select(br is not null, gzip is not null);
        if (br is not null || gzip is not null) AppendVary(context.Response.Headers, HeaderNames.AcceptEncoding);
        if (selected is null)
        {
            AppendVary(context.Response.Headers, HeaderNames.AcceptEncoding);
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            return;
        }
        if (selected == "identity")
        {
            await next(context);
            return;
        }

        var representation = selected == "br" ? br! : gzip!;
        await SendAsync(context, source, representation, selected);
    }

    private async Task SendAsync(HttpContext context, IFileInfo source, IFileInfo representation, string encoding)
    {
        var lastModified = source.LastModified.ToUniversalTime();
        lastModified = lastModified.AddTicks(-(lastModified.Ticks % TimeSpan.TicksPerSecond));
        var etag = CreateEtag(source, representation, encoding);
        var response = context.Response;
        response.Headers.ETag = etag;
        response.Headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);
        response.Headers.ContentEncoding = encoding;
        response.Headers.CacheControl = CacheControl(context.Request.Path);

        var precondition = EvaluatePreconditions(context.Request, etag, lastModified);
        if (precondition is StatusCodes.Status304NotModified or StatusCodes.Status412PreconditionFailed)
        {
            response.StatusCode = precondition;
            response.ContentLength = null;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentTypes.TryGetContentType(source.Name, out var contentType)
            ? contentType
            : "application/octet-stream";
        response.ContentLength = representation.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;

        await using var stream = representation.CreateReadStream();
        await stream.CopyToAsync(response.Body, context.RequestAborted);
    }

    private IFileInfo? Companion(string requestPath, IFileInfo source, string suffix)
    {
        var companion = files.GetFileInfo(requestPath + suffix);
        return companion.Exists && companion.LastModified >= source.LastModified
            ? companion
            : null;
    }

    private static string CacheControl(PathString path) =>
        path.Value?.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) == true
            || path.Value?.Equals("/index.html", StringComparison.OrdinalIgnoreCase) == true
            ? "no-cache"
            : path.StartsWithSegments("/assets")
                ? "public,max-age=31536000,immutable"
                : "public,max-age=3600";

    private static string CreateEtag(IFileInfo source, IFileInfo representation, string encoding)
    {
        var metadata = $"{source.Length}:{source.LastModified.UtcDateTime.Ticks}:{representation.Length}:{representation.LastModified.UtcDateTime.Ticks}:{encoding}";
        return $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant()}\"";
    }

    private static int EvaluatePreconditions(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        var ifMatch = request.Headers.IfMatch;
        if (!StringValues.IsNullOrEmpty(ifMatch) && !StrongEtagMatches(ifMatch, etag))
            return StatusCodes.Status412PreconditionFailed;
        if (StringValues.IsNullOrEmpty(ifMatch)
            && TryReadHttpDate(request.Headers.IfUnmodifiedSince, out var unmodifiedSince)
            && lastModified > unmodifiedSince)
            return StatusCodes.Status412PreconditionFailed;

        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (!StringValues.IsNullOrEmpty(ifNoneMatch))
            return WeakEtagMatches(ifNoneMatch, etag) ? StatusCodes.Status304NotModified : StatusCodes.Status200OK;
        if (TryReadHttpDate(request.Headers.IfModifiedSince, out var modifiedSince) && lastModified <= modifiedSince)
            return StatusCodes.Status304NotModified;
        return StatusCodes.Status200OK;
    }

    private static bool StrongEtagMatches(StringValues values, string etag) =>
        HeaderTokens(values).Any(value => value == "*" || (!value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) && value == etag));

    private static bool WeakEtagMatches(StringValues values, string etag)
    {
        var normalized = etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag[2..] : etag;
        return HeaderTokens(values).Any(value => value == "*"
            || (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..] : value) == normalized);
    }

    private static IEnumerable<string> HeaderTokens(StringValues values) =>
        values.SelectMany(value => (value ?? "").Split(',')).Select(value => value.Trim()).Where(value => value.Length > 0);

    private static bool TryReadHttpDate(StringValues values, out DateTimeOffset date)
    {
        date = default;
        return !StringValues.IsNullOrEmpty(values)
            && DateTimeOffset.TryParse(values.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }

    private static void AppendVary(IHeaderDictionary headers, string value)
    {
        var current = headers.Vary.SelectMany(item => (item ?? "").Split(','))
            .Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        if (!current.Contains(value, StringComparer.OrdinalIgnoreCase)) current.Add(value);
        headers.Vary = string.Join(", ", current);
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".mjs"] = "text/javascript";
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        return provider;
    }

    private sealed record AcceptedEncodings(bool HeaderPresent, IReadOnlyDictionary<string, double> Qualities)
    {
        public double IdentityQuality => Quality("identity");

        public static AcceptedEncodings Parse(StringValues header)
        {
            if (StringValues.IsNullOrEmpty(header)) return new(false, new Dictionary<string, double>());
            var qualities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in header.SelectMany(value => (value ?? "").Split(',')))
            {
                var parts = item.Split(';', StringSplitOptions.TrimEntries);
                var coding = parts[0].Trim();
                if (coding.Length == 0) continue;
                var quality = 1d;
                foreach (var parameter in parts.Skip(1))
                {
                    var pair = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length == 2 && pair[0].Equals("q", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!double.TryParse(pair[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality)
                            || quality is < 0 or > 1) quality = 0;
                    }
                }
                qualities[coding] = qualities.TryGetValue(coding, out var existing) ? Math.Max(existing, quality) : quality;
            }
            return new(true, qualities);
        }

        public string? Select(bool hasBrotli, bool hasGzip)
        {
            if (!HeaderPresent) return "identity";
            var candidates = new List<(string Encoding, double Quality, int Preference)>
            {
                ("identity", IdentityQuality, 1),
            };
            if (hasGzip) candidates.Add(("gzip", Quality("gzip"), 2));
            if (hasBrotli) candidates.Add(("br", Quality("br"), 3));
            var selected = candidates.Where(item => item.Quality > 0)
                .OrderByDescending(item => item.Quality).ThenByDescending(item => item.Preference).FirstOrDefault();
            return selected.Quality > 0 ? selected.Encoding : null;
        }

        private double Quality(string coding)
        {
            if (!HeaderPresent) return coding == "identity" ? 1 : 0;
            if (Qualities.TryGetValue(coding, out var exact)) return exact;
            if (coding == "identity")
                return Qualities.TryGetValue("*", out var wildcard) && wildcard == 0 ? 0 : 1;
            return Qualities.TryGetValue("*", out var fallback) ? fallback : 0;
        }
    }
}
