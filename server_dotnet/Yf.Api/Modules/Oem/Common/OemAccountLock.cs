using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Shared row lock on the acting OEM account, taken by business writers right before they
/// insert a row that references that account (upload session, uploaded file, download
/// session, vendor-created transfer). Account deletion locks the same row FOR UPDATE before
/// its history checks, so a reference can never appear between "no history" and the delete.
/// The management gate already serialises these transactions; this keeps the guarantee local
/// to the row instead of depending on the gate alone.
/// The account row is the last OEM row a writer locks — see the global order on <see cref="OemLocks"/>.
/// </summary>
internal static class OemAccountLock
{
    public static async Task ShareAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct)
    {
        if (actor is not OemAccountActor account) return;
        if (!await OemLocks.ForShareAsync<OemAccount>(uow, account.AccountId, ct)) throw ApiException.Forbidden();
    }
}
