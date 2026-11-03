using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Identity;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Modules.Oem.Admin;

public sealed record OemCompanyUpsert(string Name, string? Remark);
public sealed record OemStatusRequest(string Status);
public sealed record OemAccountCreate(
    [property: JsonRequired] string EmployeeNo,
    [property: JsonRequired] string Password);
public sealed record OemCompanyContactUpsert(
    [property: JsonRequired] string Name,
    string? Email,
    int? SortNo = null);
public sealed record OemAccountImportRow(
    int RowNumber,
    [property: JsonRequired] string CompanyName,
    [property: JsonRequired] string EmployeeNo,
    [property: JsonRequired] string InitialPassword);
public sealed record OemAccountImportRequest([property: JsonRequired] OemAccountImportRow[] Rows);
public sealed record OemCompanyContactImportRow(
    int RowNumber,
    [property: JsonRequired] string CompanyName,
    [property: JsonRequired] string Name,
    string? Email,
    int? SortNo = null);
public sealed record OemCompanyContactImportRequest([property: JsonRequired] OemCompanyContactImportRow[] Rows);
public sealed record OemPasswordReset(string NewPassword);
public sealed record OemCompanyImportRow(
    int RowNumber,
    [property: JsonRequired] string Name,
    string? Remark);
public sealed record OemCompanyImportRequest([property: JsonRequired] OemCompanyImportRow[] Rows);
public sealed record OemImportRowResult(int RowNumber, string Key, string Status, string? Message);
public sealed record OemImportResponse(int Created, int Skipped, int Failed, IReadOnlyList<OemImportRowResult> Rows);

/// <summary>
/// Management of OEM vendors and their login accounts. Only internal staff with the
/// matching `oem:*` permission reach this service; disabling a vendor or an account,
/// or resetting a password, revokes the affected OEM sessions in the same transaction.
/// Empty vendors and accounts may be physically deleted; records with business or
/// historical references must be disabled so their identity remains explainable.
/// An active vendor always keeps at least one active account, and a vendor cannot be
/// disabled while transfers in either direction are still in flight.
/// </summary>
public sealed class OemDirectoryService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit)
{
    internal const string LastActiveAccountMessage = "厂商至少需要保留一个启用账号；如需停止合作，请停用厂商";
    internal const int MaximumCompanyImportRows = 500;
    internal const int MaximumAccountImportRows = 100;
    internal const int MaximumContactsPerCompany = 100;

    public async Task<OemPageResponse<OemCompanyListItemResponse>> ListCompaniesAsync(OemActor actor, ulong page, uint size, ulong offset, string? keyword, string? status, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        // Account managers need the vendor list to reach the accounts they manage.
        if (actor is not InternalOemActor) throw ApiException.Forbidden("仅公司内部账号可以执行该操作");
        var current = (InternalOemActor)await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var codes = await OemAuthorizer.InternalPermissionsAsync(uow, current.User.Id, ct);
        if (!codes.Contains(OemPermissions.CompanyManage) && !codes.Contains(OemPermissions.AccountManage)) throw ApiException.Forbidden();
        var query = uow.Db.OemCompanies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = QueryValues.ContainsPattern(keyword.Trim());
            query = query.Where(company => EF.Functions.Like(company.Name, pattern, QueryValues.LikeEscape));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = OemStatus.Normalize(status);
            query = query.Where(company => company.Status == normalized);
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(company => company.Id).Page(offset, size)
            .Select(company => new OemCompanyListItemResponse(
                company.Id, company.Name, company.Remark,
                company.Status, company.CreatedAt, company.UpdatedAt,
                uow.Db.OemAccounts.LongCount(account => account.OemCompanyId == company.Id),
                uow.Db.OemAccounts.LongCount(account => account.OemCompanyId == company.Id && account.Status == OemStatus.Active),
                uow.Db.OemCompanyContacts.LongCount(contact => contact.OemCompanyId == company.Id)))
            .ToArrayAsync(ct);
        return new OemPageResponse<OemCompanyListItemResponse>(rows, total, page, size);
    }

    /// <summary>Active vendors for choosing a transfer target; available to anyone who may create or view internal transfers.</summary>
    public async Task<IReadOnlyList<OemCompanyOptionResponse>> CompanyOptionsAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        if (!await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferCreate, ct)
            && !await OemAuthorizer.HasAsync(uow, current, OemPermissions.TransferView, ct))
            throw ApiException.Forbidden();
        var companies = await uow.Db.OemCompanies.AsNoTracking().Where(company => company.Status == OemStatus.Active)
            .OrderBy(company => company.Name).Select(company => new
            {
                company.Id, company.Name,
                CanReceive = uow.Db.OemAccounts.Any(account => account.OemCompanyId == company.Id && account.Status == OemStatus.Active),
            }).ToArrayAsync(ct);
        return companies.Select(company => new OemCompanyOptionResponse(company.Id, company.Name, company.CanReceive,
            company.CanReceive ? null : OemRecipientPolicy.MissingAccountMessage)).ToArray();
    }

    public async Task<OemCompanyResponse> CompanyDetailAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var company = await uow.Db.OemCompanies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct) ?? throw ApiException.NotFound();
        return CompanyJson(company);
    }

    public async Task<OemCompanyResponse> CreateCompanyAsync(OemActor actor, OemCompanyUpsert request, CancellationToken ct)
    {
        var input = ValidateCompany(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        if (await uow.Db.OemCompanies.AnyAsync(company => company.Name == input.Name, ct)) throw ApiException.Conflict("厂商名称已存在");
        var company = new OemCompany
        {
            Name = input.Name,
            Remark = input.Remark, Status = OemStatus.Active, CreatedBy = current.User.Id, CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemCompanies.Add(company);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_CREATE", "oem_company", company.Id, new { targetName = company.Name }, ct);
        await uow.CommitAsync(ct);
        return CompanyJson(company);
    }

    public async Task<OemImportResponse> ImportCompaniesAsync(OemActor actor, OemCompanyImportRequest request, CancellationToken ct)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumCompanyImportRows
            || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumCompanyImportRows} 行");

        // Verify authority before exposing duplicate names in the preview/commit response.
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
            await OemAuthorizer.RequireInternalAsync(read, actor, OemPermissions.CompanyManage, ct);

        var normalized = new OemCompanyUpsert[request.Rows.Length];
        var validationErrors = new string?[request.Rows.Length];
        for (var index = 0; index < request.Rows.Length; index++)
        {
            try
            {
                var row = request.Rows[index] ?? throw ApiException.BadRequest("厂商名称不能为空");
                normalized[index] = ValidateCompany(new OemCompanyUpsert(
                    row.Name, row.Remark));
            }
            catch (ApiException error) when (error.Status == 400)
            {
                validationErrors[index] = error.Message;
                normalized[index] = new OemCompanyUpsert("", null);
            }
        }

        var duplicatesInFile = normalized.Select(item => item.Name.Trim().ToUpperInvariant()).ToArray()
            .Select((key, index) => (key, index)).Where(item => item.key.Length > 0)
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.index)).ToHashSet();

        string[] existingNames;
        await using (var read = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            await OemAuthorizer.RequireInternalAsync(read, actor, OemPermissions.CompanyManage, ct);
            var names = normalized.Select(item => item.Name).Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            existingNames = names.Length == 0 ? []
                : await read.Db.OemCompanies.AsNoTracking().Where(company => Enumerable.Contains(names, company.Name))
                    .Select(company => company.Name).ToArrayAsync(ct);
        }
        var existing = existingNames.Select(name => name.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<OemImportRowResult>(request.Rows.Length);
        for (var index = 0; index < request.Rows.Length; index++)
        {
            var row = request.Rows[index];
            var rowNumber = row.RowNumber is >= 2 and <= 500_001 ? row.RowNumber : index + 2;
            var name = normalized[index].Name;
            if (validationErrors[index] is { } validationError)
            {
                results.Add(new(rowNumber, row.Name ?? "", "FAILED", validationError));
                continue;
            }
            if (duplicatesInFile.Contains(index))
            {
                results.Add(new(rowNumber, name, "SKIPPED", "文件内厂商名称重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(name.Trim().ToUpperInvariant()))
            {
                results.Add(new(rowNumber, name, "SKIPPED", "厂商名称已存在，未覆盖现有资料"));
                continue;
            }
            try
            {
                await CreateCompanyAsync(actor, normalized[index], ct);
                results.Add(new(rowNumber, name, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                var status = error.Status == 409 && error.Message == "厂商名称已存在" ? "SKIPPED" : "FAILED";
                results.Add(new(rowNumber, name, status, error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, name, "FAILED", "权限已变化，批次已停止"));
                for (var remaining = index + 1; remaining < request.Rows.Length; remaining++)
                {
                    var pending = request.Rows[remaining];
                    results.Add(new(pending.RowNumber is >= 2 and <= 500_001 ? pending.RowNumber : remaining + 2,
                        pending.Name ?? "", "FAILED", "未执行"));
                }
                break;
            }
        }

        return new(
            results.Count(row => row.Status == "CREATED"),
            results.Count(row => row.Status == "SKIPPED"),
            results.Count(row => row.Status == "FAILED"),
            results);
    }

    public async Task<OemCompanyResponse> UpdateCompanyAsync(OemActor actor, ulong id, OemCompanyUpsert request, CancellationToken ct)
    {
        var input = ValidateCompany(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var company = await LockCompanyAsync(uow, id, ct);
        if (await uow.Db.OemCompanies.AnyAsync(item => item.Name == input.Name && item.Id != id, ct)) throw ApiException.Conflict("厂商名称已存在");
        var changes = AuditChange.OnlyChanged(
            new AuditChange("name", "厂商名称", company.Name, input.Name),
            new AuditChange("remark", "备注", company.Remark, input.Remark));
        company.Name = input.Name;
        company.Remark = input.Remark;
        company.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_UPDATE", "oem_company", id, new { targetName = company.Name, changes }, ct);
        await uow.CommitAsync(ct);
        return CompanyJson(company);
    }

    public async Task<OemCompanyResponse> SetCompanyStatusAsync(OemActor actor, ulong id, string status, CancellationToken ct)
    {
        var target = OemStatus.Normalize(status);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var company = await LockCompanyAsync(uow, id, ct);
        if (company.Status != target && target == OemStatus.Disabled)
        {
            // Senders create and seal transfers under the business gate, which this management
            // transaction holds exclusively, so the count cannot change before commit.
            var inFlight = await uow.Db.OemTransfers.LongCountAsync(transfer => transfer.OemCompanyId == id
                && transfer.LifecycleStatus == TransferLifecycle.Sealed, ct);
            if (inFlight > 0)
                throw ApiException.Conflict($"该厂商仍有 {inFlight} 个进行中的传递单（已发送、尚未发布或关闭），请先终止或等待其完成后再停用厂商");
        }
        if (company.Status != target)
        {
            var previous = company.Status;
            company.Status = target;
            company.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            var revoked = 0;
            if (target == OemStatus.Disabled)
                revoked = await OemAuthService.RevokeCompanyAsync(uow.Db, id, RefreshRevokeReasons.Admin, ct);
            await audit.WriteAsync(uow, current, "OEM_COMPANY_STATUS", "oem_company", id, new
            {
                targetName = company.Name, revokedSessions = revoked,
                changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", previous, target)),
            }, ct);
        }
        await uow.CommitAsync(ct);
        return CompanyJson(company);
    }

    public async Task DeleteCompanyAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        await OemAuthorizer.RequireAsync(uow, current, OemPermissions.CompanyDelete, ct);
        var company = await LockCompanyAsync(uow, id, ct);
        var accountCount = await uow.Db.OemAccounts.LongCountAsync(account => account.OemCompanyId == id, ct);
        if (accountCount > 0)
            throw ApiException.BadRequest($"该厂商仍有 {accountCount} 个关联账号，请先逐个删除账号；如需保留账号请停用厂商");
        if (await uow.Db.OemTransfers.AnyAsync(transfer => transfer.OemCompanyId == id, ct))
            throw ApiException.BadRequest("该厂商仍有传递单或历史记录，请停用厂商，不要删除");
        var removedContacts = await uow.Db.OemCompanyContacts.Where(contact => contact.OemCompanyId == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_DELETE", "oem_company", id,
            new { targetName = company.Name, removedContacts, changes = Array.Empty<AuditChange>() }, ct);
        await uow.Db.OemCompanies.Where(item => item.Id == id).ExecuteDeleteAsync(ct);
        await uow.CommitAsync(ct);
    }

    public async Task<OemCompanyContactResponse[]> ListCompanyContactsAsync(OemActor actor, ulong companyId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == companyId, ct)) throw ApiException.NotFound();
        return await uow.Db.OemCompanyContacts.AsNoTracking()
            .Where(contact => contact.OemCompanyId == companyId)
            .OrderBy(contact => contact.SortNo).ThenBy(contact => contact.Id)
            .Select(contact => ContactJson(contact)).ToArrayAsync(ct);
    }

    public async Task<OemCompanyContactResponse> CreateCompanyContactAsync(
        OemActor actor, ulong companyId, OemCompanyContactUpsert request, CancellationToken ct)
    {
        var input = ValidateCompanyContact(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var company = await LockCompanyAsync(uow, companyId, ct);
        if (company.Status != OemStatus.Active) throw ApiException.BadRequest("厂商已被禁用，不能新增联络人");
        if (await uow.Db.OemCompanyContacts.LongCountAsync(contact => contact.OemCompanyId == companyId, ct) >= MaximumContactsPerCompany)
            throw ApiException.BadRequest($"每个厂商最多维护 {MaximumContactsPerCompany} 位联络人");
        if (await uow.Db.OemCompanyContacts.AnyAsync(contact => contact.OemCompanyId == companyId && contact.Name == input.Name, ct))
            throw ApiException.Conflict("该厂商下已有同名联络人");
        var contact = new OemCompanyContact
        {
            OemCompanyId = companyId, Name = input.Name, Email = input.Email,
            SortNo = input.SortNo, Status = OemStatus.Active, CreatedBy = current.User.Id,
            CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemCompanyContacts.Add(contact);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_CONTACT_CREATE", "oem_company_contact", contact.Id,
            new { companyId, companyName = company.Name, contact.Name, contact.Email }, ct);
        await uow.CommitAsync(ct);
        return ContactJson(contact);
    }

    public async Task<OemCompanyContactResponse> UpdateCompanyContactAsync(
        OemActor actor, ulong id, OemCompanyContactUpsert request, CancellationToken ct)
    {
        var input = ValidateCompanyContact(request);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var contact = await OemLocks.ForUpdate<OemCompanyContact>(uow.Db, id).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (await uow.Db.OemCompanyContacts.AnyAsync(item => item.OemCompanyId == contact.OemCompanyId && item.Id != id && item.Name == input.Name, ct))
            throw ApiException.Conflict("该厂商下已有同名联络人");
        contact.Name = input.Name;
        contact.Email = input.Email;
        contact.SortNo = input.SortNo;
        contact.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_CONTACT_UPDATE", "oem_company_contact", id, new { contact.Name, contact.Email }, ct);
        await uow.CommitAsync(ct);
        return ContactJson(contact);
    }

    public async Task<OemCompanyContactResponse> SetCompanyContactStatusAsync(OemActor actor, ulong id, string status, CancellationToken ct)
    {
        var target = OemStatus.Normalize(status);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
        var contact = await OemLocks.ForUpdate<OemCompanyContact>(uow.Db, id).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        contact.Status = target;
        contact.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_CONTACT_STATUS", "oem_company_contact", id, new { contact.Name, status = target }, ct);
        await uow.CommitAsync(ct);
        return ContactJson(contact);
    }

    public async Task<OemImportResponse> ImportCompanyContactsAsync(
        OemActor actor, ulong companyId, OemCompanyContactImportRequest request, CancellationToken ct)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumContactsPerCompany || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumContactsPerCompany} 行");

        string companyName;
        HashSet<string> existing;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.CompanyManage, ct);
            companyName = await uow.Db.OemCompanies.AsNoTracking().Where(company => company.Id == companyId)
                .Select(company => company.Name).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
            existing = (await uow.Db.OemCompanyContacts.AsNoTracking().Where(contact => contact.OemCompanyId == companyId)
                .Select(contact => contact.Name).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var repeated = request.Rows.Select(row => row.Name?.Trim() ?? "").Select((name, index) => (name, index))
            .Where(item => item.name.Length > 0).GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.index)).ToHashSet();
        var results = new List<OemImportRowResult>(request.Rows.Length);
        for (var index = 0; index < request.Rows.Length; index++)
        {
            var row = request.Rows[index];
            var rowNumber = row.RowNumber is >= 2 and <= 101 ? row.RowNumber : index + 2;
            var name = row.Name?.Trim() ?? "";
            if (!string.Equals(row.CompanyName?.Trim(), companyName, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(rowNumber, name, "FAILED", "厂商名称与当前联络人抽屉不匹配"));
                continue;
            }
            if (repeated.Contains(index))
            {
                results.Add(new(rowNumber, name, "SKIPPED", "文件内联络人姓名重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(name))
            {
                results.Add(new(rowNumber, name, "SKIPPED", "该厂商已有同名联络人，未覆盖现有资料"));
                continue;
            }
            try
            {
                await CreateCompanyContactAsync(actor, companyId,
                    new OemCompanyContactUpsert(name, row.Email, row.SortNo), ct);
                existing.Add(name);
                results.Add(new(rowNumber, name, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                results.Add(new(rowNumber, name, error.Status == 409 ? "SKIPPED" : "FAILED", error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, name, "FAILED", "权限已变化，批次已停止"));
                AddNotRun(results, request.Rows, index + 1);
                break;
            }
        }
        return Summarize(results);
    }

    private static void AddNotRun(List<OemImportRowResult> results, IReadOnlyList<OemCompanyContactImportRow> rows, int start)
    {
        for (var index = start; index < rows.Count; index++)
        {
            var row = rows[index];
            results.Add(new(row.RowNumber is >= 2 and <= 101 ? row.RowNumber : index + 2, row.Name ?? "", "FAILED", "未执行"));
        }
    }

    private static OemImportResponse Summarize(IReadOnlyList<OemImportRowResult> results) => new(
        results.Count(row => row.Status == "CREATED"), results.Count(row => row.Status == "SKIPPED"),
        results.Count(row => row.Status == "FAILED"), results);

    private static (string Name, string? Email, int SortNo) ValidateCompanyContact(OemCompanyContactUpsert request)
    {
        var name = OemValidation.RequiredText(request.Name, "联络人姓名", 64);
        var email = OemValidation.OptionalEmail(request.Email);
        if (email is null) throw ApiException.BadRequest("请输入邮箱");
        var sortNo = request.SortNo ?? 0;
        if (sortNo is < 0 or > 9999) throw ApiException.BadRequest("排序需为 0–9999");
        return (name, email, sortNo);
    }

    private static OemCompanyContactResponse ContactJson(OemCompanyContact contact) => new(
        contact.Id, contact.OemCompanyId, contact.Name, contact.Email, contact.SortNo,
        contact.Status, contact.CreatedAt, contact.UpdatedAt);

    public async Task<IReadOnlyList<OemAccountResponse>> ListAccountsAsync(OemActor actor, ulong companyId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == companyId, ct)) throw ApiException.NotFound();
        var accounts = await uow.Db.OemAccounts.AsNoTracking().Where(account => account.OemCompanyId == companyId)
            .OrderBy(account => account.Id).ToArrayAsync(ct);
        return accounts.Select(account => AccountJson(account, uow.Now)).ToArray();
    }

    public async Task<OemImportResponse> ImportAccountsAsync(
        OemActor actor, ulong companyId, OemAccountImportRequest request, CancellationToken ct)
    {
        if (request.Rows is null || request.Rows.Length is < 1 or > MaximumAccountImportRows || request.Rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumAccountImportRows} 行");

        string companyName;
        HashSet<string> existing;
        await using (var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct))
        {
            await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
            companyName = await uow.Db.OemCompanies.AsNoTracking().Where(company => company.Id == companyId)
                .Select(company => company.Name).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
            existing = (await uow.Db.OemAccounts.AsNoTracking().Select(account => account.EmployeeNo).ToArrayAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var repeated = request.Rows.Select(row => row.EmployeeNo?.Trim() ?? "").Select((key, index) => (key, index))
            .Where(item => item.key.Length > 0).GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.index)).ToHashSet();
        var results = new List<OemImportRowResult>(request.Rows.Length);
        for (var index = 0; index < request.Rows.Length; index++)
        {
            var row = request.Rows[index];
            var rowNumber = row.RowNumber is >= 2 and <= 101 ? row.RowNumber : index + 2;
            var employeeNo = row.EmployeeNo?.Trim() ?? "";
            if (!string.Equals(row.CompanyName?.Trim(), companyName, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(rowNumber, employeeNo, "FAILED", "厂商名称与当前账号抽屉不匹配"));
                continue;
            }
            if (repeated.Contains(index))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "文件内账号重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(employeeNo))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "登录账号已存在，未覆盖现有资料"));
                continue;
            }
            try
            {
                await CreateAccountAsync(actor, companyId,
                    new OemAccountCreate(employeeNo, row.InitialPassword ?? ""), ct, importedInitialPassword: true);
                existing.Add(employeeNo);
                results.Add(new(rowNumber, employeeNo, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                results.Add(new(rowNumber, employeeNo, error.Status == 409 ? "SKIPPED" : "FAILED", error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, employeeNo, "FAILED", "权限已变化，批次已停止"));
                for (var remaining = index + 1; remaining < request.Rows.Length; remaining++)
                {
                    var pending = request.Rows[remaining];
                    results.Add(new(pending.RowNumber is >= 2 and <= 101 ? pending.RowNumber : remaining + 2,
                        pending.EmployeeNo ?? "", "FAILED", "未执行"));
                }
                break;
            }
        }
        return Summarize(results);
    }

    public async Task<OemAccountResponse> AccountDetailAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await uow.Db.OemAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct) ?? throw ApiException.NotFound();
        return AccountJson(account, uow.Now);
    }

    /// <param name="importedInitialPassword">Batch import: the initial password is only length-checked (see PasswordService.ValidateInitial).</param>
    public async Task<OemAccountResponse> CreateAccountAsync(OemActor actor, ulong companyId, OemAccountCreate request, CancellationToken ct, bool importedInitialPassword = false)
    {
        var employeeNo = OemValidation.EmployeeNo(request.EmployeeNo);
        if (importedInitialPassword) PasswordService.ValidateInitial(request.Password); else PasswordService.Validate(request.Password);
        await PrecheckAccountManageAsync(actor, ct);
        var hash = importedInitialPassword
            ? await PasswordService.HashInitialAsync(request.Password, ct)
            : await PasswordService.HashAsync(request.Password, ct);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var company = await LockCompanyAsync(uow, companyId, ct);
        if (company.Status != OemStatus.Active) throw ApiException.BadRequest("厂商已被禁用");
        if (await uow.Db.OemAccounts.AnyAsync(account => account.EmployeeNo == employeeNo, ct)) throw ApiException.Conflict("登录账号已存在");
        var created = new OemAccount
        {
            EmployeeNo = employeeNo, PasswordHash = hash, RealName = employeeNo, Email = string.Empty, OemCompanyId = companyId,
            Status = OemStatus.Active, MustChangePassword = true, FailedLoginAttempts = 0, CreatedBy = current.User.Id,
            CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemAccounts.Add(created);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_CREATE", "oem_account", created.Id, new
        {
            targetName = created.EmployeeNo, companyId, companyName = company.Name,
        }, ct);
        await uow.CommitAsync(ct);
        return AccountJson(created, uow.Now);
    }

    public async Task<OemAccountResponse> SetAccountStatusAsync(OemActor actor, ulong id, string status, CancellationToken ct)
    {
        var target = OemStatus.Normalize(status);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await LockAccountForCompanyChangeAsync(uow, id, ct);
        if (target == OemStatus.Disabled && account.Status == OemStatus.Active) await EnsureNotLastActiveAccountAsync(uow, account, ct);
        if (target == OemStatus.Active
            && !await uow.Db.OemCompanies.AnyAsync(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active, ct))
            throw ApiException.BadRequest("所属厂商已被禁用，不能启用账号");
        if (account.Status != target)
        {
            var previous = account.Status;
            account.Status = target;
            account.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            var revoked = target == OemStatus.Disabled ? await OemAuthService.RevokeAllAsync(uow.Db, id, RefreshRevokeReasons.Admin, ct) : 0;
            await audit.WriteAsync(uow, current, "OEM_ACCOUNT_STATUS", "oem_account", id, new
            {
                targetName = account.EmployeeNo, revokedSessions = revoked,
                changes = AuditChange.OnlyChanged(new AuditChange("status", "状态", previous, target)),
            }, ct);
        }
        await uow.CommitAsync(ct);
        return AccountJson(account, uow.Now);
    }

    public async Task ResetAccountPasswordAsync(OemActor actor, ulong id, OemPasswordReset request, CancellationToken ct)
    {
        PasswordService.Validate(request.NewPassword);
        await PrecheckAccountManageAsync(actor, ct);
        var hash = await PasswordService.HashAsync(request.NewPassword, ct);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await LockAccountAsync(uow, id, ct);
        account.PasswordHash = hash;
        account.MustChangePassword = true;
        account.FailedLoginAttempts = 0;
        account.LockedUntil = null;
        account.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        var revoked = await OemAuthService.RevokeAllAsync(uow.Db, id, RefreshRevokeReasons.Admin, ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_RESET_PASSWORD", "oem_account", id,
            new { targetName = account.EmployeeNo, revokedSessions = revoked }, ct);
        await uow.CommitAsync(ct);
    }

    public async Task DeleteAccountAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        await OemAuthorizer.RequireAsync(uow, current, OemPermissions.AccountDelete, ct);
        // Account row lock first (FOR UPDATE), then the history checks: OEM writers that add
        // references to an account hold a shared lock on that row (OemAccountLock).
        var account = await LockAccountForCompanyChangeAsync(uow, id, ct);
        if (account.Status == OemStatus.Active) await EnsureNotLastActiveAccountAsync(uow, account, ct);
        await EnsureNoAccountHistoryAsync(uow, id, ct);
        var revokedSessions = await uow.Db.OemRefreshTokens.Where(token => token.AccountId == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_DELETE", "oem_account", id, new
        {
            account.EmployeeNo,
            targetName = account.EmployeeNo,
            revokedSessions,
            changes = Array.Empty<AuditChange>(),
        }, ct);
        await uow.Db.OemAccounts.Where(item => item.Id == id).ExecuteDeleteAsync(ct);
        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// Rejects unauthorised callers before the Argon2 hash is computed, so they cannot
    /// occupy the hash slots shared with login. The write transaction re-checks.
    /// </summary>
    private async Task PrecheckAccountManageAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
    }

    private static async Task EnsureNoAccountHistoryAsync(OemUnitOfWork uow, ulong id, CancellationToken ct)
    {
        var hasHistory = await uow.Db.OemTransfers.AnyAsync(transfer => transfer.OemSenderAccountId == id
                || transfer.ClosedByRealm == OemRealms.Oem && transfer.ClosedById == id, ct)
            || await uow.Db.OemTransferFiles.AnyAsync(file => file.UploadedByOemAccountId == id
                || file.FirstRecipientRealm == OemRealms.Oem && file.FirstRecipientId == id, ct)
            || await uow.Db.OemUploadSessions.AnyAsync(session => session.UploaderRealm == OemRealms.Oem && session.UploaderId == id, ct)
            || await uow.Db.OemDownloadSessions.AnyAsync(session => session.ActorRealm == OemRealms.Oem && session.ActorId == id, ct)
            || await uow.Db.AuditLogs.AnyAsync(log => log.ActorRealm == OemRealms.Oem && log.ActorAccountId == id, ct)
            || await uow.Db.EmailOutbox.AnyAsync(mail => mail.RecipientRealm == OemRealms.Oem && mail.RecipientAccountId == id, ct);
        if (hasHistory) throw ApiException.BadRequest("该账号仍有传递、文件、通知、登录或审计历史，请停用账号，不要删除");
    }

    /// <summary>
    /// Locks the account's company and then the account, the company → account order of
    /// <see cref="OemLocks"/> also used by <see cref="CreateAccountAsync"/>, so the active-account count read afterwards
    /// cannot be changed by a concurrent create/disable/delete for the same company.
    /// </summary>
    private static async Task<OemAccount> LockAccountForCompanyChangeAsync(OemUnitOfWork uow, ulong id, CancellationToken ct)
    {
        var companyId = await uow.Db.OemAccounts.AsNoTracking().Where(item => item.Id == id)
            .Select(item => (ulong?)item.OemCompanyId).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        await LockCompanyAsync(uow, companyId, ct);
        var account = await LockAccountAsync(uow, id, ct);
        // An account never moves between companies; guard anyway so the lock is always the right one.
        if (account.OemCompanyId != companyId) throw ApiException.Conflict("账号所属厂商已变化，请刷新后重试");
        return account;
    }

    /// <summary>
    /// An active vendor must keep at least one active account (like the last active admin);
    /// a disabled vendor is exempt. Callers hold the company row lock.
    /// </summary>
    private static async Task EnsureNotLastActiveAccountAsync(OemUnitOfWork uow, OemAccount account, CancellationToken ct)
    {
        var companyActive = await uow.Db.OemCompanies.AnyAsync(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active, ct);
        if (!companyActive) return;
        var othersActive = await uow.Db.OemAccounts.AnyAsync(item => item.OemCompanyId == account.OemCompanyId
            && item.Id != account.Id && item.Status == OemStatus.Active, ct);
        if (!othersActive) throw ApiException.Conflict(LastActiveAccountMessage);
    }

    private static async Task<OemCompany> LockCompanyAsync(OemUnitOfWork uow, ulong id, CancellationToken ct) =>
        await OemLocks.ForUpdate<OemCompany>(uow.Db, id).SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound();

    private static async Task<OemAccount> LockAccountAsync(OemUnitOfWork uow, ulong id, CancellationToken ct) =>
        await OemLocks.ForUpdate<OemAccount>(uow.Db, id).SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound();

    private static OemCompanyUpsert ValidateCompany(OemCompanyUpsert request) => new(
        OemValidation.RequiredText(request.Name, "厂商名称", 128),
        OemValidation.OptionalText(request.Remark, "备注", 512));

    private static OemCompanyResponse CompanyJson(OemCompany company) => new(
        company.Id, company.Name, company.Remark,
        company.Status, company.CreatedAt, company.UpdatedAt);

    /// <param name="now">The unit of work's database clock, which is also what login lockout compares against.</param>
    private static OemAccountResponse AccountJson(OemAccount account, DateTime now) => new(
        account.Id, account.EmployeeNo, account.OemCompanyId, account.Status,
        account.MustChangePassword, account.LockedUntil.HasValue && account.LockedUntil > now,
        account.LastLoginAt, account.CreatedAt, account.UpdatedAt);
}
