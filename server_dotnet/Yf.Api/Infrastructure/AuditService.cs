using Dapper;
using MySqlConnector;
using System.Text.Json;

namespace Yf.Api.Infrastructure;

public interface IProjectAuditCapture
{
    Task CaptureAsync(MySqlConnection db, MySqlTransaction? tx, ulong auditId, CancellationToken ct);
}

public sealed class AuditService(IEnumerable<IProjectAuditCapture> captures)
{
    public async Task<ulong> WriteAsync(MySqlConnection db, MySqlTransaction? tx, ulong? actorId,
        string action, string? targetType, ulong? targetId, object? detail, string? ip, CancellationToken ct = default,
        string? employeeNoOverride = null)
    {
        var employeeNo = employeeNoOverride ?? (actorId is null ? null : await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT employee_no FROM users WHERE id=@actorId", new { actorId }, tx, cancellationToken: ct)));
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_logs(user_id,employee_no,action,target_type,target_id,detail,ip,created_at)
            VALUES(@actorId,@employeeNo,@action,@targetType,@targetId,@detail,@ip,UTC_TIMESTAMP(6))
            """, new { actorId, employeeNo, action, targetType, targetId = targetId?.ToString(),
                detail = detail is null ? null : JsonSerializer.Serialize(detail, JsonSerializerOptions.Web), ip }, tx, cancellationToken: ct));
        var id = await db.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
        foreach (var capture in captures) await capture.CaptureAsync(db, tx, id, ct);
        return id;
    }
}
