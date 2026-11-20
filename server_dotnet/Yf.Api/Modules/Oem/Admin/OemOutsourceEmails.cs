using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Identity;

namespace Yf.Api.Modules.Oem.Admin;

public sealed record OemOutsourceEmailUpsert(string? Email);

public sealed record OemOutsourceEmailImportRow(int RowNumber, [property: JsonRequired] string CompanyName, [property: JsonRequired] string Email);

public sealed record OemOutsourceEmailImportRequest([property: JsonRequired] OemOutsourceEmailImportRow[] Rows);

public sealed record OemOutsourceEmailResponse(ulong Id, ulong CompanyId, string Email, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// The addresses that receive a vendor's outsourcing-list mail (外协清单契约 → 外协邮箱, 2026-11-20). They are only
/// e-mail addresses, kept apart from the ODM/OEMA contacts and maintained on the same contact drawer under its own tab,
/// with the same permission as the contacts (<c>oem:company_manage</c>). Addresses are deleted outright: every mail
/// record keeps its own recipients.
/// </summary>
public sealed class OemOutsourceEmailService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit)
{
    internal const int MaximumPerCompany = 50;

    public async Task<OemOutsourceEmailResponse[]> ListAsync(OemActor actor, ulong companyId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == companyId, ct)) throw ApiException.NotFound();
        return await uow.Db.OutsourceVendorEmails.AsNoTracking().Where(x => x.VendorId == companyId)
            .OrderBy(x => x.Id).Select(x => Json(x)).ToArrayAsync(ct);
    }

    public async Task<OemOutsourceEmailResponse> CreateAsync(OemActor actor, ulong companyId, OemOutsourceEmailUpsert request, CancellationToken ct)
    {
        var email = Validate(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var company = await OemLocks.ForUpdate<OemCompany>(uow.Db, companyId).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (company.Status != OemStatus.Active) throw ApiException.BadRequest("厂商已被停用，不能新增外协邮箱");
        if (await uow.Db.OutsourceVendorEmails.LongCountAsync(x => x.VendorId == companyId, ct) >= MaximumPerCompany)
            throw ApiException.BadRequest($"每个厂商最多维护 {MaximumPerCompany} 个外协邮箱");
        if (await uow.Db.OutsourceVendorEmails.AnyAsync(x => x.VendorId == companyId && x.Email == email, ct))
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        var row = new OutsourceVendorEmail
        {
            VendorId = companyId, Email = email, CreatedBy = current.User.Id, CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OutsourceVendorEmails.Add(row);
        await SaveAsync(uow, ct);
        await audit.WriteAsync(uow, current, "OEM_OUTSOURCE_EMAIL_CREATE", "outsource_vendor_email", row.Id,
            new { companyId, companyName = company.Name, row.Email }, ct);
        await uow.CommitAsync(ct);
        return Json(row);
    }

    /// <summary>
    /// Create-only import for one company: a repeated address in the file or one the company already has is skipped,
    /// never overwritten. Every row goes through <see cref="CreateAsync"/>, so the same checks and audit apply.
    /// </summary>
    public async Task<OemImportResponse> ImportAsync(OemActor actor, ulong companyId, OemOutsourceEmailImportRequest request, CancellationToken ct)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumPerCompany || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumPerCompany} 行");

        string companyName;
        HashSet<string> existing;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
            companyName = await uow.Db.OemCompanies.AsNoTracking().Where(company => company.Id == companyId)
                .Select(company => company.Name).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
            existing = (await uow.Db.OutsourceVendorEmails.AsNoTracking().Where(x => x.VendorId == companyId)
                .Select(x => x.Email).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var repeated = request.Rows.Select((row, index) => (email: row.Email?.Trim() ?? "", index))
            .Where(item => item.email.Length > 0).GroupBy(item => item.email, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.index)).ToHashSet();
        var results = new List<OemImportRowResult>(request.Rows.Length);
        for (var index = 0; index < request.Rows.Length; index++)
        {
            var row = request.Rows[index];
            var rowNumber = row.RowNumber >= 2 && row.RowNumber <= MaximumPerCompany + 1 ? row.RowNumber : index + 2;
            var email = row.Email?.Trim() ?? "";
            if (!string.Equals(row.CompanyName?.Trim(), companyName, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(rowNumber, email, "FAILED", "厂商名称与导入对象不匹配"));
                continue;
            }
            if (repeated.Contains(index))
            {
                results.Add(new(rowNumber, email, "SKIPPED", "文件内外协邮箱重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(email))
            {
                results.Add(new(rowNumber, email, "SKIPPED", "该厂商已有这个外协邮箱，未覆盖现有资料"));
                continue;
            }
            try
            {
                await CreateAsync(actor, companyId, new OemOutsourceEmailUpsert(email), ct);
                existing.Add(email);
                results.Add(new(rowNumber, email, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                results.Add(new(rowNumber, email, error.Status == 409 ? "SKIPPED" : "FAILED", error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, email, "FAILED", "权限已变化，批次已停止"));
                for (var rest = index + 1; rest < request.Rows.Length; rest++)
                    results.Add(new(request.Rows[rest].RowNumber >= 2 ? request.Rows[rest].RowNumber : rest + 2, request.Rows[rest].Email ?? "", "FAILED", "未执行"));
                break;
            }
        }
        return new(results.Count(row => row.Status == "CREATED"), results.Count(row => row.Status == "SKIPPED"),
            results.Count(row => row.Status == "FAILED"), results);
    }

    public async Task<OemOutsourceEmailResponse> UpdateAsync(OemActor actor, ulong id, OemOutsourceEmailUpsert request, CancellationToken ct)
    {
        var email = Validate(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var row = await LockAsync(uow, id, ct);
        if (await uow.Db.OutsourceVendorEmails.AnyAsync(x => x.VendorId == row.VendorId && x.Id != id && x.Email == email, ct))
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        var before = row.Email;
        row.Email = email;
        row.UpdatedAt = uow.Now;
        await SaveAsync(uow, ct);
        await audit.WriteAsync(uow, current, "OEM_OUTSOURCE_EMAIL_UPDATE", "outsource_vendor_email", id,
            new { companyId = row.VendorId, changes = AuditChange.OnlyChanged(new AuditChange("email", "邮箱", before, email)) }, ct);
        await uow.CommitAsync(ct);
        return Json(row);
    }

    public async Task DeleteAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var row = await LockAsync(uow, id, ct);
        await uow.Db.OutsourceVendorEmails.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_OUTSOURCE_EMAIL_DELETE", "outsource_vendor_email", id,
            new { companyId = row.VendorId, row.Email }, ct);
        await uow.CommitAsync(ct);
    }

    /// <summary>Not an <c>oem_*</c> table, so <see cref="OemLocks"/> does not cover it; written under the management gate.</summary>
    private static async Task<OutsourceVendorEmail> LockAsync(OemUnitOfWork uow, ulong id, CancellationToken ct) =>
        await uow.Db.OutsourceVendorEmails.FromSqlInterpolated($"SELECT * FROM outsource_vendor_emails WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();

    private static string Validate(OemOutsourceEmailUpsert request) =>
        OemValidation.OptionalEmail(request.Email) ?? throw ApiException.BadRequest("请输入邮箱");

    private static async Task SaveAsync(OemUnitOfWork uow, CancellationToken ct)
    {
        try { await uow.Db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            throw ApiException.Conflict("该厂商已有这个外协邮箱");
        }
    }

    private static OemOutsourceEmailResponse Json(OutsourceVendorEmail row) =>
        new(row.Id, row.VendorId, row.Email, row.CreatedAt, row.UpdatedAt);
}
