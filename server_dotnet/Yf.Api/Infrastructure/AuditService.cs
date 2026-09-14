using Dapper;
using MySqlConnector;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yf.Api.Infrastructure;

public interface IProjectAuditCapture
{
    Task CaptureAsync(MySqlConnection db, MySqlTransaction? tx, ulong auditId, CancellationToken ct);
}

public sealed class AuditService(IEnumerable<IProjectAuditCapture> captures, IHttpContextAccessor? accessor = null, AppOptions? options = null)
{
    public async Task<ulong> WriteAsync(MySqlConnection db, MySqlTransaction? tx, ulong? actorId,
        string action, string? targetType, ulong? targetId, object? detail, string? ip, CancellationToken ct = default,
        string? employeeNoOverride = null)
    {
        var actor = actorId is null ? null : await db.QuerySingleOrDefaultAsync<AuditActor>(new CommandDefinition(
            "SELECT employee_no EmployeeNo,real_name RealName FROM users WHERE id=@actorId", new { actorId }, tx, cancellationToken: ct));
        var employeeNo = employeeNoOverride ?? actor?.EmployeeNo;
        var payload = detail is null ? new JsonObject() : JsonSerializer.SerializeToNode(detail, JsonSerializerOptions.Web) as JsonObject
            ?? new JsonObject { ["payload"] = JsonSerializer.SerializeToNode(detail, JsonSerializerOptions.Web) };
        var targetName = await TargetNameAsync(db, tx, targetType, targetId, ct);
        targetName ??= new[] { "targetName", "name", "newName", "fileName", "employeeNo" }
            .Select(key => payload[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var context = accessor?.HttpContext;
        if (string.IsNullOrWhiteSpace(ip) && context is not null)
            ip = options is null ? context.Connection.RemoteIpAddress?.ToString() : ClientIp.Resolve(context, options);
        payload["auditContext"] = JsonSerializer.SerializeToNode(new
        {
            actorName = actor?.RealName,
            targetName,
            requestId = context?.TraceIdentifier,
            source = context is null ? "SYSTEM" : "HTTP",
        }, JsonSerializerOptions.Web);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_logs(user_id,employee_no,action,target_type,target_id,detail,ip,created_at)
            VALUES(@actorId,@employeeNo,@action,@targetType,@targetId,@detail,@ip,UTC_TIMESTAMP(6))
            """, new { actorId, employeeNo, action, targetType, targetId = targetId?.ToString(),
                detail = payload.ToJsonString(JsonSerializerOptions.Web), ip }, tx, cancellationToken: ct));
        var id = await db.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        foreach (var capture in captures) await capture.CaptureAsync(db, tx, id, ct);
        return id;
    }

    private static Task<string?> TargetNameAsync(MySqlConnection db, MySqlTransaction? tx, string? type, ulong? id, CancellationToken ct)
    {
        var sql = type switch
        {
            "project" => "SELECT name FROM projects WHERE id=@id",
            "role" => "SELECT name FROM roles WHERE id=@id",
            "department" => "SELECT name FROM departments WHERE id=@id",
            "supplier" => "SELECT name FROM suppliers WHERE id=@id",
            "user" => "SELECT real_name FROM users WHERE id=@id",
            "file" => "SELECT original_name FROM files WHERE id=@id",
            _ => null,
        };
        return id is null || sql is null ? Task.FromResult<string?>(null)
            : db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(sql, new { id }, tx, cancellationToken: ct));
    }

    private sealed record AuditActor(string? EmployeeNo, string? RealName);
}
