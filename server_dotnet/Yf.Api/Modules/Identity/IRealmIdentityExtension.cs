namespace Yf.Api.Modules.Identity;

/// <summary>
/// Plug-in point for an identity realm that lives outside the internal/supplier
/// `users` table. The Identity module only knows this contract; realm owners (for
/// example the OEM business line) implement it, so authentication stays closed for
/// modification while new realms can be added (dependency inversion).
///
/// Isolation rules enforced by <see cref="IdentityMiddleware"/> on top of this:
/// a token of this realm is accepted only below <see cref="PathPrefix"/>, and the
/// internal realm's principal is never materialised for it.
/// </summary>
public interface IRealmIdentityExtension
{
    /// <summary>Realm stamped into access tokens (the `rlm` claim).</summary>
    string Realm { get; }

    /// <summary>API path prefix that this realm's tokens are confined to (e.g. /api/v1/oem).</summary>
    PathString PathPrefix { get; }

    /// <summary>Requests that carry no bearer token by design (login/refresh/logout, cookie-authenticated streams).</summary>
    bool IsAnonymousPath(HttpRequest request);

    /// <summary>Validates the live account/session behind <paramref name="claims"/> and stores the realm principal on the context.</summary>
    Task AuthenticateAsync(HttpContext context, AccessClaims claims, CancellationToken ct);
}
