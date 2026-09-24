using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class ApiErrorMiddlewareTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public async Task DiskFullErrorsReturnAnActionableStorageResponse(int hresult)
    {
        var middleware = new ApiErrorMiddleware(_ => throw new IOException("private path", hresult), new RecordingLogger());
        var (status, body, _) = await InvokeAsync(middleware, "/api/v1/uploads/session/chunks/0");
        Assert.Equal(507, status);
        Assert.Equal(50701, body.GetProperty("code").GetInt32());
        Assert.Equal("存储空间不足，请联系管理员", body.GetProperty("message").GetString());
        Assert.DoesNotContain("private path", body.ToString());
    }

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

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class ApiErrorMiddlewareLockTests
{
    [Fact]
    public async Task LockWaitTimeoutsAreReportedAsRetryableConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await using var holder = await database.Database.OpenAsync(ct);
        await using var holderTx = await AppDb.BeginTransactionAsync(holder, ct);
        await AccessService.LockManagementAsync(holder, holderTx, ct);

        await using var waiter = await database.Database.OpenAsync(ct);
        await using (var timeout = waiter.CreateCommand())
        {
            timeout.CommandText = "SET SESSION innodb_lock_wait_timeout=1";
            await timeout.ExecuteNonQueryAsync(ct);
        }
        await using var waiterTx = await AppDb.BeginTransactionAsync(waiter, ct);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AccessService.LockBusinessAsync(waiter, waiterTx, ct));

        var logger = new NullRecordingLogger();
        var middleware = new ApiErrorMiddleware(_ => throw error, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/projects/1";
        using var output = new MemoryStream();
        context.Response.Body = output;
        await middleware.InvokeAsync(context);
        output.Position = 0;
        using var body = await JsonDocument.ParseAsync(output, cancellationToken: ct);

        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal(40902, body.RootElement.GetProperty("code").GetInt32());
        Assert.False(logger.Logged);
    }

    private sealed class NullRecordingLogger : ILogger<ApiErrorMiddleware>
    {
        public bool Logged { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Logged = true;
    }
}
