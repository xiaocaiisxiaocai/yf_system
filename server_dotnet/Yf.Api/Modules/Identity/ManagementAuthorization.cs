using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

internal static class ManagementAuthorization
{
    internal static async Task PrecheckAsync(
        YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var connectionWasOpen = context.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        if (!connectionWasOpen) await context.Database.OpenConnectionAsync(ct);
        try
        {
            await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, permission, ct);
        }
        finally
        {
            if (!connectionWasOpen) await context.Database.CloseConnectionAsync();
        }
    }

    internal static async Task RequireAsync(
        YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        var connection = context.Database.Connection();
        var transaction = context.Database.RequireTransaction();
        await AccessService.LockManagementAsync(connection, transaction, ct);
        actor = await AccessService.RecheckActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, transaction, actor, permission, ct);
    }
}
