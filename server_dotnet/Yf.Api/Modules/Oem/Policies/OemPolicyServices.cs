using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Policies;

public sealed record RetentionTemplateCreate(string Name, string Mode, uint? ReleaseTtlMinutes, uint? ReceiptGraceMinutes);
public sealed record RetentionTemplateUpdate(string Name, string Mode, uint? ReleaseTtlMinutes, uint? ReceiptGraceMinutes, string Status, ulong? Version);
public sealed record OemSettingItem(string Key, string? Value);
public sealed record OemSettingsUpdate(OemSettingItem[] Items);

/// <summary>
/// Administrator-defined deletion policies. Senders can only pick an active template;
/// its mode and durations are snapshotted at send time, so edits apply only to later
/// transfers. Templates are never physically deleted, only disabled.
/// </summary>
public sealed class OemRetentionTemplateService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit)
{
    public async Task<object> ListAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.RetentionTemplateManage, ct);
        var rows = await uow.Db.OemRetentionTemplates.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(ct);
        return rows.Select(Json);
    }

    /// <summary>Active templates a sender may choose (internal senders and OEM accounts).</summary>
    public async Task<object> OptionsAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        if (current is InternalOemActor && !await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferCreate, ct)
            && !await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferView, ct))
            throw ApiException.Forbidden();
        var rows = await uow.Db.OemRetentionTemplates.AsNoTracking().Where(item => item.Status == OemStatus.Active)
            .OrderBy(item => item.Id).ToArrayAsync(ct);
        return rows.Select(Json);
    }

    public async Task<object> CreateAsync(OemActor actor, RetentionTemplateCreate request, CancellationToken ct)
    {
        var name = OemValidation.RequiredText(request.Name, "策略名称", 64);
        RetentionStrategies.For(request.Mode).Validate(request.ReleaseTtlMinutes, request.ReceiptGraceMinutes);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.RetentionTemplateManage, ct);
        if (await uow.Db.OemRetentionTemplates.AnyAsync(item => item.Name == name, ct)) throw ApiException.Conflict("策略名称已存在");
        var template = new OemRetentionTemplate
        {
            Name = name, Mode = request.Mode, ReleaseTtlMinutes = request.ReleaseTtlMinutes, ReceiptGraceMinutes = request.ReceiptGraceMinutes,
            Status = OemStatus.Active, ConcurrencyVersion = 0, CreatedBy = current.User.Id, CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemRetentionTemplates.Add(template);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_RETENTION_TEMPLATE_CREATE", "oem_retention_template", template.Id,
            new { targetName = name, template.Mode, template.ReleaseTtlMinutes, template.ReceiptGraceMinutes }, ct);
        await uow.CommitAsync(ct);
        return Json(template);
    }

    public async Task<object> UpdateAsync(OemActor actor, ulong id, RetentionTemplateUpdate request, CancellationToken ct)
    {
        var name = OemValidation.RequiredText(request.Name, "策略名称", 64);
        var status = OemStatus.Normalize(request.Status);
        var version = OemValidation.ExpectedVersion(request.Version);
        RetentionStrategies.For(request.Mode).Validate(request.ReleaseTtlMinutes, request.ReceiptGraceMinutes);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.RetentionTemplateManage, ct);
        // Lock the whole (small) table so the "at least one active template" rule cannot be raced.
        var all = await uow.Db.OemRetentionTemplates.FromSqlRaw("SELECT * FROM oem_retention_templates FOR UPDATE").ToArrayAsync(ct);
        var template = all.SingleOrDefault(item => item.Id == id) ?? throw ApiException.NotFound();
        OemValidation.MatchVersion(template.ConcurrencyVersion, version);
        if (all.Any(item => item.Name == name && item.Id != id)) throw ApiException.Conflict("策略名称已存在");
        if (status == OemStatus.Disabled && !all.Any(item => item.Id != id && item.Status == OemStatus.Active))
            throw ApiException.BadRequest("至少需要保留一个启用的删除策略");
        var changes = AuditChange.OnlyChanged(
            new AuditChange("name", "策略名称", template.Name, name),
            new AuditChange("mode", "删除模式", template.Mode, request.Mode),
            new AuditChange("releaseTtlMinutes", "发布后期限（分钟）", template.ReleaseTtlMinutes, request.ReleaseTtlMinutes),
            new AuditChange("receiptGraceMinutes", "接收宽限期（分钟）", template.ReceiptGraceMinutes, request.ReceiptGraceMinutes),
            new AuditChange("status", "状态", template.Status, status));
        template.Name = name;
        template.Mode = request.Mode;
        template.ReleaseTtlMinutes = request.ReleaseTtlMinutes;
        template.ReceiptGraceMinutes = request.ReceiptGraceMinutes;
        template.Status = status;
        template.ConcurrencyVersion++;
        template.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_RETENTION_TEMPLATE_UPDATE", "oem_retention_template", id, new { targetName = name, changes }, ct);
        await uow.CommitAsync(ct);
        return Json(template);
    }

    private static object Json(OemRetentionTemplate template) => new
    {
        template.Id, template.Name, template.Mode, template.ReleaseTtlMinutes, template.ReceiptGraceMinutes, template.Status,
        version = template.ConcurrencyVersion, summary = Describe(template.Mode, template.ReleaseTtlMinutes, template.ReceiptGraceMinutes),
        template.CreatedAt, template.UpdatedAt,
    };

    /// <summary>Plain-language behaviour shown to senders, including the "kept until someone downloads" caveat.</summary>
    public static string Describe(string mode, uint? releaseTtl, uint? receiptGrace) => mode switch
    {
        RetentionModes.Keep => "不自动删除",
        RetentionModes.AfterRelease => $"发布 {Duration(releaseTtl)} 后删除",
        RetentionModes.AfterFirstReceipt => $"首名接收方完整下载 {Duration(receiptGrace)} 后删除；无人下载时不会自动删除",
        RetentionModes.FirstReceiptOrDeadline => $"首名接收方完整下载 {Duration(receiptGrace)} 后删除，最迟发布 {Duration(releaseTtl)} 后删除",
        _ => mode,
    };

    private static string Duration(uint? minutes) => minutes switch
    {
        null => "-",
        var value when value % (24 * 60) == 0 => $"{value / (24 * 60)} 天",
        var value when value % 60 == 0 => $"{value / 60} 小时",
        var value => $"{value} 分钟",
    };
}

/// <summary>
/// Admin pages for the `oem.*` parameters. The same service serves the file policy
/// page and the notification page; each page only reads and writes its own group.
/// </summary>
public sealed class OemSettingsService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit)
{
    public Task<object> GetAsync(OemActor actor, OemSettingGroup group, CancellationToken ct) => ReadAsync(actor, group, ct);

    public async Task<object> UpdateAsync(OemActor actor, OemSettingGroup group, OemSettingsUpdate request, CancellationToken ct)
    {
        if (group == OemSettingGroup.System) throw ApiException.Forbidden();
        if (request.Items is null || request.Items.Length is < 1 or > 100) throw ApiException.BadRequest("参数必须为 1–100 个项目");
        var normalized = request.Items.Select(item =>
        {
            var key = item?.Key?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!OemSettingCatalog.TryGet(key, out var definition) || definition.Group != group) throw ApiException.BadRequest($"未知参数：{key}");
            return (definition, value: definition.Normalize(item!.Value));
        }).ToArray();
        if (normalized.Select(item => item.definition.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw ApiException.BadRequest("参数不能重复");
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, PermissionFor(group), ct);
        var changes = new List<AuditChange>();
        foreach (var (definition, value) in normalized)
        {
            var config = await uow.Db.SystemConfigs.SingleOrDefaultAsync(item => item.CfgKey == definition.Key, ct);
            if (config is null)
            {
                config = new Infrastructure.Entities.SystemConfig { CfgKey = definition.Key, Description = definition.Label };
                uow.Db.SystemConfigs.Add(config);
            }
            if (config.CfgValue != value) changes.Add(new AuditChange(definition.Key, definition.Label, config.CfgValue, value));
            config.CfgValue = value;
            config.UpdatedAt = uow.Now;
        }
        await uow.Db.SaveChangesAsync(ct);
        if (changes.Count > 0)
            await audit.WriteAsync(uow, current, "OEM_SETTINGS_UPDATE", "oem_settings", null,
                new { group = group.ToString(), changes = AuditChange.OnlyChanged(changes.ToArray()) }, ct);
        await uow.CommitAsync(ct);
        return await ReadAsync(actor, group, ct);
    }

    private async Task<object> ReadAsync(OemActor actor, OemSettingGroup group, CancellationToken ct)
    {
        if (group == OemSettingGroup.System) throw ApiException.Forbidden();
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, PermissionFor(group), ct);
        var settings = await OemSettings.LoadAsync(uow.Db, ct);
        return OemSettingCatalog.All.Where(item => item.Group == group).Select(item => new
        {
            item.Key, item.Label, kind = item.Kind.ToString().ToLowerInvariant(), value = settings.Raw(item.Key),
            min = item.Kind == OemSettingKind.Integer ? item.Min : (long?)null,
            max = item.Kind == OemSettingKind.Integer ? item.Max : (long?)null,
        });
    }

    private static string PermissionFor(OemSettingGroup group) => group == OemSettingGroup.Notify
        ? OemPermissions.NotifyManage
        : OemPermissions.FilePolicyManage;
}
