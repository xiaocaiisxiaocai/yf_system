using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

public static class SystemModule
{
    public static IServiceCollection AddSystemModule(this IServiceCollection services)
        => services.AddSingleton<SystemService>().AddSingleton<SmtpSettingsService>().AddSingleton<MailService>().AddHostedService<MailWorker>();

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
        config.MapGet("/mail-status", (MailService service, CancellationToken ct) => service.StatusAsync(ct));
        config.MapGet("/mail-settings", (SmtpSettingsService service, CancellationToken ct) => service.GetAsync(ct));
        config.MapPut("/mail-settings", (SmtpSettingsUpdate body, HttpContext ctx, SmtpSettingsService service, CancellationToken ct) =>
            service.SaveAsync(body, AccessService.GetCurrent(ctx), ct));
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

public sealed class SystemService(AppDb db, AuditService audit)
{
    public async Task<object> ListConfigsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QueryAsync(new CommandDefinition("SELECT cfg_key AS `key`,cfg_value AS value,description,updated_at AS updatedAt FROM system_configs WHERE cfg_key NOT IN ('security.management_lock','mail.smtp','storage.warn_percent') ORDER BY cfg_key", cancellationToken: ct));
    }

    public static string? NormalizeConfig(string key, string? input)
    {
        key = key.Trim().ToLowerInvariant();
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw ApiException.BadRequest("系统参数名称无效");
        var value = input?.Trim();
        if (key is "security.management_lock" or "mail.smtp") throw ApiException.BadRequest("请使用对应的专用配置入口");
        if (key == "storage.warn_percent") throw ApiException.BadRequest("存储告警阈值已停用");
        (long min, long max)? range = key switch
        {
            "upload.max_file_size" => (1048576, 21474836480),
            "upload.chunk_size" => (262144, 67108864),
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
        if (body.Items is null || body.Items.Length is < 1 or > 100 || body.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.Key)))
            throw ApiException.BadRequest("参数必须为 1–100 个不重复的项目");
        var normalized = body.Items.Select(x => new ConfigItem(x.Key.Trim().ToLowerInvariant(), NormalizeConfig(x.Key, x.Value))).ToArray();
        if (normalized.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw ApiException.BadRequest("参数必须为 1–100 个不重复的项目");
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "config:manage", ct);
        var changes = new List<AuditChange>();
        foreach (var item in normalized)
        {
            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM system_configs WHERE cfg_key=@Key", item, tx, cancellationToken: ct));
            if (exists != 1) throw ApiException.BadRequest($"未知系统参数：{item.Key}");
            var previous = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition("SELECT cfg_value FROM system_configs WHERE cfg_key=@Key", item, tx, cancellationToken: ct));
            if (previous != item.Value)
            {
                var safe = item.Key is "notify.enabled" or "upload.max_file_size" or "upload.chunk_size" or "upload.allowed_exts";
                changes.Add(new AuditChange(item.Key, ConfigLabel(item.Key), safe ? previous : "未展示", safe ? item.Value : "已更新"));
            }
            await conn.ExecuteAsync(new CommandDefinition("UPDATE system_configs SET cfg_value=@Value,updated_at=UTC_TIMESTAMP(6) WHERE cfg_key=@Key", item, tx, cancellationToken: ct));
        }
        await audit.WriteAsync(conn, tx, actor.Id, "CONFIG_UPDATE", "system_config", null, new { keys = normalized.Select(x => x.Key), changes }, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<object> ListLogsAsync(HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        var where = new List<string>();
        var args = new DynamicParameters(new { size, offset });
        foreach (var (name, column) in new[] { ("action", "a.action"), ("targetType", "a.target_type"), ("targetId", "a.target_id") })
            if (!string.IsNullOrWhiteSpace(request.Query[name])) { where.Add($"{column}=@{name}"); args.Add(name, request.Query[name].ToString().Trim()); }
        if (!string.IsNullOrWhiteSpace(request.Query["keyword"]))
        {
            where.Add("(a.employee_no LIKE @keyword OR u.real_name LIKE @keyword OR a.action LIKE @keyword OR a.target_type LIKE @keyword OR a.target_id LIKE @keyword OR a.detail LIKE @keyword OR p.name LIKE @keyword OR r.name LIKE @keyword OR d.name LIKE @keyword OR s.name LIKE @keyword OR target_user.real_name LIKE @keyword OR f.original_name LIKE @keyword)");
            args.Add("keyword", "%" + request.Query["keyword"].ToString().Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(request.Query["employeeNo"])) { where.Add("a.employee_no LIKE @employeeNo"); args.Add("employeeNo", "%" + request.Query["employeeNo"].ToString().Trim() + "%"); }
        foreach (var name in new[] { "start", "end" })
            if (!string.IsNullOrWhiteSpace(request.Query[name]))
            {
                if (!DateTimeOffset.TryParse(request.Query[name], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) throw ApiException.BadRequest("日期参数无效");
                where.Add($"a.created_at {(name == "start" ? ">=" : "<=")} @{name}"); args.Add(name, at.UtcDateTime);
            }
        if (Categories.TryGetValue(request.Query["category"].ToString().Trim(), out var actions)) { where.Add("a.action IN @actions"); args.Add("actions", actions); }
        var condition = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        const string from = """
            FROM audit_logs a
            LEFT JOIN users u ON u.id=a.user_id
            LEFT JOIN projects p ON a.target_type='project' AND p.id=a.target_id
            LEFT JOIN roles r ON a.target_type='role' AND r.id=a.target_id
            LEFT JOIN departments d ON a.target_type='department' AND d.id=a.target_id
            LEFT JOIN suppliers s ON a.target_type='supplier' AND s.id=a.target_id
            LEFT JOIN users target_user ON a.target_type='user' AND target_user.id=a.target_id
            LEFT JOIN files f ON a.target_type='file' AND f.id=a.target_id
            """;
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT COUNT(*) " + from + condition, args, cancellationToken: ct));
        var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition("SELECT a.id,a.user_id AS UserId,COALESCE(a.employee_no,u.employee_no) AS EmployeeNo,u.real_name CurrentActorName,COALESCE(p.name,r.name,d.name,s.name,target_user.real_name,f.original_name) CurrentTargetName,a.action,a.target_type AS TargetType,a.target_id AS TargetId,a.detail,a.ip,a.created_at AS CreatedAt " + from + condition + " ORDER BY a.id DESC LIMIT @size OFFSET @offset", args, cancellationToken: ct));
        return new { list = rows.Select(x => x.ToResponse()), total, page, pageSize = size };
    }

    private static string ConfigLabel(string key) => key switch
    {
        "notify.enabled" => "邮件通知",
        "upload.max_file_size" => "单文件大小上限",
        "upload.chunk_size" => "上传分片大小",
        "upload.allowed_exts" => "允许的文件类型",
        _ => key,
    };

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
        ["PROJECT"] = "PROJECT_GROUP_CREATE PROJECT_GROUP_UPDATE PROJECT_GROUP_STATUS_AUTO PROJECT_GROUP_DELETE PROJECT_CREATE PROJECT_COPY PROJECT_UPDATE PROJECT_START PROJECT_SUBMIT PROJECT_CONFIRM PROJECT_REJECT PROJECT_WITHDRAW PROJECT_TERMINATE PROJECT_RESTART PROJECT_MEMBERS PROJECT_DELETE PROJECT_ACCEPTANCE_MIGRATE PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE".Split(' '),
        ["FILE"] = "FILE_UPLOAD FILE_DOWNLOAD FILE_BATCH_DOWNLOAD FILE_DELETE UPLOAD_ABORT".Split(' '),
        ["MESSAGE"] = "MESSAGE_CREATE MESSAGE_DELETE".Split(' '),
        ["ORG"] = "USER_CREATE USER_UPDATE USER_STATUS USER_RESET_PASSWORD USER_ASSIGN_ROLE USER_ASSIGN_ROLES DEPT_CREATE DEPT_UPDATE DEPT_STATUS DEPT_DELETE USER_DELETE ROLE_CREATE ROLE_UPDATE ROLE_STATUS ROLE_ASSIGN_PERMS ROLE_DELETE".Split(' '),
        ["SUPPLIER"] = "SUPPLIER_CREATE SUPPLIER_UPDATE SUPPLIER_STATUS SUPPLIER_DELETE SUPPLIER_ACCOUNT_CREATE SUPPLIER_ACCOUNT_UPDATE SUPPLIER_ACCOUNT_STATUS SUPPLIER_ACCOUNT_RESET_PASSWORD SUPPLIER_ACCOUNT_DELETE".Split(' '),
        ["SYSTEM"] = "CONFIG_UPDATE AUDIT_LOG_DELETE EMAIL_SENT EMAIL_FAILED EMAIL_RETRY EMAIL_SKIPPED_MISSING_EMAIL EMAIL_CANCELLED_STALE".Split(' ')
    };
}

public sealed class AuditRow
{
    public ulong Id { get; set; }
    public ulong? UserId { get; set; }
    public string? EmployeeNo { get; set; }
    public string? CurrentActorName { get; set; }
    public string? CurrentTargetName { get; set; }
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; }
    public object ToResponse()
    {
        var detail = Detail is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(Detail);
        string? Snapshot(string field) => detail is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("auditContext", out var context) && context.ValueKind == JsonValueKind.Object
            && context.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString() : null;
        var actorSnapshot = Snapshot("actorName");
        var targetSnapshot = Snapshot("targetName");
        return new
        {
            Id, UserId, EmployeeNo, Action, TargetType, TargetId, detail, Ip, CreatedAt,
            actorName = actorSnapshot ?? CurrentActorName,
            targetName = targetSnapshot ?? CurrentTargetName,
            actorNameSource = actorSnapshot is not null ? "snapshot" : CurrentActorName is not null ? "current" : "unknown",
            targetNameSource = targetSnapshot is not null ? "snapshot" : CurrentTargetName is not null ? "current" : "unknown",
        };
    }
}
