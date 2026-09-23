using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class ApiErrorMiddlewareTests
{
    [Fact]
    public async Task UnexpectedErrorsReturnARequestIdAndLogTheFullException()
    {
        var logger = new RecordingLogger();
        var middleware = new ApiErrorMiddleware(_ => throw new InvalidOperationException("secret detail"), logger);
        var (status, body, context) = await InvokeAsync(middleware, "/api/v1/projects?access_token=hidden");

        Assert.Equal(500, status);
        Assert.Equal(50000, body.GetProperty("code").GetInt32());
        Assert.Equal(context.TraceIdentifier, body.GetProperty("requestId").GetString());
        Assert.DoesNotContain("secret detail", body.ToString());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Contains(context.TraceIdentifier, entry.Message);
        Assert.DoesNotContain("access_token", entry.Message);
    }

    [Fact]
    public async Task ExpectedErrorsKeepTheCompactShapeAndAreNotLogged()
    {
        var logger = new RecordingLogger();
        var middleware = new ApiErrorMiddleware(_ => throw ApiException.BadRequest("参数错误"), logger);
        var (status, body, _) = await InvokeAsync(middleware, "/api/v1/projects");

        Assert.Equal(400, status);
        Assert.Equal("参数错误", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("requestId", out _));
        Assert.Empty(logger.Entries);
    }

    private static async Task<(int Status, JsonElement Body, HttpContext Context)> InvokeAsync(
        ApiErrorMiddleware middleware, string pathAndQuery)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N") };
        var split = pathAndQuery.Split('?', 2);
        context.Request.Method = "GET";
        context.Request.Path = split[0];
        if (split.Length == 2) context.Request.QueryString = new QueryString("?" + split[1]);
        using var output = new MemoryStream();
        context.Response.Body = output;
        await middleware.InvokeAsync(context);
        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, document.RootElement.Clone(), context);
    }

    private sealed class RecordingLogger : ILogger<ApiErrorMiddleware>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
