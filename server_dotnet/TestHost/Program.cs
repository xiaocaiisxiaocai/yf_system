using System.Net;
using Yf.Api.Infrastructure;

// This executable is never published by publish-iis.ps1 and may only listen on loopback.
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(urls) ||
    urls.Split(';').Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) ||
        !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)))
    throw new InvalidOperationException("Test host requires explicit loopback-only URLs.");
var app = await ApiApplication.BuildAsync(args, builder => builder.WebHost.UseUrls(urls));
if (app is not null)
{
    await app.RunAsync();
}
