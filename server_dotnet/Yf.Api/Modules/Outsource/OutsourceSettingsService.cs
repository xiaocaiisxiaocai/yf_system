using System.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Modules.Outsource;

/// <summary>
/// Three permissions since 2026-11-19 (外协清单契约 → 权限): every internal account sees the list without one; who fills
/// a column in is decided per column on the field settings page.
/// </summary>
internal static class OutsourcePermissions
{
    /// <summary>
    /// Manages records: import, delete, complete/reopen and the manual IPMS sync; also fills in every column whose
    /// editor setting is ALL. Columns limited to roles or people need no permission.
    /// </summary>
    internal const string Manage = "outsource:manage";
    /// <summary>Previews and sends vendor mails, queues failed ones again, sees the mail records and the send queue.</summary>
    internal const string Send = "outsource:send";
    /// <summary>The settings pages, field settings included, and the IPMS data page.</summary>
    internal const string Settings = "outsource:settings";
}

/// <summary>
/// Purchasing-maintained reference data of the outsourcing list: the outsourcing-type dictionary with its vendor
/// mapping and the default internal copy list. Vendors and their mail contacts are the ODM/OEMA companies and
/// contacts, maintained in the ODM/OEMA directory and only read here. See 外协清单契约 → 外发类型字典与防呆 / 发送.
/// </summary>
public sealed class OutsourceSettingsService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit, IMesSource? mes = null)
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

    // ---- customer short names (客户全称 → 简称) ----

    internal const int CustomerPageSize = 500;
    internal const int MaximumCustomerPages = 200;

    public async Task<OutsourceCustomerAliasResponse[]> CustomerAliasesAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        return await AliasListAsync(context, ct);
    }

    /// <summary>
    /// Reads the whole IPMS 工单查询 list and refreshes the table: every full name gets its most frequent short name that is
    /// not just the full name again (IPMS often repeats the full name there). Short names set by hand stay; nothing is deleted.
    /// </summary>
    public async Task<OutsourceCustomerSyncResponse> SyncCustomerAliasesAsync(CurrentUser actor, CancellationToken ct)
    {
        if (mes is null) throw ApiException.Unavailable("当前环境未连接 IPMS");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using (var check = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await AuthorizeWriteAsync(context, actor, ct);
            await check.CommitAsync(ct);
        }
        await context.Database.CloseConnectionAsync();

        // The fetch talks to IPMS for a while; no database connection or transaction is held meanwhile.
        var rows = new List<MesCustomerRow>();
        try
        {
            for (var page = 1; page <= MaximumCustomerPages; page++)
            {
                var result = await mes.FetchCustomersPageAsync(page, CustomerPageSize, ct);
                rows.AddRange(result.Rows);
                if (result.Rows.Count < CustomerPageSize || rows.Count >= result.Total) break;
            }
        }
        catch (IpmsUnavailableException error) { throw ApiException.Unavailable(error.Message); }
        var aliases = ShortNames(rows);
        if (aliases.Count == 0) throw ApiException.Unavailable("IPMS 工单查询没有返回任何客户简称");

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var existing = (await context.OutsourceCustomerAliases.ToListAsync(ct))
            .GroupBy(x => CustomerKey(x.FullName)).ToDictionary(x => x.Key, x => x.First());
        int added = 0, updated = 0, keptManual = 0;
        foreach (var (fullName, shortName) in aliases)
        {
            if (!existing.TryGetValue(CustomerKey(fullName), out var row))
            {
                context.OutsourceCustomerAliases.Add(new OutsourceCustomerAlias
                    { FullName = fullName, ShortName = shortName, UpdatedBy = actor.Id, UpdatedAt = now });
                added++;
            }
            else if (row.Manual) { if (row.ShortName != shortName) keptManual++; }
            else if (row.ShortName != shortName)
            {
                row.ShortName = shortName; row.UpdatedBy = actor.Id; row.UpdatedAt = now;
                updated++;
            }
        }
        await SaveUniqueAsync(context, "另一次同步刚刚完成，请刷新后再试", ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_CUSTOMER_SYNC", "outsource_customer_alias", null, new
        {
            targetName = "客户简称同步", rows = rows.Count, customers = aliases.Count, added, updated, keptManual,
        }, ct);
        await transaction.CommitAsync(ct);
        return new(rows.Count, aliases.Count, added, updated, keptManual, await AliasListAsync(context, ct));
    }

    /// <summary>Adds a pair by hand for a customer the IPMS sync gives no short name; later syncs keep it.</summary>
    public async Task<OutsourceCustomerAliasResponse> CreateCustomerAliasAsync(
        CurrentUser actor, OutsourceCustomerAliasCreate request, CancellationToken ct)
    {
        var fullName = request.FullName?.Trim() ?? "";
        var shortName = request.ShortName?.Trim() ?? "";
        if (fullName.Length == 0 || fullName.EnumerateRunes().Count() > 128) throw ApiException.BadRequest("客户全称需为 1~128 个字符");
        if (shortName.Length == 0 || shortName.EnumerateRunes().Count() > 64) throw ApiException.BadRequest("客户简称需为 1~64 个字符");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var key = CustomerKey(fullName);
        var names = await context.OutsourceCustomerAliases.AsNoTracking().Select(x => x.FullName).ToArrayAsync(ct);
        if (names.Any(name => CustomerKey(name) == key)) throw ApiException.Conflict("该客户已有简称，请直接编辑");
        var row = new OutsourceCustomerAlias
        {
            FullName = fullName, ShortName = shortName, Manual = true, UpdatedBy = actor.Id,
            UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3),
        };
        context.OutsourceCustomerAliases.Add(row);
        await SaveUniqueAsync(context, "该客户已有简称，请直接编辑", ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_CUSTOMER_ALIAS", "outsource_customer_alias", row.Id, new
        {
            targetName = fullName,
            changes = new[] { new AuditChange("shortName", "客户简称", null, shortName) },
        }, ct);
        await transaction.CommitAsync(ct);
        return new(row.Id, row.FullName, row.ShortName, row.Manual, row.UpdatedAt);
    }

    /// <summary>Removes a pair added or edited by hand; pairs that came from IPMS would only come back with the next sync.</summary>
    public async Task<EmptyResponse> DeleteCustomerAliasAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var row = await context.OutsourceCustomerAliases
            .FromSqlInterpolated($"SELECT * FROM outsource_customer_aliases WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (!row.Manual) throw ApiException.Conflict("只能删除手动设定的简称；IPMS 同步的会在下次同步时再出现");
        await context.OutsourceCustomerAliases.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_CUSTOMER_ALIAS", "outsource_customer_alias", id, new
        {
            targetName = row.FullName,
            changes = new[] { new AuditChange("shortName", "客户简称", row.ShortName, null) },
        }, ct);
        await transaction.CommitAsync(ct);
        return new();
    }

    /// <summary>
    /// Customer full names in the IPMS data (MES mirror) or the outsourcing list without a short name, most work orders first,
    /// so purchasing can add the ones IPMS does not provide.
    /// </summary>
    public async Task<OutsourceMissingCustomerResponse[]> MissingCustomerAliasesAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, ct);
        var known = (await context.OutsourceCustomerAliases.AsNoTracking().Select(x => x.FullName).ToArrayAsync(ct))
            .Select(CustomerKey).ToHashSet(StringComparer.Ordinal);
        var mes = await context.MesWorkOrders.AsNoTracking().Where(x => x.CustomerName != null && x.CustomerName != "")
            .GroupBy(x => x.CustomerName!).Select(x => new { Name = x.Key, Count = x.Count() }).ToArrayAsync(ct);
        var items = await context.OutsourceItems.AsNoTracking().Where(x => x.Customer != null && x.Customer != "")
            .GroupBy(x => x.Customer!).Select(x => new { Name = x.Key, Count = x.Count() }).ToArrayAsync(ct);
        return mes.Select(x => (x.Name, Mes: x.Count, Items: 0)).Concat(items.Select(x => (x.Name, Mes: 0, Items: x.Count)))
            .Where(x => !known.Contains(CustomerKey(x.Name)))
            .GroupBy(x => CustomerKey(x.Name))
            .Select(x => new OutsourceMissingCustomerResponse(x.First().Name.Trim(), x.Sum(y => y.Mes), x.Sum(y => y.Items)))
            .OrderByDescending(x => x.MesWorkOrders + x.OutsourceItems).ThenBy(x => x.FullName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Sets a short name by hand (kept by later syncs); an empty one hands the row back to the next sync.</summary>
    public async Task<OutsourceCustomerAliasResponse> SetCustomerAliasAsync(
        CurrentUser actor, ulong id, OutsourceCustomerAliasRequest request, CancellationToken ct)
    {
        var shortName = request.ShortName?.Trim() ?? "";
        if (shortName.EnumerateRunes().Count() > 64) throw ApiException.BadRequest("客户简称最多 64 个字符");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, ct);
        var row = await context.OutsourceCustomerAliases
            .FromSqlInterpolated($"SELECT * FROM outsource_customer_aliases WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        var before = (row.ShortName, row.Manual);
        if (shortName.Length > 0) { row.ShortName = shortName; row.Manual = true; }
        else row.Manual = false;
        if ((row.ShortName, row.Manual) != before)
        {
            row.UpdatedBy = actor.Id;
            row.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
            await context.SaveChangesAsync(ct);
            await WriteAuditAsync(context, actor, "OUTSOURCE_CUSTOMER_ALIAS", "outsource_customer_alias", id, new
            {
                targetName = row.FullName,
                changes = AuditChange.OnlyChanged(
                    new AuditChange("shortName", "客户简称", before.ShortName, row.ShortName),
                    new AuditChange("manual", "手动设定", before.Manual ? "是" : "否", row.Manual ? "是" : "否")),
            }, ct);
        }
        await transaction.CommitAsync(ct);
        return new(row.Id, row.FullName, row.ShortName, row.Manual, row.UpdatedAt);
    }

    private static Task<OutsourceCustomerAliasResponse[]> AliasListAsync(YfDbContext context, CancellationToken ct) =>
        context.OutsourceCustomerAliases.AsNoTracking().OrderBy(x => x.FullName)
            .Select(x => new OutsourceCustomerAliasResponse(x.Id, x.FullName, x.ShortName, x.Manual, x.UpdatedAt)).ToArrayAsync(ct);

    /// <summary>
    /// How customer full names compare, like the database's <c>utf8mb4_unicode_ci</c> does: full-width and half-width forms
    /// (IPMS writes both "（淮安）" and "（淮安)"), letter case and repeated spaces do not make another customer.
    /// </summary>
    internal static string CustomerKey(string name) =>
        string.Join(' ', name.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Full name → short name: per customer (<see cref="CustomerKey"/>), the most frequent short name that is not the full
    /// name again (ties go to the one IPMS listed first), under the first spelling of the full name IPMS listed. Names longer
    /// than the columns hold are skipped.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ShortNames(IEnumerable<MesCustomerRow> rows)
    {
        var customers = new Dictionary<string, (string FullName, List<(string Short, int Count)> Shorts)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var full = row.CustomerName?.Trim();
            var shortName = row.CustomerShortName?.Trim();
            if (string.IsNullOrEmpty(full) || full.Length > 128 || string.IsNullOrEmpty(shortName) || shortName.Length > 64
                || CustomerKey(full) == CustomerKey(shortName)) continue;
            var key = CustomerKey(full);
            if (!customers.TryGetValue(key, out var customer)) customers[key] = customer = (full, []);
            var index = customer.Shorts.FindIndex(x => x.Short == shortName);
            if (index < 0) customer.Shorts.Add((shortName, 1)); else customer.Shorts[index] = (shortName, customer.Shorts[index].Count + 1);
        }
        return customers.Values.ToDictionary(x => x.FullName,
            x => x.Shorts.Select((item, order) => (item.Short, item.Count, order))
                .OrderByDescending(x => x.Count).ThenBy(x => x.order).First().Short,
            StringComparer.Ordinal);
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

    /// <summary>Every internal account reads the list's vendor tabs, type filters and copy list.</summary>
    private static Task AuthorizeReadAsync(YfDbContext context, CurrentUser actor, CancellationToken ct) =>
        OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);

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
