using System.Net;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.SystemManagement;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

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
    // Only the disposable browser harness enables this. No SMTP traffic can leave its owned sink.
    var mailControlKey = Environment.GetEnvironmentVariable("YF_TESTHOST_MAIL_CONTROL");
    if (!string.IsNullOrEmpty(mailControlKey))
    {
        if (app.Services.GetRequiredService<AppOptions>().WorkerEnabled)
            throw new InvalidOperationException("Browser mail control requires regular workers to be disabled.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("YF_TESTHOST_SMTP_PORT"), out var smtpPort) || smtpPort is < 1 or > 65535)
            throw new InvalidOperationException("Browser mail control requires an owned SMTP capture port.");
        async Task CheckCaptureAsync(CancellationToken ct)
        {
            var smtp = await app.Services.GetRequiredService<SmtpSettingsService>().GetAsync(ct);
            if (smtp.Port != smtpPort || !(smtp.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(smtp.Host, out var address) && IPAddress.IsLoopback(address)))
                throw ApiException.BadRequest("Browser mail acceptance may only send to its owned loopback SMTP capture.");
            await using var database = await app.Services.GetRequiredService<IDbContextFactory<YfDbContext>>().CreateDbContextAsync(ct);
            var recipients = await database.EmailOutbox.Where(item => item.Status == "PENDING" || item.Status == "SENDING")
                .Select(item => item.RecipientEmail).Distinct().ToListAsync(ct);
            if (recipients.Any(email => !email.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)))
                throw ApiException.BadRequest("Browser mail acceptance only permits synthetic .invalid recipients.");
        }
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/api/v1/admin/system/mail-settings/test")
            {
                await CheckCaptureAsync(context.RequestAborted);
                context.Request.EnableBuffering();
                using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var recipient = body.RootElement.TryGetProperty("recipient", out var value) ? value.GetString() : null;
                if (recipient is null || !recipient.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
                    throw ApiException.BadRequest("Browser mail acceptance only permits synthetic .invalid recipients.");
                context.Request.Body.Position = 0;
            }
            await next(context);
        });
        app.MapPost("/__test/mail/drain", async (HttpContext context, MailService mail, IDbContextFactory<YfDbContext> factory, CancellationToken ct) =>
        {
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Test-Key"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(mailControlKey))) return Results.NotFound();
            await CheckCaptureAsync(ct);
            if (context.Request.Query["advance"] == "true")
            {
                await using var database = await factory.CreateDbContextAsync(ct);
                await database.EmailOutbox.Where(item => item.Status == "PENDING")
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextAttemptAt, (DateTime?)null), ct);
            }
            var taken = await mail.FlushAsync(ct);
            return Results.Ok(new { taken });
        });
        app.MapGet("/__test/mail/outbox", async (HttpContext context, IDbContextFactory<YfDbContext> factory, CancellationToken ct) =>
        {
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Test-Key"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(mailControlKey))) return Results.NotFound();
            await using var database = await factory.CreateDbContextAsync(ct);
            var rows = await database.EmailOutbox.OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.EventType,
                    item.RecipientEmail,
                    item.Status,
                    item.RetryCount,
                    item.NextAttemptAt,
                    item.OemTransferId,
                    item.RecipientRealm,
                })
                .ToListAsync(ct);
            if (rows.Any(item => !item.RecipientEmail.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest();
            return Results.Ok(rows);
        });
    }
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
