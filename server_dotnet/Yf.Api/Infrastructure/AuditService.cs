using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yf.Api.Infrastructure.Entities;

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
        await using var ef = EfDb.Use(db, tx);
        var actor = actorId is null ? null : await ef.Users.Where(user => user.Id == actorId.Value)
            .Select(user => new AuditActor(user.EmployeeNo, user.RealName)).SingleOrDefaultAsync(ct);
        var employeeNo = employeeNoOverride ?? actor?.EmployeeNo;
        var payload = detail is null ? new JsonObject() : JsonSerializer.SerializeToNode(detail, JsonSerializerOptions.Web) as JsonObject
            ?? new JsonObject { ["payload"] = JsonSerializer.SerializeToNode(detail, JsonSerializerOptions.Web) };
        var targetName = await TargetNameAsync(ef, targetType, targetId, ct);
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
        var createdAt = await ef.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        var auditLog = new AuditLog
        {
            UserId = actorId,
            EmployeeNo = employeeNo,
            Action = action,
            TargetType = targetType,
            TargetId = targetId?.ToString(),
            Detail = payload.ToJsonString(JsonSerializerOptions.Web),
            Ip = ip,
            CreatedAt = createdAt
        };
        ef.AuditLogs.Add(auditLog);
        await ef.SaveChangesAsync(ct);
        foreach (var capture in captures) await capture.CaptureAsync(db, tx, auditLog.Id, ct);
        return auditLog.Id;
    }

    private static async Task<string?> TargetNameAsync(YfDbContext context, string? type, ulong? id, CancellationToken ct)
    {
        if (id is not ulong targetId) return null;
        return type switch
        {
            "project" => await context.Projects.Where(item => item.Id == targetId).Select(item => item.Name).SingleOrDefaultAsync(ct),
            "role" => await context.Roles.Where(item => item.Id == targetId).Select(item => item.Name).SingleOrDefaultAsync(ct),
            "department" => await context.Departments.Where(item => item.Id == targetId).Select(item => item.Name).SingleOrDefaultAsync(ct),
            "supplier" => await context.Suppliers.Where(item => item.Id == targetId).Select(item => item.Name).SingleOrDefaultAsync(ct),
            "user" => await context.Users.Where(item => item.Id == targetId).Select(item => item.RealName).SingleOrDefaultAsync(ct),
            "file" => await context.Files.Where(item => item.Id == targetId).Select(item => item.OriginalName).SingleOrDefaultAsync(ct),
            _ => null,
        };
    }

    private sealed record AuditActor(string? EmployeeNo, string? RealName);
}
