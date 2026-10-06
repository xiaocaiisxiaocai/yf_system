using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Identity;

namespace Yf.Api.Modules.Oem.Admin;

public sealed record OemCompanyUpsert(string Name, string? ContactName, string? ContactPhone, string? ContactEmail, string? Remark);
public sealed record OemStatusRequest(string Status);
public sealed record OemAccountCreate(string EmployeeNo, string RealName, string Email, string Password);
public sealed record OemAccountUpdate(string RealName, string Email);
public sealed record OemPasswordReset(string NewPassword);

/// <summary>
/// Management of OEM vendors and their login accounts. Only internal staff with the
/// matching `oem:*` permission reach this service; disabling a vendor or an account,
/// or resetting a password, revokes the affected OEM sessions in the same transaction.
/// Empty vendors and accounts may be physically deleted; records with business or
/// historical references must be disabled so their identity remains explainable.
/// </summary>
public sealed class OemDirectoryService(IDbContextFactory<YfDbContext> dbFactory, OemAuditWriter audit)
{
    public async Task<OemPageResponse<OemCompanyListItemResponse>> ListCompaniesAsync(OemActor actor, ulong page, uint size, ulong offset, string? keyword, string? status, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        // Account managers need the vendor list to reach the accounts they manage.
        if (!await OemAuthorizer.HasAsync(uow, await OemAuthorizer.RecheckAsync(uow, actor, ct), OemPermissions.CompanyManage, ct))
            await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var query = uow.Db.OemCompanies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = "%" + keyword.Trim() + "%";
            query = query.Where(company => EF.Functions.Like(company.Name, pattern));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = OemStatus.Normalize(status);
            query = query.Where(company => company.Status == normalized);
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(company => company.Id).Page(offset, size)
            .Select(company => new OemCompanyListItemResponse(
                company.Id, company.Name, company.ContactName, company.ContactPhone, company.ContactEmail, company.Remark,
                company.Status, company.CreatedAt, company.UpdatedAt,
                uow.Db.OemAccounts.LongCount(account => account.OemCompanyId == company.Id),
                uow.Db.OemAccounts.LongCount(account => account.OemCompanyId == company.Id && account.Status == OemStatus.Active)))
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
            Name = input.Name, ContactName = input.ContactName, ContactPhone = input.ContactPhone, ContactEmail = input.ContactEmail,
            Remark = input.Remark, Status = OemStatus.Active, CreatedBy = current.User.Id, CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemCompanies.Add(company);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_COMPANY_CREATE", "oem_company", company.Id, new { targetName = company.Name }, ct);
        await uow.CommitAsync(ct);
        return CompanyJson(company);
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
            new AuditChange("contactName", "联系人", company.ContactName, input.ContactName),
            new AuditChange("contactPhone", "联系电话", company.ContactPhone, input.ContactPhone),
            new AuditChange("contactEmail", "联系邮箱", company.ContactEmail, input.ContactEmail),
            new AuditChange("remark", "备注", company.Remark, input.Remark));
        company.Name = input.Name;
        company.ContactName = input.ContactName;
        company.ContactPhone = input.ContactPhone;
        company.ContactEmail = input.ContactEmail;
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
        if (company.Status != target)
        {
            var previous = company.Status;
            company.Status = target;
            company.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            var revoked = 0;
            if (target == OemStatus.Disabled)
            {
                var accountIds = await uow.Db.OemAccounts.Where(account => account.OemCompanyId == id).Select(account => account.Id).ToArrayAsync(ct);
                foreach (var accountId in accountIds) revoked += await OemAuthService.RevokeAllAsync(uow.Db, accountId, ct);
            }
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
        await audit.WriteAsync(uow, current, "OEM_COMPANY_DELETE", "oem_company", id,
            new { targetName = company.Name, changes = Array.Empty<AuditChange>() }, ct);
        await uow.Db.OemCompanies.Where(item => item.Id == id).ExecuteDeleteAsync(ct);
        await uow.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<OemAccountResponse>> ListAccountsAsync(OemActor actor, ulong companyId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        if (!await uow.Db.OemCompanies.AnyAsync(company => company.Id == companyId, ct)) throw ApiException.NotFound();
        var accounts = await uow.Db.OemAccounts.AsNoTracking().Where(account => account.OemCompanyId == companyId)
            .OrderBy(account => account.Id).ToArrayAsync(ct);
        return accounts.Select(account => AccountJson(account, uow.Now)).ToArray();
    }

    public async Task<OemAccountResponse> AccountDetailAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await uow.Db.OemAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct) ?? throw ApiException.NotFound();
        return AccountJson(account, uow.Now);
    }

    public async Task<OemAccountResponse> CreateAccountAsync(OemActor actor, ulong companyId, OemAccountCreate request, CancellationToken ct)
    {
        var employeeNo = OemValidation.EmployeeNo(request.EmployeeNo);
        var realName = OemValidation.RequiredText(request.RealName, "姓名", 64);
        var email = OemValidation.Email(request.Email);
        PasswordService.Validate(request.Password);
        await PrecheckAccountManageAsync(actor, ct);
        var hash = await PasswordService.HashAsync(request.Password, ct);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var company = await LockCompanyAsync(uow, companyId, ct);
        if (company.Status != OemStatus.Active) throw ApiException.BadRequest("厂商已被禁用");
        if (await uow.Db.OemAccounts.AnyAsync(account => account.EmployeeNo == employeeNo, ct)) throw ApiException.Conflict("登录账号已存在");
        var created = new OemAccount
        {
            EmployeeNo = employeeNo, PasswordHash = hash, RealName = realName, Email = email, OemCompanyId = companyId,
            Status = OemStatus.Active, MustChangePassword = true, FailedLoginAttempts = 0, CreatedBy = current.User.Id,
            CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemAccounts.Add(created);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_CREATE", "oem_account", created.Id, new
        {
            targetName = $"{created.RealName}（{created.EmployeeNo}）", companyId, companyName = company.Name, created.Email,
        }, ct);
        await uow.CommitAsync(ct);
        return AccountJson(created, uow.Now);
    }

    public async Task<OemAccountResponse> UpdateAccountAsync(OemActor actor, ulong id, OemAccountUpdate request, CancellationToken ct)
    {
        var realName = OemValidation.RequiredText(request.RealName, "姓名", 64);
        var email = OemValidation.Email(request.Email);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await LockAccountAsync(uow, id, ct);
        var changes = AuditChange.OnlyChanged(
            new AuditChange("realName", "姓名", account.RealName, realName),
            new AuditChange("email", "邮箱", account.Email, email));
        account.RealName = realName;
        account.Email = email;
        account.UpdatedAt = uow.Now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_UPDATE", "oem_account", id,
            new { targetName = $"{account.RealName}（{account.EmployeeNo}）", changes }, ct);
        await uow.CommitAsync(ct);
        return AccountJson(account, uow.Now);
    }

    public async Task<OemAccountResponse> SetAccountStatusAsync(OemActor actor, ulong id, string status, CancellationToken ct)
    {
        var target = OemStatus.Normalize(status);
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        var account = await LockAccountAsync(uow, id, ct);
        if (target == OemStatus.Active
            && !await uow.Db.OemCompanies.AnyAsync(company => company.Id == account.OemCompanyId && company.Status == OemStatus.Active, ct))
            throw ApiException.BadRequest("所属厂商已被禁用，不能启用账号");
        if (account.Status != target)
        {
            var previous = account.Status;
            account.Status = target;
            account.UpdatedAt = uow.Now;
            await uow.Db.SaveChangesAsync(ct);
            var revoked = target == OemStatus.Disabled ? await OemAuthService.RevokeAllAsync(uow.Db, id, ct) : 0;
            await audit.WriteAsync(uow, current, "OEM_ACCOUNT_STATUS", "oem_account", id, new
            {
                targetName = $"{account.RealName}（{account.EmployeeNo}）", revokedSessions = revoked,
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
        var revoked = await OemAuthService.RevokeAllAsync(uow.Db, id, ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_RESET_PASSWORD", "oem_account", id,
            new { targetName = $"{account.RealName}（{account.EmployeeNo}）", revokedSessions = revoked }, ct);
        await uow.CommitAsync(ct);
    }

    public async Task DeleteAccountAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.AccountManage, ct);
        await OemAuthorizer.RequireAsync(uow, current, OemPermissions.AccountDelete, ct);
        var account = await LockAccountAsync(uow, id, ct);
        await EnsureNoAccountHistoryAsync(uow, id, ct);
        var revokedSessions = await uow.Db.OemRefreshTokens.Where(token => token.AccountId == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_ACCOUNT_DELETE", "oem_account", id, new
        {
            account.EmployeeNo,
            account.RealName,
            targetName = $"{account.RealName}（{account.EmployeeNo}）",
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

    private static async Task<OemCompany> LockCompanyAsync(OemUnitOfWork uow, ulong id, CancellationToken ct) =>
        await uow.Db.OemCompanies.FromSqlInterpolated($"SELECT * FROM oem_companies WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound();

    private static async Task<OemAccount> LockAccountAsync(OemUnitOfWork uow, ulong id, CancellationToken ct) =>
        await uow.Db.OemAccounts.FromSqlInterpolated($"SELECT * FROM oem_accounts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound();

    private static OemCompanyUpsert ValidateCompany(OemCompanyUpsert request) => new(
        OemValidation.RequiredText(request.Name, "厂商名称", 128),
        OemValidation.OptionalText(request.ContactName, "联系人", 64),
        OemValidation.OptionalText(request.ContactPhone, "联系电话", 32),
        OemValidation.OptionalEmail(request.ContactEmail),
        OemValidation.OptionalText(request.Remark, "备注", 512));

    private static OemCompanyResponse CompanyJson(OemCompany company) => new(
        company.Id, company.Name, company.ContactName, company.ContactPhone, company.ContactEmail, company.Remark,
        company.Status, company.CreatedAt, company.UpdatedAt);

    /// <param name="now">The unit of work's database clock, which is also what login lockout compares against.</param>
    private static OemAccountResponse AccountJson(OemAccount account, DateTime now) => new(
        account.Id, account.EmployeeNo, account.RealName, account.Email, account.OemCompanyId, account.Status,
        account.MustChangePassword, account.LockedUntil.HasValue && account.LockedUntil > now,
        account.LastLoginAt, account.CreatedAt, account.UpdatedAt);
}
