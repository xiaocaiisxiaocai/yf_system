using MySqlConnector;
using System.Net;
using System.Text.Json;

namespace Yf.Api.Infrastructure;

public static class ClientIp
{
    public static string Resolve(HttpContext context, AppOptions options)
    {
        var remote = context.Connection.RemoteIpAddress;
        var peer = remote?.ToString() ?? "unknown";
        if (!options.TrustLoopbackProxy || remote is null || !IPAddress.IsLoopback(remote)) return peer;
        var raw = context.Request.Headers["X-Forwarded-For"].ToString();
        if (IPAddress.TryParse(raw, out var forwarded) && forwarded.ToString() == raw) return raw;
        return peer;
    }
}

public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var error = exception switch
            {
                ApiException api => api,
                JsonException => ApiException.BadRequest("请求格式不正确"),
                BadHttpRequestException bad => new ApiException(bad.StatusCode, 40001, "请求格式不正确"),
                MySqlException sql when sql.Number == 1062 => ApiException.Conflict("数据已存在，请刷新后重试"),
                _ => new ApiException(500, 50000, "服务器内部错误")
            };
            if (error.Status == 500) logger.LogError("Request {TraceId} failed: {ErrorType}; database code {DatabaseCode}; stack {StackTrace}",
                context.TraceIdentifier, exception.GetType().Name, (exception as MySqlException)?.Number, exception.StackTrace);
            context.Response.StatusCode = error.Status;
            await context.Response.WriteAsJsonAsync(new { code = error.Code, message = error.Message }, context.RequestAborted);
        }
    }
}

public static class QueryValues
{
    public static (ulong Page, uint Size, ulong Offset) Page(HttpRequest request)
    {
        var p = ulong.TryParse(request.Query["page"], out var parsed) ? parsed : 1;
        var s = uint.TryParse(request.Query["pageSize"], out var size) ? Math.Clamp(size, 1U, 100U) : 20;
        p = Math.Clamp(p, 1, ulong.MaxValue / s);
        return (p, s, (p - 1) * s);
    }
}
