using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Optional structured file logging (App:LogDirectory). When configured, Serilog is added as an
/// extra logging provider — the existing providers and the Logging:LogLevel filters keep working —
/// and one compact-JSON line per HTTP request is written with its trace id. Request paths never
/// include the query string, so SignalR's access_token query parameter cannot reach the log.
/// </summary>
internal static class FileLogging
{
    /// <summary>Daily files are kept for about a month.</summary>
    internal const int RetainedFileCountLimit = 31;
    /// <summary>A file that reaches this size rolls to a numbered sibling (counted in the retention limit).</summary>
    internal const long FileSizeLimitBytes = 100L * 1024 * 1024;
    internal const string FileNamePattern = "yf-api-.clef";
    private const string RequestTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

    internal static Serilog.Core.Logger? Configure(WebApplicationBuilder builder, AppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LogDirectory)) return null;
        Directory.CreateDirectory(options.LogDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            // Hosting diagnostics log the full URL including the query string (SignalR access_token);
            // the request log below replaces them with a path-only line.
            .Filter.ByExcluding(Serilog.Filters.Matching.FromSource("Microsoft.AspNetCore.Hosting.Diagnostics"))
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(options.LogDirectory, FileNamePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
        // dispose: true flushes and closes the file when the host's logger factory is disposed.
        builder.Logging.AddSerilog(logger, dispose: true);
        return logger;
    }

    internal static void UseRequestLogging(WebApplication app, Serilog.ILogger? logger)
    {
        if (logger is null) return;
        var requests = logger.ForContext("SourceContext", "Yf.Api.RequestLog");
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try { await next(context); }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                var status = failure is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
                var level = failure is not null || status >= 500 ? LogEventLevel.Error : LogEventLevel.Information;
                requests
                    .ForContext("TraceId", Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier)
                    .ForContext("RequestId", context.TraceIdentifier)
                    // Path only: the query string may carry access_token (SignalR) or other secrets.
                    .Write(level, failure, RequestTemplate, context.Request.Method,
                        context.Request.PathBase.Add(context.Request.Path).ToString(), status,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });
    }
}
