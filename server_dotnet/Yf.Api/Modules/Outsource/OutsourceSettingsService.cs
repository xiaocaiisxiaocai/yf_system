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
/// Purchasing-maintained reference data of the outsourcing list: vendors and their mail contacts, the
/// outsourcing-type dictionary with its vendor mapping, and the default internal copy list. Nothing here is
/// shared with the Robot or ODM/OEMA directories. See 外协清单契约 → 外发类型字典与防呆 / 发送.
/// </summary>
public sealed class OutsourceSettingsService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumContactsPerVendor = 20;
    internal const int MaximumDefaultCc = 50;
    private const int OptionLimit = 20;

    // ---- vendors and contacts ----

    public async Task<OutsourceVendorResponse[]> VendorsAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        var vendors = await context.OutsourceVendors.AsNoTracking()
            .OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        var contacts = (await context.OutsourceVendorContacts.AsNoTracking()
                .OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct))
            .ToLookup(x => x.VendorId);
        return vendors.Select(vendor => Json(vendor, contacts[vendor.Id])).ToArray();
    }

    public async Task<OutsourceVendorResponse> CreateVendorAsync(CurrentUser actor, OutsourceVendorUpsert request, CancellationToken ct)
    {
        var input = NormalizeVendor(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var vendor = new OutsourceVendor
        {
            Name = input.Name, Remark = input.Remark, SortNo = input.SortNo, Status = AccountStatuses.Active, CreatedBy = actor.Id,
        };
        context.OutsourceVendors.Add(vendor);
        await SaveUniqueAsync(context, "已有同名外协厂商", ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_CREATE", "outsource_vendor", vendor.Id, new
        {
            targetName = vendor.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("name", "厂商名称", null, vendor.Name),
                new AuditChange("remark", "备注", null, vendor.Remark)),
        }, ct);
        await transaction.CommitAsync(ct);
        return Json(await context.OutsourceVendors.AsNoTracking().SingleAsync(x => x.Id == vendor.Id, ct), []);
    }

    public async Task<OutsourceVendorResponse> UpdateVendorAsync(CurrentUser actor, ulong id, OutsourceVendorUpsert request, CancellationToken ct)
    {
        var input = NormalizeVendor(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockVendorAsync(context, id, ct);
        if (await context.OutsourceVendors.AnyAsync(x => x.Name == input.Name && x.Id != id, ct))
            throw ApiException.Conflict("已有同名外协厂商");
        try
        {
            await context.OutsourceVendors.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, input.Name).SetProperty(x => x.Remark, input.Remark)
                .SetProperty(x => x.SortNo, input.SortNo), ct);
        }
        catch (MySqlException error) when (error.Number == 1062) { throw ApiException.Conflict("已有同名外协厂商"); }
        await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_UPDATE", "outsource_vendor", id, new
        {
            targetName = input.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("name", "厂商名称", before.Name, input.Name),
                new AuditChange("remark", "备注", before.Remark, input.Remark),
                new AuditChange("sortNo", "排序", before.SortNo, input.SortNo)),
        }, ct);
        var result = await VendorJsonAsync(context, id, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<OutsourceVendorResponse> SetVendorStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockVendorAsync(context, id, ct);
        if (before.Status != status)
        {
            await context.OutsourceVendors.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
            await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_STATUS", "outsource_vendor", id, new
            {
                targetName = before.Name,
                changes = AuditChange.OnlyChanged(new AuditChange("status", "厂商状态", before.Status, status)),
            }, ct);
        }
        var result = await VendorJsonAsync(context, id, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<OutsourceContactResponse> CreateContactAsync(
        CurrentUser actor, ulong vendorId, OutsourceContactUpsert request, CancellationToken ct)
    {
        var input = NormalizeContact(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var vendor = await LockVendorAsync(context, vendorId, ct);
        if (await context.OutsourceVendorContacts.CountAsync(x => x.VendorId == vendorId, ct) >= MaximumContactsPerVendor)
            throw ApiException.BadRequest($"每个外协厂商最多维护 {MaximumContactsPerVendor} 个联络邮箱");
        var contact = new OutsourceVendorContact
        {
            VendorId = vendorId, Name = input.Name, Email = input.Email, SortNo = input.SortNo,
            Status = AccountStatuses.Active, CreatedBy = actor.Id,
        };
        context.OutsourceVendorContacts.Add(contact);
        await SaveUniqueAsync(context, "该厂商已有相同邮箱", ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_CONTACT_CREATE", "outsource_vendor", vendorId, new
        {
            targetName = vendor.Name, contactId = contact.Id,
            changes = AuditChange.OnlyChanged(new AuditChange("contactName", "联络人姓名", null, contact.Name),
                new AuditChange("contactEmail", "联络人邮箱", null, contact.Email)),
        }, ct);
        await transaction.CommitAsync(ct);
        return Json(await context.OutsourceVendorContacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id, ct));
    }

    public async Task<OutsourceContactResponse> UpdateContactAsync(
        CurrentUser actor, ulong id, OutsourceContactUpsert request, CancellationToken ct)
    {
        var input = NormalizeContact(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockContactAsync(context, id, ct);
        if (await context.OutsourceVendorContacts.AnyAsync(x => x.VendorId == before.VendorId && x.Email == input.Email && x.Id != id, ct))
            throw ApiException.Conflict("该厂商已有相同邮箱");
        try
        {
            await context.OutsourceVendorContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, input.Name).SetProperty(x => x.Email, input.Email)
                .SetProperty(x => x.SortNo, input.SortNo), ct);
        }
        catch (MySqlException error) when (error.Number == 1062) { throw ApiException.Conflict("该厂商已有相同邮箱"); }
        await WriteAuditAsync(context, actor, "OUTSOURCE_CONTACT_UPDATE", "outsource_vendor", before.VendorId, new
        {
            targetName = await VendorNameAsync(context, before.VendorId, ct), contactId = id,
            changes = AuditChange.OnlyChanged(new AuditChange("contactName", "联络人姓名", before.Name, input.Name),
                new AuditChange("contactEmail", "联络人邮箱", before.Email, input.Email),
                new AuditChange("contactSortNo", "联络人排序", before.SortNo, input.SortNo)),
        }, ct);
        var result = Json(await context.OutsourceVendorContacts.AsNoTracking().SingleAsync(x => x.Id == id, ct));
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<OutsourceContactResponse> SetContactStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var before = await LockContactAsync(context, id, ct);
        if (before.Status != status)
        {
            await context.OutsourceVendorContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
            await WriteAuditAsync(context, actor, "OUTSOURCE_CONTACT_STATUS", "outsource_vendor", before.VendorId, new
            {
                targetName = await VendorNameAsync(context, before.VendorId, ct), contactId = id,
                changes = AuditChange.OnlyChanged(new AuditChange("contactStatus", $"联络邮箱「{before.Email}」状态", before.Status, status)),
            }, ct);
        }
        var result = Json(await context.OutsourceVendorContacts.AsNoTracking().SingleAsync(x => x.Id == id, ct));
        await transaction.CommitAsync(ct);
        return result;
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
        var vendors = await context.OutsourceVendors.AsNoTracking().Where(x => vendorIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name }).ToArrayAsync(ct);
        if (vendors.Length != vendorIds.Length) throw ApiException.BadRequest("所选外协厂商不存在");
        var before = await context.OutsourceTypeVendors.Where(x => x.TypeId == id).Select(x => x.VendorId).ToArrayAsync(ct);
        var names = await context.OutsourceVendors.AsNoTracking().Where(x => before.Contains(x.Id) || vendorIds.Contains(x.Id))
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

    /// <summary>Active internal accounts with an email address, for picking copy recipients.</summary>
    public async Task<OutsourceCcUserResponse[]> CcUserOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        var term = keyword?.Trim() ?? "";
        var query = context.Users.AsNoTracking().Where(x => x.UserType == UserTypes.Internal
            && x.Status == AccountStatuses.Active && x.Email != "");
        if (term.Length > 0) query = query.Where(x => x.RealName.Contains(term) || x.EmployeeNo.Contains(term) || x.Email.Contains(term));
        return await query.OrderBy(x => x.EmployeeNo).Take(OptionLimit)
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

    private static async Task<OutsourceVendor> LockVendorAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.OutsourceVendors.FromSqlInterpolated($"SELECT * FROM outsource_vendors WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static async Task<OutsourceVendorContact> LockContactAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.OutsourceVendorContacts.FromSqlInterpolated($"SELECT * FROM outsource_vendor_contacts WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static async Task<OutsourceType> LockTypeAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.OutsourceTypes.FromSqlInterpolated($"SELECT * FROM outsource_types WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static Task<string> VendorNameAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        context.OutsourceVendors.Where(x => x.Id == id).Select(x => x.Name).SingleAsync(ct);

    private static async Task<OutsourceVendorResponse> VendorJsonAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var vendor = await context.OutsourceVendors.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var contacts = await context.OutsourceVendorContacts.AsNoTracking().Where(x => x.VendorId == id)
            .OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        return Json(vendor, contacts);
    }

    private static async Task<OutsourceTypeResponse> TypeJsonAsync(YfDbContext context, ulong id, CancellationToken ct)
    {
        var type = await context.OutsourceTypes.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var vendorIds = await context.OutsourceTypeVendors.AsNoTracking().Where(x => x.TypeId == id).Select(x => x.VendorId).ToArrayAsync(ct);
        return Json(type, vendorIds);
    }

    private static OutsourceVendorResponse Json(OutsourceVendor vendor, IEnumerable<OutsourceVendorContact> contacts) =>
        new(vendor.Id, vendor.Name, vendor.Remark, vendor.Status, vendor.SortNo, contacts.Select(Json).ToArray());

    private static OutsourceContactResponse Json(OutsourceVendorContact contact) =>
        new(contact.Id, contact.VendorId, contact.Name, contact.Email, contact.Status, contact.SortNo);

    private static OutsourceTypeResponse Json(OutsourceType type, IEnumerable<ulong> vendorIds) =>
        new(type.Id, type.Code, type.Name, type.Status, type.SortNo, vendorIds.Order().ToArray());

    internal static string Label(string code, string name) => $"{code}【{name}】";

    private static int SortNo(int? value) => value is null or (>= 0 and <= 9999)
        ? value ?? 0 : throw ApiException.BadRequest("排序需为 0~9999");

    private static (string Name, string? Remark, int SortNo) NormalizeVendor(OutsourceVendorUpsert request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.EnumerateRunes().Count() > 64) throw ApiException.BadRequest("厂商名称需为 1~64 个字符");
        var remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim();
        if (remark is not null && remark.EnumerateRunes().Count() > 255) throw ApiException.BadRequest("备注最多 255 个字符");
        return (name, remark, SortNo(request.SortNo));
    }

    private static (string? Name, string Email, int SortNo) NormalizeContact(OutsourceContactUpsert request)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (name is not null && name.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("联络人姓名最多 32 个字符");
        AdminValidation.Email(request.Email);
        return (name, request.Email!.Trim(), SortNo(request.SortNo));
    }

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
