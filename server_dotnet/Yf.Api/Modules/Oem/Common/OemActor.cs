using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// The caller of an OEM operation. The OEM business line serves two identity realms,
/// and every rule that differs between them (permissions, data scope, audit identity)
/// is expressed polymorphically instead of by scattered realm string comparisons.
/// </summary>
public abstract record OemActor(string Realm, ulong Id, string EmployeeNo, string LoginSessionId)
{
    public bool IsInternal => this is InternalOemActor;

    /// <summary>`users` id for audit rows, or null for actors of another realm.</summary>
    public abstract ulong? AuditUserId { get; }

    /// <summary>Realm identity for audit rows, or null for the historical `users` realm.</summary>
    public abstract AuditRealmActor? AuditRealmActor { get; }
}

public sealed record InternalOemActor(CurrentUser User, string SessionId)
    : OemActor(OemRealms.Internal, User.Id, User.EmployeeNo, SessionId)
{
    public override ulong? AuditUserId => User.Id;
    public override AuditRealmActor? AuditRealmActor => null;
}

public sealed record OemAccountActor(ulong AccountId, string AccountEmployeeNo, string RealName, ulong CompanyId, string SessionId)
    : OemActor(OemRealms.Oem, AccountId, AccountEmployeeNo, SessionId)
{
    public override ulong? AuditUserId => null;
    public override AuditRealmActor? AuditRealmActor => new(OemRealms.Oem, AccountId, AccountEmployeeNo, RealName);
}

public static class OemActorAccessor
{
    /// <summary>
    /// Resolves the authenticated OEM caller. Supplier accounts are rejected here as a
    /// second line of defence: the supplier collaboration line and the OEM line are
    /// mutually invisible regardless of any role configuration.
    /// </summary>
    public static OemActor Get(HttpContext context)
    {
        if (context.Items.TryGetValue(typeof(OemAccountActor), out var oem) && oem is OemAccountActor account)
            return account;
        if (context.Items.TryGetValue(typeof(CurrentUser), out var value) && value is CurrentUser user)
        {
            if (!user.IsInternal) throw new ApiException(403, 40304, "该账号无权访问此系统");
            var session = context.Items.TryGetValue(typeof(AccessClaims), out var claims) && claims is AccessClaims access
                ? access.SessionId
                : throw ApiException.Unauthorized();
            return new InternalOemActor(user, session);
        }
        throw ApiException.Unauthorized();
    }

    public static InternalOemActor GetInternal(HttpContext context) =>
        Get(context) as InternalOemActor ?? throw ApiException.Forbidden("仅公司内部账号可以执行该操作");
}

/// <summary>Group filter applied to every authenticated OEM endpoint.</summary>
internal sealed class OemAccessFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        OemActorAccessor.Get(context.HttpContext);
        return next(context);
    }
}
