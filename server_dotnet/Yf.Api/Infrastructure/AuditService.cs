using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Infrastructure;

public interface IProjectAuditCapture
{
    /// <summary>Called inside the audit write transaction with the just-inserted row, so implementations need not re-read it.</summary>
    Task CaptureAsync(MySqlConnection db, MySqlTransaction? tx, Entities.AuditLog audit, string? actorName, CancellationToken ct);

    async Task CaptureBatchAsync(MySqlConnection db, MySqlTransaction? tx,
        IReadOnlyList<Entities.AuditLog> audits, string? actorName, CancellationToken ct)
    {
        foreach (var audit in audits) await CaptureAsync(db, tx, audit, actorName, ct);
    }
}

/// <summary>Partitions the shared audit table between collaboration and OEM views.</summary>
public static class AuditScopes
{
    public const string OemActionPrefix = "OEM_";
    public const string OemRealm = "oem";
    public const string OemConfigPrefix = "oem.";

    public static readonly System.Linq.Expressions.Expression<Func<AuditLog, bool>> IsOemRow =
        log => log.ActorRealm == OemRealm || log.Action.StartsWith(OemActionPrefix);

    public static readonly System.Linq.Expressions.Expression<Func<AuditLog, bool>> IsCollaborationRow =
        log => (log.ActorRealm == null || log.ActorRealm != OemRealm)
               && !log.Action.StartsWith(OemActionPrefix);

    public static bool IsOemAction(string action) =>
        action.StartsWith(OemActionPrefix, StringComparison.Ordinal);

    public static bool IsCollaboration(AuditLog audit) =>
        audit.ActorRealm != OemRealm && !IsOemAction(audit.Action);
}

/// <summary>An actor from outside users; its id is stored with its realm, never in AuditLog.UserId.</summary>
public sealed record AuditRealmActor(string Realm, ulong AccountId, string EmployeeNo, string? Name);

public sealed record AuditWrite(
    string Action,
    string? TargetType,
    ulong? TargetId,
    object? Detail);

public sealed class AuditService(IEnumerable<IProjectAuditCapture> captures, IHttpContextAccessor? accessor = null, AppOptions? options = null)
{
    public async Task<ulong> WriteAsync(MySqlConnection db, MySqlTransaction? tx, ulong? actorId,
        string action, string? targetType, ulong? targetId, object? detail, string? ip, CancellationToken ct = default,
        string? employeeNoOverride = null, AuditRealmActor? realmActor = null)
    {
        var ids = await WriteBatchAsync(db, tx, actorId,
            [new AuditWrite(action, targetType, targetId, detail)], ip, ct, employeeNoOverride, realmActor);
        return ids[0];
    }

    /// <summary>
    /// Writes one actor's audit entries as a transaction-local batch. Actor, database clock, request context
    /// and target names are resolved once per batch; no result is cached across transactions.
    /// </summary>
    public async Task<IReadOnlyList<ulong>> WriteBatchAsync(
        MySqlConnection db,
        MySqlTransaction? tx,
        ulong? actorId,
        IReadOnlyCollection<AuditWrite> writes,
        string? ip,
        CancellationToken ct = default,
        string? employeeNoOverride = null,
        AuditRealmActor? realmActor = null)
    {
        if (writes.Count == 0) return [];
        if (actorId is not null && realmActor is not null)
            throw new ArgumentException("A realm actor cannot also be a users-table actor.", nameof(actorId));
        await using var ef = EfDb.Use(db, tx);
        var actor = realmActor is not null
            ? new AuditActor(realmActor.EmployeeNo, realmActor.Name)
            : actorId is null
                ? null
                : await ef.Users.Where(user => user.Id == actorId.Value)
                    .Select(user => new AuditActor(user.EmployeeNo, user.RealName)).SingleOrDefaultAsync(ct);
        var employeeNo = employeeNoOverride ?? actor?.EmployeeNo;
        var context = accessor?.HttpContext;
        if (string.IsNullOrWhiteSpace(ip) && context is not null)
            ip = options is null ? context.Connection.RemoteIpAddress?.ToString() : ClientIp.Resolve(context, options);
        var createdAt = await DbClock.UtcNowAsync(ef, ct, 3);
        var entries = writes.ToArray();
        var targetNames = await TargetNamesAsync(ef, entries, ct);
        var auditLogs = new List<AuditLog>(entries.Length);
        foreach (var entry in entries)
        {
            var payload = entry.Detail is null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(entry.Detail, JsonDefaults.Web) as JsonObject
                  ?? new JsonObject { ["payload"] = JsonSerializer.SerializeToNode(entry.Detail, JsonDefaults.Web) };
            targetNames.TryGetValue((entry.TargetType, entry.TargetId), out var targetName);
            targetName ??= new[] { "targetName", "name", "newName", "fileName", "employeeNo" }
                .Select(key => payload[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            payload["auditContext"] = JsonSerializer.SerializeToNode(new
            {
                actorName = actor?.RealName,
                targetName,
                requestId = context?.TraceIdentifier,
                source = context is null ? "SYSTEM" : "HTTP",
            }, JsonDefaults.Web);
            auditLogs.Add(new AuditLog
            {
                UserId = actorId,
                EmployeeNo = employeeNo,
                Action = entry.Action,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId?.ToString(),
                Detail = payload.ToJsonString(JsonDefaults.Web),
                Ip = ip,
                CreatedAt = createdAt,
                ActorRealm = realmActor?.Realm,
                ActorAccountId = realmActor?.AccountId,
            });
        }
        ef.AuditLogs.AddRange(auditLogs);
        await ef.SaveChangesAsync(ct);
        var collaborationAudits = auditLogs.Where(AuditScopes.IsCollaboration).ToArray();
        if (collaborationAudits.Length > 0)
        {
            foreach (var capture in captures)
                await capture.CaptureBatchAsync(db, tx, collaborationAudits, actor?.RealName, ct);
        }
        return auditLogs.Select(audit => audit.Id).ToArray();
    }

    private static async Task<Dictionary<(string? Type, ulong? Id), string>> TargetNamesAsync(
        YfDbContext context, IReadOnlyCollection<AuditWrite> writes, CancellationToken ct)
    {
        var result = new Dictionary<(string? Type, ulong? Id), string>();
        foreach (var group in writes.Where(write => write.TargetId is not null)
                     .GroupBy(write => write.TargetType, StringComparer.Ordinal))
        {
            var ids = group.Select(write => write.TargetId!.Value).Distinct().ToArray();
            var names = group.Key switch
            {
                "project" => await context.Projects.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.Name)).ToArrayAsync(ct),
                "role" => await context.Roles.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.Name)).ToArrayAsync(ct),
                "department" => await context.Departments.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.Name)).ToArrayAsync(ct),
                "supplier" => await context.Suppliers.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.Name)).ToArrayAsync(ct),
                "robot_part" => await context.RobotParts.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.PartNumber)).ToArrayAsync(ct),
                "user" => await context.Users.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.RealName)).ToArrayAsync(ct),
                "file" => await context.Files.Where(item => Enumerable.Contains(ids, item.Id))
                    .Select(item => new TargetName(item.Id, item.OriginalName)).ToArrayAsync(ct),
                _ => [],
            };
            foreach (var name in names) result[(group.Key, name.Id)] = name.Name;
        }
        return result;
    }

    private sealed record AuditActor(string? EmployeeNo, string? RealName);
    private sealed record TargetName(ulong Id, string Name);
}
