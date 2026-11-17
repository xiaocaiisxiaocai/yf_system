using System.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourcePermissions
{
    internal const string View = "outsource:view";
    internal const string Import = "outsource:import";
    internal const string Manage = "outsource:manage";
    internal const string Settings = "outsource:settings";
}

/// <summary>
/// Purchasing-maintained reference data of the outsourcing list: the outsourcing-type dictionary with its vendor
/// mapping and the default internal copy list. Vendors and their mail contacts are the ODM/OEMA companies and
/// contacts, maintained in the ODM/OEMA directory and only read here. See 外协清单契约 → 外发类型字典与防呆 / 发送.
/// </summary>
public sealed class OutsourceSettingsService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumDefaultCc = 50;

    // ---- vendors: the ODM/OEMA companies, read only ----

    /// <summary>ODM/OEMA companies for the vendor pickers and the type mapping.</summary>
    public async Task<OutsourceVendorResponse[]> VendorsAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        return await context.OemCompanies.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new OutsourceVendorResponse(x.Id, x.Name, x.Status)).ToArrayAsync(ct);
    }

    // ---- outsourcing types ----

    public async Task<OutsourceTypeResponse[]> TypesAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        var types = await context.OutsourceTypes.AsNoTracking().OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        var mapping = (await context.OutsourceTypeVendors.AsNoTracking().ToArrayAsync(ct)).ToLookup(x => x.TypeId, x => x.VendorId);
        return types.Select(type => Json(type, mapping[type.Id])).ToArray();
    }

    public async Task<OutsourceTypeResponse> CreateTypeAsync(CurrentUser actor, OutsourceTypeUpsert request, CancellationToken ct)
    {
        var input = NormalizeType(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var type = new OutsourceType { Code = input.Code, Name = input.Name, SortNo = input.SortNo, Status = AccountStatuses.Active };
        context.OutsourceTypes.Add(type);
        await SaveUniqueAsync(context, "已有相同代码的外发类型", ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_TYPE_CREATE", "outsource_type", type.Id, new
        {
            targetName = Label(type.Code, type.Name),
            changes = AuditChange.OnlyChanged(new AuditChange("code", "类型代码", null, type.Code),
                new AuditChange("name", "中文名称", null, type.Name)),
        }, ct);
        await transaction.CommitAsync(ct);
        return Json(await context.OutsourceTypes.AsNoTracking().SingleAsync(x => x.Id == type.Id, ct), []);
    }

    public async Task<OutsourceTypeResponse> UpdateTypeAsync(CurrentUser actor, ulong id, OutsourceTypeUpsert request, CancellationToken ct)
    {
        var input = NormalizeType(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockTypeAsync(context, id, ct);
        if (await context.OutsourceTypes.AnyAsync(x => x.Code == input.Code && x.Id != id, ct))
            throw ApiException.Conflict("已有相同代码的外发类型");
        try
        {
            await context.OutsourceTypes.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Code, input.Code).SetProperty(x => x.Name, input.Name)
                .SetProperty(x => x.SortNo, input.SortNo), ct);
        }
        catch (MySqlException error) when (error.Number == 1062) { throw ApiException.Conflict("已有相同代码的外发类型"); }
        await WriteAuditAsync(context, actor, "OUTSOURCE_TYPE_UPDATE", "outsource_type", id, new
        {
            targetName = Label(input.Code, input.Name),
            changes = AuditChange.OnlyChanged(new AuditChange("code", "类型代码", before.Code, input.Code),
                new AuditChange("name", "中文名称", before.Name, input.Name),
                new AuditChange("sortNo", "排序", before.SortNo, input.SortNo)),
        }, ct);
        var result = await TypeJsonAsync(context, id, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<OutsourceTypeResponse> SetTypeStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockTypeAsync(context, id, ct);
        if (before.Status != status)
        {
            await context.OutsourceTypes.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
            await WriteAuditAsync(context, actor, "OUTSOURCE_TYPE_STATUS", "outsource_type", id, new
            {
                targetName = Label(before.Code, before.Name),
                changes = AuditChange.OnlyChanged(new AuditChange("status", "类型状态", before.Status, status)),
            }, ct);
        }
        var result = await TypeJsonAsync(context, id, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Replaces the vendors allowed for one type. Unknown vendor IDs are rejected.</summary>
    public async Task<OutsourceTypeResponse> SetTypeVendorsAsync(
        CurrentUser actor, ulong id, OutsourceTypeVendorsRequest request, CancellationToken ct)
    {
        var vendorIds = (request.VendorIds ?? []).Distinct().ToArray();
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var type = await LockTypeAsync(context, id, ct);
        var vendors = await context.OemCompanies.AsNoTracking().Where(x => vendorIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name }).ToArrayAsync(ct);
        if (vendors.Length != vendorIds.Length) throw ApiException.BadRequest("所选厂商不存在");
        var before = await context.OutsourceTypeVendors.Where(x => x.TypeId == id).Select(x => x.VendorId).ToArrayAsync(ct);
        var names = await context.OemCompanies.AsNoTracking().Where(x => before.Contains(x.Id) || vendorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        if (!before.ToHashSet().SetEquals(vendorIds))
        {
            await context.OutsourceTypeVendors.Where(x => x.TypeId == id).ExecuteDeleteAsync(ct);
            context.OutsourceTypeVendors.AddRange(vendorIds.Select(vendorId => new OutsourceTypeVendor { TypeId = id, VendorId = vendorId }));
            await context.SaveChangesAsync(ct);
            string Joined(IEnumerable<ulong> ids) => string.Join("、", ids.Select(x => names[x]).Order(StringComparer.Ordinal));
            await WriteAuditAsync(context, actor, "OUTSOURCE_TYPE_VENDORS", "outsource_type", id, new
            {
                targetName = Label(type.Code, type.Name),
                changes = AuditChange.OnlyChanged(new AuditChange("vendors", "可用厂商", Joined(before), Joined(vendorIds))),
            }, ct);
        }
        var result = await TypeJsonAsync(context, id, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    // ---- default copy list ----

    public async Task<OutsourceCcUserResponse[]> DefaultCcAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        return await DefaultCcQuery(context).ToArrayAsync(ct);
    }

    /// <summary>Replaces the default copy list. Members must be active internal accounts with an email address.</summary>
    public async Task<OutsourceCcUserResponse[]> SetDefaultCcAsync(CurrentUser actor, OutsourceDefaultCcRequest request, CancellationToken ct)
    {
        var userIds = (request.UserIds ?? []).Distinct().ToArray();
        if (userIds.Length > MaximumDefaultCc) throw ApiException.BadRequest($"默认抄送最多 {MaximumDefaultCc} 人");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var users = await context.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.RealName, x.Email, x.UserType, x.Status }).ToArrayAsync(ct);
        if (users.Length != userIds.Length) throw ApiException.BadRequest("所选人员不存在");
        if (users.Any(x => x.UserType != UserTypes.Internal || x.Status != AccountStatuses.Active))
            throw ApiException.BadRequest("默认抄送只能选择启用的内部账号");
        if (users.Any(x => string.IsNullOrWhiteSpace(x.Email)))
            throw ApiException.BadRequest("所选人员没有填写邮箱，无法接收抄送");
        var before = await DefaultCcQuery(context).Select(x => x.RealName).ToArrayAsync(ct);
        var beforeIds = await context.OutsourceDefaultCc.Select(x => x.UserId).ToArrayAsync(ct);
        if (!beforeIds.SequenceEqual(userIds))
        {
            await context.OutsourceDefaultCc.ExecuteDeleteAsync(ct);
            context.OutsourceDefaultCc.AddRange(userIds.Select((userId, index) => new OutsourceDefaultCc { UserId = userId, SortNo = index }));
            await context.SaveChangesAsync(ct);
            var byId = users.ToDictionary(x => x.Id, x => x.RealName);
            await WriteAuditAsync(context, actor, "OUTSOURCE_DEFAULT_CC", "outsource_settings", null, new
            {
                targetName = "默认抄送",
                changes = AuditChange.OnlyChanged(new AuditChange("defaultCc", "默认抄送",
                    string.Join("、", before), string.Join("、", userIds.Select(x => byId[x])))),
            }, ct);
        }
        var result = await DefaultCcQuery(context).ToArrayAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Every active internal account, for picking copy recipients. Accounts without an email address are listed
    /// too (empty <c>email</c>) so the picker can show them as unselectable instead of silently leaving them out.
    /// </summary>
    public async Task<OutsourceCcUserResponse[]> CcUserOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        var term = keyword?.Trim() ?? "";
        var query = context.Users.AsNoTracking().Where(x => x.UserType == UserTypes.Internal && x.Status == AccountStatuses.Active);
        if (term.Length > 0) query = query.Where(x => x.RealName.Contains(term) || x.EmployeeNo.Contains(term) || x.Email.Contains(term));
        return await query.OrderBy(x => x.EmployeeNo)
            .Select(x => new OutsourceCcUserResponse(x.Id, x.EmployeeNo, x.RealName, x.Email, x.Status)).ToArrayAsync(ct);
    }

    // ---- helpers ----

    /// <summary>Reads need any outsourcing permission: list users see vendor tabs, type filters and the copy list.</summary>
    private static async Task AuthorizeReadAsync(YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        var codes = await AccessService.PermissionCodesAsync(connection, null, actor.Id, ct);
        if (!codes.Any(code => code is OutsourcePermissions.View or OutsourcePermissions.Settings
                or OutsourcePermissions.Manage or OutsourcePermissions.Import))
            throw ApiException.Forbidden();
    }

    private static async Task<CurrentUser> AuthorizeWriteAsync(YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var connection = context.Database.Connection();
        var transaction = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, transaction, actor, OutsourcePermissions.Settings, ct);
        return actor;
    }

    private Task WriteAuditAsync(YfDbContext context, CurrentUser actor, string action, string targetType, ulong? targetId,
        object detail, CancellationToken ct) =>
        audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            action, targetType, targetId, detail, null, ct);

    private static async Task SaveUniqueAsync(YfDbContext context, string duplicateMessage, CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict(duplicateMessage); }
    }

    private static IQueryable<OutsourceCcUserResponse> DefaultCcQuery(YfDbContext context) =>
        context.OutsourceDefaultCc.AsNoTracking()
            .Join(context.Users, cc => cc.UserId, user => user.Id, (cc, user) => new { cc, user })
            .OrderBy(x => x.cc.SortNo).ThenBy(x => x.cc.UserId)
            .Select(x => new OutsourceCcUserResponse(x.user.Id, x.user.EmployeeNo, x.user.RealName, x.user.Email, x.user.Status));

    private static async Task<OutsourceType> LockTypeAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.OutsourceTypes.FromSqlInterpolated($"SELECT * FROM outsource_types WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static async Task<OutsourceTypeResponse> TypeJsonAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var type = await context.OutsourceTypes.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var vendorIds = await context.OutsourceTypeVendors.AsNoTracking().Where(x => x.TypeId == id).Select(x => x.VendorId).ToArrayAsync(ct);
        return Json(type, vendorIds);
    }

    private static OutsourceTypeResponse Json(OutsourceType type, IEnumerable<ulong> vendorIds) =>
        new(type.Id, type.Code, type.Name, type.Status, type.SortNo, vendorIds.Order().ToArray());

    internal static string Label(string code, string name) => $"{code}【{name}】";

    private static int SortNo(int? value) => value is null or (>= 0 and <= 9999)
        ? value ?? 0 : throw ApiException.BadRequest("排序需为 0~9999");

    private static (string Code, string Name, int SortNo) NormalizeType(OutsourceTypeUpsert request)
    {
        var code = request.Code?.Trim() ?? "";
        if (code.Length is < 1 or > 32 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw ApiException.BadRequest("类型代码需为 1~32 位字母、数字、连字符或下划线");
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.EnumerateRunes().Count() > 64) throw ApiException.BadRequest("中文名称需为 1~64 个字符");
        return (code, name, SortNo(request.SortNo));
    }
}
