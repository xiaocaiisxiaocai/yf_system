using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Modules.Admin;

/// <summary>
/// Internally maintained contacts behind a supplier's (possibly shared) login. Contacts are never deleted:
/// signatures and Robot-owner selections reference them. See 厂商联络人与签名契约.
/// </summary>
public sealed class SupplierContactService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumPerSupplier = 100;

    public async Task<SupplierContactResponse[]> ListAsync(CurrentUser actor, ulong supplierId, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, "supplier:manage", ct);
        if (!await context.Suppliers.AnyAsync(x => x.Id == supplierId, ct)) throw ApiException.NotFound();
        return await Ordered(context.SupplierContacts.AsNoTracking().Where(x => x.SupplierId == supplierId))
            .Select(x => Json(x)).ToArrayAsync(ct);
    }

    public async Task<SupplierContactResponse> CreateAsync(
        CurrentUser actor, ulong supplierId, SupplierContactUpsert request, CancellationToken ct)
    {
        var input = Normalize(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAuthorization.RequireAsync(context, actor, "supplier:manage", ct);
        var supplier = await context.Suppliers.FromSqlInterpolated($"SELECT * FROM suppliers WHERE id={supplierId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (await context.SupplierContacts.CountAsync(x => x.SupplierId == supplierId, ct) >= MaximumPerSupplier)
            throw ApiException.BadRequest($"每个供应商最多维护 {MaximumPerSupplier} 位联络人");
        var contact = new SupplierContact
        {
            SupplierId = supplierId, Name = input.Name, Email = input.Email, SortNo = input.SortNo,
            Status = AccountStatuses.Active, CreatedBy = actor.Id,
        };
        context.SupplierContacts.Add(contact);
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("该供应商下已有同名联络人"); }
        var created = await context.SupplierContacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "SUPPLIER_CONTACT_CREATE", "supplier", supplierId, new
            {
                contactId = created.Id, created.Name, created.Email, targetName = supplier.Name,
                changes = AuditChange.OnlyChanged(new AuditChange("contactName", "联络人姓名", null, created.Name),
                    new AuditChange("contactEmail", "联络人邮箱", null, created.Email)),
            }, null, ct);
        await transaction.CommitAsync(ct);
        return Json(created);
    }

    public async Task<SupplierContactResponse> UpdateAsync(
        CurrentUser actor, ulong id, SupplierContactUpsert request, CancellationToken ct)
    {
        var input = Normalize(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAuthorization.RequireAsync(context, actor, "supplier:manage", ct);
        var before = await LockAsync(context, id, ct);
        if (await context.SupplierContacts.AnyAsync(x => x.SupplierId == before.SupplierId && x.Name == input.Name && x.Id != id, ct))
            throw ApiException.Conflict("该供应商下已有同名联络人");
        try
        {
            await context.SupplierContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, input.Name).SetProperty(x => x.Email, input.Email)
                .SetProperty(x => x.SortNo, input.SortNo), ct);
        }
        catch (MySqlException error) when (error.Number == 1062)
        { throw ApiException.Conflict("该供应商下已有同名联络人"); }
        if (before.Name != input.Name) await RobotOwners.RefreshSnapshotsAsync(context, id, ct);
        var updated = await context.SupplierContacts.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "SUPPLIER_CONTACT_UPDATE", "supplier", before.SupplierId, new
            {
                contactId = id, updated.Name, targetName = await SupplierNameAsync(context, before.SupplierId, ct),
                changes = AuditChange.OnlyChanged(new AuditChange("contactName", "联络人姓名", before.Name, updated.Name),
                    new AuditChange("contactEmail", "联络人邮箱", before.Email, updated.Email),
                    new AuditChange("contactSortNo", "联络人排序", before.SortNo, updated.SortNo)),
            }, null, ct);
        await transaction.CommitAsync(ct);
        return Json(updated);
    }

    public async Task<SupplierContactResponse> SetStatusAsync(CurrentUser actor, ulong id, string status, CancellationToken ct)
    {
        status = AdminValidation.Status(status);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await ManagementAuthorization.RequireAsync(context, actor, "supplier:manage", ct);
        var before = await LockAsync(context, id, ct);
        if (before.Status == status) { await transaction.CommitAsync(ct); return Json(before); }
        await context.SupplierContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "SUPPLIER_CONTACT_STATUS", "supplier", before.SupplierId, new
            {
                contactId = id, before.Name, oldStatus = before.Status, newStatus = status,
                targetName = await SupplierNameAsync(context, before.SupplierId, ct),
                changes = AuditChange.OnlyChanged(new AuditChange("contactStatus", $"联络人「{before.Name}」状态", before.Status, status)),
            }, null, ct);
        var result = Json(await context.SupplierContacts.AsNoTracking().SingleAsync(x => x.Id == id, ct));
        await transaction.CommitAsync(ct);
        return result;
    }

    internal static IQueryable<SupplierContact> Ordered(IQueryable<SupplierContact> query) =>
        query.OrderBy(x => x.SortNo).ThenBy(x => x.Id);

    internal static SupplierContactResponse Json(SupplierContact contact) =>
        new(contact.Id, contact.SupplierId, contact.Name, contact.Email, contact.Status, contact.SortNo, contact.CreatedAt);

    private static async Task<SupplierContact> LockAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.SupplierContacts.FromSqlInterpolated($"SELECT * FROM supplier_contacts WHERE id={id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static Task<string> SupplierNameAsync(YfDbContext context, ulong supplierId, CancellationToken ct) =>
        context.Suppliers.Where(x => x.Id == supplierId).Select(x => x.Name).SingleAsync(ct);

    private static (string Name, string Email, int SortNo) Normalize(SupplierContactUpsert request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("联络人姓名需为 1~32 个字符");
        AdminValidation.Email(request.Email);
        var sortNo = request.SortNo ?? 0;
        if (sortNo is < 0 or > 9999) throw ApiException.BadRequest("排序需为 0~9999");
        return (name, request.Email!.Trim(), sortNo);
    }
}
