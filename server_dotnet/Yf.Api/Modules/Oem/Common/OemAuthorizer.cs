using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Authorization for OEM operations. Internal staff are authorised through the
/// shared role/permission tables; OEM accounts have a fixed capability set and can
/// never hold an internal permission. Both are re-validated inside the business
/// transaction so a concurrent disable/revoke cannot slip through.
/// </summary>
internal static class OemAuthorizer
{
    /// <summary>Re-reads the actor inside the unit of work and rejects disabled or password-reset-pending callers.</summary>
    public static async Task<OemActor> RecheckAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct)
    {
        switch (actor)
        {
            case InternalOemActor internalActor:
            {
                var user = await AccessService.RecheckActorAsync(uow.Connection, uow.Transaction!, internalActor.User, ct);
                if (!user.IsInternal) throw new ApiException(403, 40304, "该账号无权访问此系统");
                return internalActor with { User = user };
            }
            case OemAccountActor account:
            {
                var row = await uow.Db.OemAccounts.AsNoTracking()
                    .Where(item => item.Id == account.AccountId)
                    .Join(uow.Db.OemCompanies, item => item.OemCompanyId, company => company.Id,
                        (item, company) => new { item.Status, item.MustChangePassword, item.OemCompanyId, CompanyStatus = company.Status })
                    .SingleOrDefaultAsync(ct);
                if (row is null || row.Status != OemStatus.Active || row.CompanyStatus != OemStatus.Active || row.MustChangePassword)
                    throw ApiException.Forbidden();
                if (row.OemCompanyId != account.CompanyId) throw ApiException.Forbidden();
                return account;
            }
            default:
                throw ApiException.Forbidden();
        }
    }

    public static async Task<bool> HasAsync(OemUnitOfWork uow, OemActor actor, string permission, CancellationToken ct)
    {
        if (actor is not InternalOemActor internalActor) return false;
        var codes = await AccessService.PermissionCodesAsync(uow.Connection, uow.Transaction, internalActor.User.Id, ct);
        return codes.Contains(permission, StringComparer.Ordinal);
    }

    public static async Task RequireAsync(OemUnitOfWork uow, OemActor actor, string permission, CancellationToken ct)
    {
        if (!await HasAsync(uow, actor, permission, ct)) throw ApiException.Forbidden();
    }

    public static async Task<IReadOnlySet<string>> InternalPermissionsAsync(OemUnitOfWork uow, ulong userId, CancellationToken ct) =>
        (await AccessService.PermissionCodesAsync(uow.Connection, uow.Transaction, userId, ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Convenience for internal-only management operations: recheck + permission in one step.</summary>
    public static async Task<InternalOemActor> RequireInternalAsync(OemUnitOfWork uow, OemActor actor, string permission, CancellationToken ct)
    {
        if (actor is not InternalOemActor) throw ApiException.Forbidden("仅公司内部账号可以执行该操作");
        var current = (InternalOemActor)await RecheckAsync(uow, actor, ct);
        await RequireAsync(uow, current, permission, ct);
        return current;
    }
}
