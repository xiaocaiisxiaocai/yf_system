using MySqlConnector;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using System.Globalization;

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

/// <summary>The body of every error response. RequestId is present only for unexpected (500) errors.</summary>
public sealed record ApiErrorResponse(
    int Code,
    string Message,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RequestId = null);

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
                DbUpdateException { InnerException: MySqlException { Number: 1062 } } => ApiException.Conflict("数据已存在，请刷新后重试"),
                _ => new ApiException(500, 50000, "服务器内部错误")
            };
            context.Response.StatusCode = error.Status;
            if (error.Status == 500)
            {
                // Log the full exception (message and inner exceptions) server-side. The path is logged
                // without its query string, which may carry the realtime hub's access_token.
                logger.LogError(exception, "Request {TraceId} {Method} {Path} failed; database code {DatabaseCode}",
                    context.TraceIdentifier, context.Request.Method, context.Request.Path.Value,
                    (exception as MySqlException)?.Number);
                // The request id matches the log entry above and the requestId in audit records, so a
                // user can report it without the response exposing any internal detail.
                await context.Response.WriteAsJsonAsync(
                    new ApiErrorResponse(error.Code, error.Message, context.TraceIdentifier), context.RequestAborted);
                return;
            }
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse(error.Code, error.Message), context.RequestAborted);
        }
    }
}

public static class QueryValues
{
    /// <summary>Escape character for patterns built by <see cref="ContainsPattern"/>; pass it to EF.Functions.Like.</summary>
    public const string LikeEscape = @"\";

    /// <summary>A LIKE pattern that matches <paramref name="term"/> literally, so user input such as "50%" or "a_b" is not treated as wildcards.</summary>
    public static string ContainsPattern(string term) =>
        "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    public static ulong? OptionalUInt64(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || !ulong.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw ApiException.BadRequest("请求参数错误: " + name);
        return value;
    }

    public static (ulong Page, uint Size, ulong Offset) Page(HttpRequest request)
    {
        var p = OptionalUInt64(request, "page") ?? 1;
        var requestedSize = OptionalUInt64(request, "pageSize") ?? 20;
        if (requestedSize > uint.MaxValue) throw ApiException.BadRequest("请求参数错误: pageSize");
        var s = Math.Clamp((uint)requestedSize, 1U, 100U);
        p = Math.Clamp(p, 1, ulong.MaxValue / s);
        return (p, s, (p - 1) * s);
    }
}
