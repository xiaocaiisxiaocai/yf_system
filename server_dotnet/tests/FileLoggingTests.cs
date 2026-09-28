using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class FileLoggingTests
{
    [Fact(Timeout = 60_000)]
    public async Task RequestLogIsCompactJsonWithTraceIdAndNeverIncludesTheQueryString()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "yf_logs_" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var logger = FileLogging.Configure(builder, new AppOptions { LogDirectory = directory });
            Assert.NotNull(logger);
            string? address;
            await using (var app = builder.Build())
            {
                FileLogging.UseRequestLogging(app, logger);
                app.MapGet("/probe", () => "ok");
                await app.StartAsync(ct);
                address = app.Urls.Single();
                using var client = new HttpClient();
                Assert.Equal("ok", await client.GetStringAsync(address + "/probe?access_token=SECRET-TOKEN-VALUE", ct));
                await app.StopAsync(ct);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "yf-api-*.clef"));
            var lines = await File.ReadAllLinesAsync(file, ct);
            var request = Assert.Single(lines, line => line.Contains("\"SourceContext\":\"Yf.Api.RequestLog\"", StringComparison.Ordinal));
            Assert.Contains("\"RequestPath\":\"/probe\"", request);
            Assert.Contains("\"StatusCode\":200", request);
            Assert.Contains("\"TraceId\":", request);
            Assert.DoesNotContain(lines, line => line.Contains("SECRET-TOKEN-VALUE", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("access_token", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LogDirectoryMustBeAbsoluteAndOutsideTheApplication()
    {
        var app = Path.Combine(Path.GetTempPath(), "yf_log_location", "app");
        var storage = Path.Combine(Path.GetTempPath(), "yf_log_location", "storage");
        var options = new AppOptions { StorageRoot = storage, LogDirectory = "logs" };
        Assert.Throws<InvalidOperationException>(() => options.ValidateStorageLocation(app));
        options.LogDirectory = Path.Combine(app, "logs");
        Assert.Throws<InvalidOperationException>(() => options.ValidateStorageLocation(app));
        options.LogDirectory = Path.Combine(Path.GetTempPath(), "yf_log_location", "logs");
        options.ValidateStorageLocation(app);
        options.LogDirectory = "";
        options.ValidateStorageLocation(app);
    }
}
