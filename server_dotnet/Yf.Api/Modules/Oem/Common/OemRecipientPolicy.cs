using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

internal static class OemRecipientPolicy
{
    internal const string MissingAccountMessage = "该厂商没有启用的登录账号，请先新增或启用账号后再发送";

    internal static async Task RequireEnabledAccountAsync(YfDbContext db, ulong companyId, CancellationToken ct)
    {
        if (!await db.OemAccounts.AnyAsync(account => account.OemCompanyId == companyId && account.Status == OemStatus.Active, ct))
            throw ApiException.BadRequest(MissingAccountMessage);
    }
}
