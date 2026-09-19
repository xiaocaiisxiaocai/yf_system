using System.Net;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;

// This executable is never published by publish-iis.ps1 and may only listen on loopback.
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(urls) ||
    urls.Split(';').Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) ||
        !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)))
    throw new InvalidOperationException("Test host requires explicit loopback-only URLs.");
var app = await ApiApplication.BuildAsync(args, builder => builder.WebHost.UseUrls(urls));
if (app is not null)
{
    // Browser runs disable every background worker (App:WorkerEnabled=false) so mail and file GC
    // stay deterministic. The OEM flow still needs its fake scan and promotion to advance, so an
    // explicit opt-in drives only those two jobs, quickly, from this test-only executable.
    Task? oemJobs = null;
    if (Environment.GetEnvironmentVariable("YF_TESTHOST_OEM_SCAN") == "1")
    {
        var jobs = app.Services.GetServices<IOemBackgroundJob>().Where(job => job.Name is "scan" or "promote").ToArray();
        if (jobs.Length != 2) throw new InvalidOperationException("OEM scan/promote jobs are not registered.");
        var stopping = app.Lifetime.ApplicationStopping;
        oemJobs = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                foreach (var job in jobs)
                {
                    try { await job.RunOnceAsync(stopping); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                    catch (Exception error) { app.Logger.LogWarning("Test OEM job {Job} failed: {Error}", job.Name, error.GetType().Name); }
                }
                try { await Task.Delay(500, stopping); } catch (OperationCanceledException) { return; }
            }
        });
    }
    await app.RunAsync();
    if (oemJobs is not null) await oemJobs;
}
