using System.Net;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;

// This executable is never published by publish-iis.ps1 and may only listen on loopback.
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(urls) || urls.Split(';').Any(x => !IsLoopbackUrl(x)))
    throw new InvalidOperationException("Test host requires explicit loopback-only URLs.");
var app = await ApiApplication.BuildAsync(args, builder =>
{
    // Kestrel:Endpoints (e.g. Kestrel__Endpoints__Http__Url) overrides UseUrls, and urls / *_ports
    // can arrive through any configuration source (environment, --urls, appsettings). Reject every
    // listener setting that is not loopback so the final bindings can only be the checked URLs.
    var configuration = builder.Configuration;
    foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        if (!IsLoopbackUrl(endpoint["Url"]))
            throw new InvalidOperationException("Test host requires loopback-only Kestrel endpoints.");
    if (configuration["urls"] is { Length: > 0 } configured && configured.Split(';').Any(x => !IsLoopbackUrl(x)))
        throw new InvalidOperationException("Test host requires loopback-only URLs.");
    if (!string.IsNullOrEmpty(configuration["http_ports"]) || !string.IsNullOrEmpty(configuration["https_ports"]))
        throw new InvalidOperationException("Test host does not accept HTTP_PORTS/HTTPS_PORTS; they bind every interface.");
    builder.WebHost.UseUrls(urls);
});
if (app is not null)
{
    // Browser/API tests disable the regular workers for deterministic behavior. The OEM
    // flow still needs file validation and promotion to advance, so this test-only opt-in
    // drives only those two jobs. This entrypoint is never published.
    Task? oemJobs = null;
    if (Environment.GetEnvironmentVariable("YF_TESTHOST_OEM_PROCESS") == "1")
    {
        var jobs = app.Services.GetServices<IOemBackgroundJob>()
            .Where(job => job.Name is "validate" or "promote")
            .ToArray();
        if (jobs.Length != 2)
            throw new InvalidOperationException("OEM validate/promote jobs are not registered.");
        var stopping = app.Lifetime.ApplicationStopping;
        oemJobs = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                foreach (var job in jobs)
                {
                    try { await job.RunOnceAsync(stopping); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        app.Logger.LogWarning("Test OEM job {Job} failed: {Error}", job.Name, error.GetType().Name);
                    }
                }
                try { await Task.Delay(500, stopping); }
                catch (OperationCanceledException) { return; }
            }
        });
    }
    await app.RunAsync();
    if (oemJobs is not null) await oemJobs;
}

static bool IsLoopbackUrl(string? value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri)
    && IPAddress.TryParse(uri.Host, out var ip)
    && IPAddress.IsLoopback(ip);
