using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.SystemManagement;

public static class SystemModule
{
    public static IServiceCollection AddSystemModule(this IServiceCollection services)
        => services.AddSingleton<SystemService>().AddSingleton<SmtpSettingsService>().AddSingleton<MailService>().AddHostedService<MailWorker>()
            .AddHostedService<AuditRetentionService>();

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
        { await service.UpdateConfigsAsync(body, AccessService.GetCurrent(ctx), ct); return EmptyResponse.Instance; });
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
    public async Task<SystemConfigResponse[]> ListConfigsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var hidden = new[] { "security.management_lock", "mail.smtp", "storage.warn_percent" };
        return await context.SystemConfigs.Where(config => !Enumerable.Contains(hidden, config.CfgKey))
            .OrderBy(config => config.CfgKey)
            .Select(config => new SystemConfigResponse(config.CfgKey, config.CfgValue, config.Description, config.UpdatedAt))
            .ToArrayAsync(ct);
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
        if (EmailNotificationPolicy.Keys.Contains(key, StringComparer.Ordinal))
        {
            if (value == "1") return "true";
            if (value == "0") return "false";
            if (!bool.TryParse(value, out var enabled))
                throw ApiException.BadRequest($"{ConfigLabel(key)}必须为 true 或 false");
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
        await using var context = EfDb.Use(conn, tx);
        var updatedAt = await context.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        var changes = new List<AuditChange>();
        foreach (var item in normalized)
        {
            var config = await context.SystemConfigs.SingleOrDefaultAsync(config => config.CfgKey == item.Key, ct)
                ?? throw ApiException.BadRequest($"未知系统参数：{item.Key}");
            var previous = config.CfgValue;
            if (previous != item.Value)
            {
                var safe = EmailNotificationPolicy.Keys.Contains(item.Key, StringComparer.Ordinal)
                    || item.Key is "upload.max_file_size" or "upload.chunk_size" or "upload.allowed_exts";
                changes.Add(new AuditChange(item.Key, ConfigLabel(item.Key), safe ? previous : "未展示", safe ? item.Value : "已更新"));
            }
            config.CfgValue = item.Value;
            config.UpdatedAt = updatedAt;
        }
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, actor.Id, "CONFIG_UPDATE", "system_config", null, new { keys = normalized.Select(x => x.Key), changes }, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<PageResponse<AuditLogResponse>> ListLogsAsync(HttpRequest request, CancellationToken ct)
    {
        var (page, size, offset) = QueryValues.Page(request);
        var category = request.Query["category"].ToString().Trim();
        string[]? categoryActions = null;
        if (category.Length > 0 && !Categories.TryGetValue(category, out categoryActions))
            throw ApiException.BadRequest("日志分类参数无效");
        await using var conn = await db.OpenAsync(ct);
        await using var context = EfDb.Use(conn);
        var query = context.AuditLogs.AsNoTracking();
        var action = request.Query["action"].ToString().Trim();
        var targetType = request.Query["targetType"].ToString().Trim();
        var targetId = request.Query["targetId"].ToString().Trim();
        if (action.Length > 0) query = query.Where(log => log.Action == action);
        if (targetType.Length > 0) query = query.Where(log => log.TargetType == targetType);
        if (targetId.Length > 0) query = query.Where(log => log.TargetId == targetId);
        var keyword = request.Query["keyword"].ToString().Trim();
        if (keyword.Length > 0)
        {
            var pattern = QueryValues.ContainsPattern(keyword);
            query = query.Where(log =>
                log.EmployeeNo != null && EF.Functions.Like(log.EmployeeNo, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(log.Action, pattern, QueryValues.LikeEscape)
                || log.TargetType != null && EF.Functions.Like(log.TargetType, pattern, QueryValues.LikeEscape)
                || log.TargetId != null && EF.Functions.Like(log.TargetId, pattern, QueryValues.LikeEscape)
                || log.Detail != null && EF.Functions.Like(EF.Functions.JsonUnquote(log.Detail), pattern, QueryValues.LikeEscape)
                || context.Users.Any(user => user.Id == log.UserId && EF.Functions.Like(user.RealName, pattern, QueryValues.LikeEscape)));
        }
        var employeeNo = request.Query["employeeNo"].ToString().Trim();
        if (employeeNo.Length > 0)
        {
            var pattern = QueryValues.ContainsPattern(employeeNo);
            query = query.Where(log => log.EmployeeNo != null && EF.Functions.Like(log.EmployeeNo, pattern, QueryValues.LikeEscape));
        }
        foreach (var name in new[] { "start", "end" })
            if (!string.IsNullOrWhiteSpace(request.Query[name]))
            {
                if (!DateTimeOffset.TryParse(request.Query[name], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) throw ApiException.BadRequest("日期参数无效");
                var utc = at.UtcDateTime;
                query = name == "start" ? query.Where(log => log.CreatedAt >= utc) : query.Where(log => log.CreatedAt <= utc);
            }
        if (categoryActions is not null)
            query = query.Where(log => Enumerable.Contains(categoryActions, log.Action));
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(log => log.Id).Select(log => new AuditRow
        {
            Id = log.Id, UserId = log.UserId, EmployeeNo = log.EmployeeNo,
            CurrentActorName = context.Users.Where(user => user.Id == log.UserId).Select(user => user.RealName).FirstOrDefault(),
            // Every new audit record stores its target-name snapshot in Detail.auditContext.
            // Keeping the projection snapshot-only avoids cross-table string casts and preserves history.
            CurrentTargetName = null,
            Action = log.Action, TargetType = log.TargetType, TargetId = log.TargetId, Detail = log.Detail,
            Ip = log.Ip, CreatedAt = log.CreatedAt,
        }).Page(offset, size).ToArrayAsync(ct);
        return new(rows.Select(x => x.ToResponse()).ToArray(), total, page, size);
    }

    private static string ConfigLabel(string key) => key switch
    {
        "notify.enabled" => "邮件通知",
        "notify.internal.enabled" => "内部员工邮件通知",
        "notify.supplier.enabled" => "外部企业邮件通知",
        "notify.event.message_created" => "新留言邮件提醒",
        "notify.event.file_uploaded" => "新文件邮件提醒",
        "notify.event.project_submitted" => "提交验收邮件提醒",
        "notify.event.project_confirmed" => "验收通过邮件提醒",
        "notify.event.project_rejected" => "验收驳回邮件提醒",
        "notify.event.project_withdrawn" => "撤回验收邮件提醒",
        "upload.max_file_size" => "单文件大小上限",
        "upload.chunk_size" => "上传分片大小",
        "upload.allowed_exts" => "允许的文件类型",
        _ => key,
    };

    public async Task<AuditLogDeleteResponse> DeleteLogsAsync(ulong[] ids, CurrentUser actor, CancellationToken ct)
    {
        if (ids is null || ids.Length is < 1 or > 500) throw ApiException.BadRequest("每次可删除 1–500 条日志");
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockManagementAsync(conn, tx, ct);
        actor = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "log:view", ct);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "log:delete", ct);
        await using var context = EfDb.Use(conn, tx);
        var distinctIds = ids.Distinct().Order().ToArray();
        var rows = new List<AuditRow>(distinctIds.Length);
        foreach (var id in distinctIds)
        {
            var locked = await context.AuditLogs.FromSqlInterpolated($"SELECT * FROM audit_logs WHERE id={id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (locked is not null) rows.Add(new AuditRow { Id = locked.Id, Action = locked.Action });
        }
        if (rows.Any(x => x.Action == "AUDIT_LOG_DELETE")) throw ApiException.Forbidden("日志清理记录不可删除");
        var actualIds = rows.Select(x => x.Id).ToArray();
        var deleted = actualIds.Length == 0 ? 0 : await context.AuditLogs.Where(log => Enumerable.Contains(actualIds, log.Id)).ExecuteDeleteAsync(ct);
        if (deleted > 0) await audit.WriteAsync(conn, tx, actor.Id, "AUDIT_LOG_DELETE", "audit_log", null, new { ids = actualIds, deleted }, null, ct);
        await tx.CommitAsync(ct);
        return new(deleted);
    }

    private static readonly Dictionary<string, string[]> Categories = new(StringComparer.Ordinal)
    {
        ["AUTH"] = "LOGIN LOGIN_FAILED LOGIN_LOCKED LOGOUT PASSWORD_CHANGE PROFILE_UPDATE".Split(' '),
        ["PROJECT"] = "PROJECT_GROUP_CREATE PROJECT_GROUP_UPDATE PROJECT_GROUP_STATUS_AUTO PROJECT_GROUP_DELETE PROJECT_CREATE PROJECT_COPY PROJECT_UPDATE PROJECT_START PROJECT_SUBMIT PROJECT_CONFIRM PROJECT_REJECT PROJECT_WITHDRAW PROJECT_ACCEPTANCE_MIGRATE PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE PROJECT_TERMINATE PROJECT_RESTART PROJECT_MEMBERS PROJECT_DELETE".Split(' '),
        ["FILE"] = "FILE_UPLOAD FILE_DOWNLOAD FILE_BATCH_DOWNLOAD FILE_DELETE UPLOAD_ABORT".Split(' '),
        ["MESSAGE"] = "MESSAGE_CREATE MESSAGE_DELETE MESSAGE_READ".Split(' '),
        ["ORG"] = "USER_CREATE USER_UPDATE USER_STATUS USER_RESET_PASSWORD USER_ASSIGN_ROLE USER_ASSIGN_ROLES DEPT_CREATE DEPT_UPDATE DEPT_STATUS DEPT_DELETE USER_DELETE ROLE_CREATE ROLE_UPDATE ROLE_STATUS ROLE_ASSIGN_PERMS ROLE_DELETE".Split(' '),
        ["SUPPLIER"] = "SUPPLIER_CREATE SUPPLIER_UPDATE SUPPLIER_STATUS SUPPLIER_DELETE SUPPLIER_ACCOUNT_CREATE SUPPLIER_ACCOUNT_UPDATE SUPPLIER_ACCOUNT_STATUS SUPPLIER_ACCOUNT_RESET_PASSWORD SUPPLIER_ACCOUNT_DELETE".Split(' '),
        ["SYSTEM"] = "CONFIG_UPDATE PROJECT_DICTIONARY_CREATE PROJECT_DICTIONARY_UPDATE PROJECT_DICTIONARY_DELETE AUDIT_LOG_DELETE AUDIT_LOG_RETENTION EMAIL_SENT EMAIL_FAILED EMAIL_RETRY EMAIL_SKIPPED_MISSING_EMAIL EMAIL_CANCELLED_STALE".Split(' ')
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
    public AuditLogResponse ToResponse()
    {
        var detail = Detail is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(Detail);
        string? Snapshot(string field) => detail is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("auditContext", out var context) && context.ValueKind == JsonValueKind.Object
            && context.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString() : null;
        var actorSnapshot = Snapshot("actorName");
        var targetSnapshot = Snapshot("targetName");
        return new AuditLogResponse(
            Id, UserId, EmployeeNo, Action, TargetType, TargetId, detail, Ip, CreatedAt,
            actorSnapshot ?? CurrentActorName,
            targetSnapshot ?? CurrentTargetName,
            actorSnapshot is not null ? "snapshot" : CurrentActorName is not null ? "current" : "unknown",
            targetSnapshot is not null ? "snapshot" : CurrentTargetName is not null ? "current" : "unknown");
    }
}
