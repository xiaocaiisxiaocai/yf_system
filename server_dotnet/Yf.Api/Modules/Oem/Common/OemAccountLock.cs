using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Shared row lock on the acting OEM account, taken by business writers right before they
/// insert a row that references that account (upload session, uploaded file, download
/// session, vendor-created transfer). Account deletion locks the same row FOR UPDATE before
/// its history checks, so a reference can never appear between "no history" and the delete.
/// The management gate already serialises these transactions; this keeps the guarantee local
/// to the row instead of depending on the gate alone.
///
/// Lock order: the account row is always the last OEM row a writer locks (after transfer,
/// file and company rows). Directory management locks company → account, and login only
/// locks the account (and its refresh tokens), so taking it last never inverts an order.
/// <c>LOCK IN SHARE MODE</c> keeps MySQL 5.7 compatibility.
/// </summary>
internal static class OemAccountLock
{
    public static async Task ShareAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct)
    {
        if (actor is not OemAccountActor account) return;
        var locked = await uow.Db.Database
            .SqlQuery<ulong>($"SELECT id AS Value FROM oem_accounts WHERE id = {account.AccountId} LOCK IN SHARE MODE")
            .SingleOrDefaultAsync(ct);
        if (locked == 0) throw ApiException.Forbidden();
    }
}
