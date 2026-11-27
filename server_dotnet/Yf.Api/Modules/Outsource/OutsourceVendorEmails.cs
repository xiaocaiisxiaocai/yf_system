using System.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Outsource;

/// <summary>One address on a vendor's outsourcing mail list; the vendor is an ODM/OEMA company.</summary>
public sealed record OutsourceVendorEmailResponse(ulong Id, ulong VendorId, string VendorName, string Email, DateTime UpdatedAt);

public sealed record OutsourceVendorEmailCreate(ulong? VendorId, string? Email);

public sealed record OutsourceVendorEmailUpdate(string? Email);

public sealed record OutsourceVendorEmailImportRow(int RowNumber, string? VendorName, string? Email);

public sealed record OutsourceVendorEmailImportRequest(OutsourceVendorEmailImportRow[]? Rows);

/// <summary>
/// The addresses that receive each vendor's outsourcing-list mail (外协清单契约 → 外协邮箱, 2026-11-20). Vendors are the
/// ODM/OEMA companies, the addresses are kept here, apart from the ODM/OEMA contacts, and maintained on the outsourcing
/// settings page with <c>outsource:settings</c> (reading needs it too). Addresses are deleted outright: every mail record
/// keeps its own recipients.
/// </summary>
public sealed class OutsourceVendorEmailService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumPerVendor = 50;
    internal const int MaximumImportRows = 500;

    public async Task<OutsourceVendorEmailResponse[]> ListAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, ct, OutsourcePermissions.Settings);
        return await context.OutsourceVendorEmails.AsNoTracking()
            .Join(context.OemCompanies, email => email.VendorId, vendor => vendor.Id, (email, vendor) => new { email, vendor })
            .OrderBy(x => x.vendor.Name).ThenBy(x => x.email.Id)
            .Select(x => new OutsourceVendorEmailResponse(x.email.Id, x.vendor.Id, x.vendor.Name, x.email.Email, x.email.UpdatedAt))
            .ToArrayAsync(ct);
    }

    public async Task<OutsourceVendorEmailResponse> CreateAsync(CurrentUser actor, OutsourceVendorEmailCreate request, CancellationToken ct)
    {
        var email = OemValidation.Email(request.Email);
        var vendorId = request.VendorId ?? throw ApiException.BadRequest("请选择厂商");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.Settings);
        var vendor = await LockVendorAsync(context, vendorId, ct);
        if (vendor.Status != AccountStatuses.Active) throw ApiException.BadRequest("厂商已停用，不能新增外协邮箱");
        if (await context.OutsourceVendorEmails.CountAsync(x => x.VendorId == vendorId, ct) >= MaximumPerVendor)
            throw ApiException.BadRequest($"每个厂商最多维护 {MaximumPerVendor} 个外协邮箱");
        if (await context.OutsourceVendorEmails.AnyAsync(x => x.VendorId == vendorId && x.Email == email, ct))
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var row = new OutsourceVendorEmail { VendorId = vendorId, Email = email, CreatedBy = actor.Id, CreatedAt = now, UpdatedAt = now };
        context.OutsourceVendorEmails.Add(row);
        await SaveAsync(context, ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_EMAIL_CREATE", row.Id, new { targetName = vendor.Name, vendor = vendor.Name, email }, ct);
        await transaction.CommitAsync(ct);
        return Json(row, vendor.Name);
    }

    public async Task<OutsourceVendorEmailResponse> UpdateAsync(CurrentUser actor, ulong id, OutsourceVendorEmailUpdate request, CancellationToken ct)
    {
        var email = OemValidation.Email(request.Email);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.Settings);
        var row = await LockAsync(context, id, ct);
        var vendorName = await context.OemCompanies.Where(x => x.Id == row.VendorId).Select(x => x.Name).SingleAsync(ct);
        if (await context.OutsourceVendorEmails.AnyAsync(x => x.VendorId == row.VendorId && x.Id != id && x.Email == email, ct))
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        var before = row.Email;
        row.Email = email;
        row.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
        await SaveAsync(context, ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_EMAIL_UPDATE", id, new
        {
            targetName = vendorName, vendor = vendorName,
            changes = AuditChange.OnlyChanged(new AuditChange("email", "邮箱", before, email)),
        }, ct);
        await transaction.CommitAsync(ct);
        return Json(row, vendorName);
    }

    public async Task<EmptyResponse> DeleteAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.Settings);
        var row = await LockAsync(context, id, ct);
        var vendorName = await context.OemCompanies.Where(x => x.Id == row.VendorId).Select(x => x.Name).SingleAsync(ct);
        await context.OutsourceVendorEmails.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_EMAIL_DELETE", id, new { targetName = vendorName, vendor = vendorName, row.Email }, ct);
        await transaction.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    /// <summary>
    /// Create-only import (vendor name, email): the vendor must be an existing, enabled ODM/OEMA company; a repeated address
    /// in the file, one the vendor already has, or a vendor that would pass its limit never overwrites anything. All ready
    /// rows are stored in one transaction, planned again against the locked vendors.
    /// </summary>
    public async Task<AdminImportResponse> ImportAsync(CurrentUser actor, OutsourceVendorEmailImportRequest request, CancellationToken ct)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumImportRows || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumImportRows} 行");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, ct, OutsourcePermissions.Settings);
        var requestedNames = request.Rows
            .Select(row => row.VendorName?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateIds = (await context.OemCompanies.AsNoTracking().Select(x => new { x.Id, x.Name }).ToArrayAsync(ct))
            .Where(x => requestedNames.Contains(x.Name.Trim()))
            .Select(x => x.Id)
            .Distinct()
            .Order()
            .ToArray();
        // Lock every touched vendor in ascending id order before reading live status and existing addresses.
        var vendors = new Dictionary<string, OemCompany>(StringComparer.OrdinalIgnoreCase);
        var lockedVendorIds = new List<ulong>(candidateIds.Length);
        foreach (var vendorId in candidateIds)
        {
            var locked = await TryLockVendorAsync(context, vendorId, ct);
            if (locked is null) continue;
            var name = locked.Name.Trim();
            if (requestedNames.Contains(name) && !vendors.ContainsKey(name))
            {
                vendors[name] = locked;
                lockedVendorIds.Add(locked.Id);
            }
        }
        var held = new Dictionary<ulong, HashSet<string>>();
        if (lockedVendorIds.Count > 0)
        {
            var lockedIds = lockedVendorIds.ToArray();
            var existing = await context.OutsourceVendorEmails.AsNoTracking()
                .Where(x => lockedIds.Contains(x.VendorId))
                .Select(x => new { x.VendorId, x.Email })
                .ToArrayAsync(ct);
            foreach (var row in existing)
            {
                if (!held.TryGetValue(row.VendorId, out var set))
                    held[row.VendorId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(row.Email);
            }
        }
        int Held(ulong vendorId) => held.TryGetValue(vendorId, out var set) ? set.Count : 0;

        var repeated = request.Rows.Select((row, index) => (key: $"{row.VendorName?.Trim()}\u0000{row.Email?.Trim()}", index, ok: !string.IsNullOrWhiteSpace(row.Email)))
            .Where(x => x.ok).GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(x => x.index)).ToHashSet();
        var results = new List<AdminImportRowResult>(request.Rows.Length);
        var ready = new List<(ulong VendorId, string VendorName, string Email)>();
        for (var index = 0; index < request.Rows.Length; index++)
        {
            var row = request.Rows[index];
            var rowNumber = row.RowNumber >= 2 && row.RowNumber <= MaximumImportRows + 1 ? row.RowNumber : index + 2;
            var vendorText = row.VendorName?.Trim() ?? "";
            var emailText = row.Email?.Trim() ?? "";
            var key = emailText.Length > 0 ? emailText : $"第 {rowNumber} 行";
            if (!vendors.TryGetValue(vendorText, out var vendor))
            { results.Add(new(rowNumber, key, "FAILED", "ODM/OEMA 厂商不存在，请先建立厂商资料")); continue; }
            if (vendor.Status != AccountStatuses.Active)
            { results.Add(new(rowNumber, key, "FAILED", "厂商已停用，不能新增外协邮箱")); continue; }
            string email;
            try { email = OemValidation.Email(emailText); }
            catch (ApiException error) { results.Add(new(rowNumber, key, "FAILED", error.Message)); continue; }
            if (repeated.Contains(index))
            { results.Add(new(rowNumber, key, "SKIPPED", "文件内该厂商的外协邮箱重复，本重复组未导入")); continue; }
            if (held.TryGetValue(vendor.Id, out var current) && current.Contains(email))
            { results.Add(new(rowNumber, key, "SKIPPED", "该厂商已有这个外协邮箱，未覆盖现有资料")); continue; }
            if (Held(vendor.Id) >= MaximumPerVendor)
            { results.Add(new(rowNumber, key, "FAILED", $"每个厂商最多维护 {MaximumPerVendor} 个外协邮箱")); continue; }
            if (!held.TryGetValue(vendor.Id, out current)) held[vendor.Id] = current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            current.Add(email);
            ready.Add((vendor.Id, vendor.Name, email));
            results.Add(new(rowNumber, key, "CREATED", null));
        }
        if (ready.Count > 0)
        {
            var now = await DbClock.UtcNowAsync(context, ct, 3);
            context.OutsourceVendorEmails.AddRange(ready.Select(x => new OutsourceVendorEmail
            { VendorId = x.VendorId, Email = x.Email, CreatedBy = actor.Id, CreatedAt = now, UpdatedAt = now }));
            try { await context.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                throw ApiException.Conflict("部分外协邮箱刚刚被他人添加，请重新选择文件导入");
            }
            await WriteAuditAsync(context, actor, "OUTSOURCE_VENDOR_EMAIL_IMPORT", null, new
            {
                targetName = "外协邮箱导入", count = ready.Count,
                vendors = ready.Select(x => x.VendorName).Distinct().Take(50).ToArray(),
            }, ct);
        }
        await transaction.CommitAsync(ct);
        return new(results.Count(x => x.Status == "CREATED"), results.Count(x => x.Status == "SKIPPED"),
            results.Count(x => x.Status == "FAILED"), results);
    }

    private static Task<OemCompany?> TryLockVendorAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        context.OemCompanies.FromSqlInterpolated($"SELECT * FROM oem_companies WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct);

    private static async Task<OemCompany> LockVendorAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await TryLockVendorAsync(context, id, ct) ?? throw ApiException.BadRequest("厂商不存在");

    private static async Task<OutsourceVendorEmail> LockAsync(YfDbContext context, ulong id, CancellationToken ct) =>
        await context.OutsourceVendorEmails.FromSqlInterpolated($"SELECT * FROM outsource_vendor_emails WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("外协邮箱不存在或已被删除");

    private static async Task SaveAsync(YfDbContext context, CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        }
    }

    private Task WriteAuditAsync(YfDbContext context, CurrentUser actor, string action, ulong? targetId, object detail, CancellationToken ct) =>
        audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            action, "outsource_vendor_email", targetId, detail, null, ct);

    private static OutsourceVendorEmailResponse Json(OutsourceVendorEmail row, string vendorName) =>
        new(row.Id, row.VendorId, vendorName, row.Email, row.UpdatedAt);
}
