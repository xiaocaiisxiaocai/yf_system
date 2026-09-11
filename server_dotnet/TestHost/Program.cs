using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

// This executable is never published by publish-iis.ps1. The production API has
// no answer route or config switch for exposing CAPTCHA answers.
var key = Environment.GetEnvironmentVariable("YF_TEST_HOST_KEY");
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(key) || key.Length < 32 || string.IsNullOrEmpty(urls) ||
    urls.Split(';').Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) ||
        !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)))
    throw new InvalidOperationException("Test host requires a random key and explicit loopback-only URLs.");
var observer = new CaptchaObserver();
var app = await ApiApplication.BuildAsync(args, builder =>
{
    builder.WebHost.UseUrls(urls);
    builder.Services.AddSingleton<ICaptchaChallengeObserver>(observer);
});
if (app is not null)
{
    app.MapGet("/__test/captcha-answer/{id}", (string id, HttpContext context) =>
    {
        var supplied = context.Request.Headers["X-Test-Host-Key"].ToString();
        if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(supplied)))
            return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        return observer.Take(id) is { } answer ? Results.Json(new { answer }) : Results.NotFound();
    });
    await app.RunAsync();
}

sealed class CaptchaObserver : ICaptchaChallengeObserver
{
    private readonly ConcurrentDictionary<string, (string Answer, DateTime Expires)> answers = new();
    public void OnIssued(string captchaId, string answer)
    {
        foreach (var item in answers.Where(x => x.Value.Expires <= DateTime.UtcNow)) answers.TryRemove(item.Key, out _);
        answers[captchaId] = (answer, DateTime.UtcNow.AddMinutes(5));
    }
    public string? Take(string id) => answers.TryRemove(id, out var entry) && entry.Expires > DateTime.UtcNow ? entry.Answer : null;
}
