using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

public static class SystemModule
{
    public static IServiceCollection AddSystemModule(this IServiceCollection services)
        => services.AddSingleton<SystemService>().AddSingleton<MailService>().AddHostedService<MailWorker>();

    public static IEndpointRouteBuilder MapSystemModule(this IEndpointRouteBuilder endpoints)
    {
        var config = endpoints.MapGroup("/api/v1/admin/system");
        config.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDb>();
            AccessService.RequireInternal(AccessService.GetCurrent(context.HttpContext));
            await using (var conn = await db.OpenAsync(context.HttpContext.RequestAborted))
                await AccessService.RequirePermissionAsync(conn, null, AccessService.GetCurrent(context.HttpContext), "config:manage", context.HttpContext.RequestAborted);
            return await next(context);
        });
        config.MapGet("/configs", (SystemService service, CancellationToken ct) => service.ListConfigsAsync(ct));
        config.MapPut("/configs", async (ConfigBatch body, HttpContext ctx, SystemService service, CancellationToken ct) =>
        { await service.UpdateConfigsAsync(body, AccessService.GetCurrent(ctx), ct); return Results.Json(new { }); });
        config.MapGet("/storage", (SystemService service, CancellationToken ct) => service.StorageAsync(ct));
        config.MapGet("/mail-status", (MailService service, CancellationToken ct) => service.StatusAsync(ct));
        var logs = endpoints.MapGroup("/api/v1/admin/audit-logs");
        logs.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDb>();
            AccessService.RequireInternal(AccessService.GetCurrent(context.HttpContext));
            await using (var conn = await db.OpenAsync(context.HttpContext.RequestAborted))
                await AccessService.RequirePermissionAsync(conn, null, AccessService.GetCurrent(context.HttpContext), "log:view", context.HttpContext.RequestAborted);
            return await next(context);
        });
        logs.MapGet("", (HttpContext ctx, SystemService service, CancellationToken ct) => service.ListLogsAsync(ctx.Request, ct));
        logs.MapDelete("/{id:long}", (ulong id, HttpContext ctx, SystemService service, CancellationToken ct) => service.DeleteLogsAsync([id], AccessService.GetCurrent(ctx), ct));
        logs.MapPost("/batch-delete", (IdList body, HttpContext ctx, SystemService service, CancellationToken ct) => service.DeleteLogsAsync(body.Ids, AccessService.GetCurrent(ctx), ct));
        return endpoints;
    }
}

public sealed record ConfigItem(string Key, string? Value);
public sealed record ConfigBatch(ConfigItem[] Items);
public sealed record IdList(ulong[] Ids);
public sealed record StorageSnapshot(string Root, string MountPoint, long TotalBytes, long AvailableBytes, long UsedBytes, double UsedPercent, double WarnPercent, bool Warning);

public sealed class SystemService(AppDb db, AuditService audit, AppOptions options)
{
    public async Task<object> ListConfigsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QueryAsync(new CommandDefinition("SELECT cfg_key AS `key`,cfg_value AS value,description,updated_at AS updatedAt FROM system_configs WHERE cfg_key <> 'security.management_lock' ORDER BY cfg_key", cancellationToken: ct));
    }

    public static string? NormalizeConfig(string key, string? input)
    {
        var value = input?.Trim();
        if (key == "security.management_lock") throw ApiException.BadRequest("系统内部参数不可修改");
        (long min, long max)? range = key switch
        {
            "upload.max_file_size" => (1048576, 21474836480),
            "upload.chunk_size" => (262144, 67108864),
            "storage.warn_percent" => (1, 99),
            _ => null
        };
        if (range is { } bounds)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < bounds.min || number > bounds.max)
                throw ApiException.BadRequest($"参数 {key} 超出允许范围");
            return number.ToString(CultureInfo.InvariantCulture);
        }
        if (key == "notify.enabled")
        {
            if (!bool.TryParse(value, out var enabled)) throw ApiException.BadRequest("notify.enabled 必须为 true 或 false");
            return enabled ? "true" : "false";
        }
        if (key == "upload.allowed_exts")
        {
            var parts = (value ?? "").Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();
            if (parts.Length == 0 || parts.Any(x => x.Length is < 1 or > 16 || x.Any(c => !char.IsAsciiLetterOrDigit(c))))
                throw ApiException.BadRequest("文件扩展名参数无效");
            return string.Join(',', parts.Distinct().Order(StringComparer.Ordinal));
        }
        return value;
    }

    public async Task UpdateConfigsAsync(ConfigBatch body, CurrentUser actor, CancellationToken ct)
    {
        if (body.Items is null || body.Items.Length is < 1 or > 100 || body.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.Key)) || body.Items.Select(x => x.Key).Distinct().Count() != body.Items.Length)
            throw ApiException.BadRequest("参数必须为 1–100 个不重复的项目");
        var normalized = body.Items.Select(x => new ConfigItem(x.Key, NormalizeConfig(x.Key, x.Value))).ToArray();
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "config:manage", ct);
        foreach (var item in normalized)
        {
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM system_configs WHERE cfg_key=@Key", item, tx, cancellationToken: ct));
            if (exists != 1) throw ApiException.BadRequest($"未知系统参数：{item.Key}");
            await conn.ExecuteAsync(new CommandDefinition("UPDATE system_configs SET cfg_value=@Value,updated_at=UTC_TIMESTAMP(6) WHERE cfg_key=@Key", item, tx, cancellationToken: ct));
        }
        await audit.WriteAsync(conn, tx, actor.Id, "CONFIG_UPDATE", "system_config", null, new { keys = normalized.Select(x => x.Key) }, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<StorageSnapshot> StorageAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var configured = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT cfg_value FROM system_configs WHERE cfg_key='storage.warn_percent'", cancellationToken: ct));
        double warn = double.TryParse(configured, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 100 ? parsed : 85;
        var root = Path.GetFullPath(options.StorageRoot);
        var drive = DriveInfo.GetDrives().Where(x => x.IsReady && root.StartsWith(x.Name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).OrderByDescending(x => x.Name.Length).FirstOrDefault()
            ?? throw new InvalidOperationException("Storage volume unavailable.");
        var used = drive.TotalSize - drive.AvailableFreeSpace;
        var percent = drive.TotalSize > 0 ? 100.0 * used / drive.TotalSize : 0;
        return new(root, drive.Name, drive.TotalSize, drive.AvailableFreeSpace, used, percent, warn, percent >= warn);
    }

    public async Task<object> ListLogsAsync(HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        var where = new List<string>();
        var args = new DynamicParameters(new { size, offset });
        foreach (var (name, column) in new[] { ("action", "a.action"), ("targetType", "a.target_type"), ("targetId", "a.target_id") })
            if (!string.IsNullOrWhiteSpace(request.Query[name])) { where.Add($"{column}=@{name}"); args.Add(name, request.Query[name].ToString()); }
        if (!string.IsNullOrWhiteSpace(request.Query["keyword"]))
        {
            where.Add("(a.employee_no LIKE @keyword OR a.action LIKE @keyword OR a.target_type LIKE @keyword OR a.target_id LIKE @keyword)");
            args.Add("keyword", "%" + request.Query["keyword"] + "%");
        }
        if (!string.IsNullOrWhiteSpace(request.Query["employeeNo"])) { where.Add("a.employee_no LIKE @employeeNo"); args.Add("employeeNo", "%" + request.Query["employeeNo"] + "%"); }
        foreach (var name in new[] { "start", "end" })
            if (!string.IsNullOrWhiteSpace(request.Query[name]))
            {
                if (!DateTimeOffset.TryParse(request.Query[name], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) throw ApiException.BadRequest("日期参数无效");
                where.Add($"a.created_at {(name == "start" ? ">=" : "<=")} @{name}"); args.Add(name, at.UtcDateTime);
            }
        if (Categories.TryGetValue(request.Query["category"].ToString(), out var actions)) { where.Add("a.action IN @actions"); args.Add("actions", actions); }
        var condition = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) FROM audit_logs a" + condition, args, cancellationToken: ct));
        var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition("SELECT a.id,a.user_id AS UserId,COALESCE(a.employee_no,u.employee_no) AS EmployeeNo,a.action,a.target_type AS TargetType,a.target_id AS TargetId,a.detail,a.ip,a.created_at AS CreatedAt FROM audit_logs a LEFT JOIN users u ON u.id=a.user_id" + condition + " ORDER BY a.id DESC LIMIT @size OFFSET @offset", args, cancellationToken: ct));
        return new { list = rows.Select(x => x.ToResponse()), total, page, pageSize = size };
    }

    public async Task<object> DeleteLogsAsync(ulong[] ids, CurrentUser actor, CancellationToken ct)
    {
        if (ids is null || ids.Length is < 1 or > 500) throw ApiException.BadRequest("每次可删除 1–500 条日志");
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "log:view", ct);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "log:delete", ct);
        var rows = (await conn.QueryAsync<AuditRow>(new CommandDefinition("SELECT id,action FROM audit_logs WHERE id IN @ids ORDER BY id FOR UPDATE", new { ids = ids.Distinct().ToArray() }, tx, cancellationToken: ct))).ToArray();
        if (rows.Any(x => x.Action == "AUDIT_LOG_DELETE")) throw ApiException.Forbidden("日志清理记录不可删除");
        var actualIds = rows.Select(x => x.Id).ToArray();
        var deleted = actualIds.Length == 0 ? 0 : await conn.ExecuteAsync(new CommandDefinition("DELETE FROM audit_logs WHERE id IN @actualIds", new { actualIds }, tx, cancellationToken: ct));
        if (deleted > 0) await audit.WriteAsync(conn, tx, actor.Id, "AUDIT_LOG_DELETE", "audit_log", null, new { ids = actualIds, deleted }, null, ct);
        await tx.CommitAsync(ct);
        return new { deleted };
    }

    private static readonly Dictionary<string, string[]> Categories = new(StringComparer.Ordinal)
    {
        ["AUTH"] = "LOGIN LOGIN_FAILED LOGIN_LOCKED LOGOUT PASSWORD_CHANGE PROFILE_UPDATE".Split(' '),
        ["PROJECT"] = "PROJECT_CREATE PROJECT_UPDATE PROJECT_START PROJECT_SUBMIT PROJECT_CONFIRM PROJECT_REJECT PROJECT_WITHDRAW PROJECT_TERMINATE PROJECT_RESTART PROJECT_MEMBERS PROJECT_DELETE".Split(' '),
        ["FILE"] = "FILE_UPLOAD FILE_DOWNLOAD FILE_BATCH_DOWNLOAD FILE_DELETE UPLOAD_ABORT".Split(' '),
        ["MESSAGE"] = "MESSAGE_CREATE MESSAGE_DELETE".Split(' '),
        ["ORG"] = "USER_CREATE USER_UPDATE USER_STATUS USER_RESET_PASSWORD USER_ASSIGN_ROLE USER_ASSIGN_ROLES DEPT_CREATE DEPT_UPDATE DEPT_STATUS DEPT_DELETE USER_DELETE ROLE_CREATE ROLE_UPDATE ROLE_STATUS ROLE_ASSIGN_PERMS ROLE_DELETE".Split(' '),
        ["SUPPLIER"] = "SUPPLIER_CREATE SUPPLIER_UPDATE SUPPLIER_STATUS SUPPLIER_DELETE SUPPLIER_ACCOUNT_CREATE SUPPLIER_ACCOUNT_UPDATE SUPPLIER_ACCOUNT_STATUS SUPPLIER_ACCOUNT_RESET_PASSWORD SUPPLIER_ACCOUNT_DELETE".Split(' '),
        ["SYSTEM"] = "CONFIG_UPDATE AUDIT_LOG_DELETE EMAIL_SENT EMAIL_FAILED EMAIL_RETRY EMAIL_SKIPPED_MISSING_EMAIL".Split(' ')
    };
}

public sealed class AuditRow
{
    public ulong Id { get; set; }
    public ulong? UserId { get; set; }
    public string? EmployeeNo { get; set; }
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; }
    public object ToResponse() => new { Id, UserId, EmployeeNo, Action, TargetType, TargetId, detail = Detail is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(Detail), Ip, CreatedAt };
}
