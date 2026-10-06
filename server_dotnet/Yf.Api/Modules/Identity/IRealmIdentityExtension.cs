namespace Yf.Api.Modules.Identity;

/// <summary>
/// Authenticates an identity realm whose accounts and sessions do not live in the
/// shared internal/supplier identity tables.
/// </summary>
public interface IRealmIdentityExtension
{
    /// <summary>Realm value carried by the access token's <c>rlm</c> claim.</summary>
    string Realm { get; }

    /// <summary>Only requests below this prefix may use access tokens from this realm.</summary>
    PathString PathPrefix { get; }

    /// <summary>Precisely identifies routes that intentionally bypass bearer authentication.</summary>
    bool IsAnonymousPath(HttpRequest request);

    /// <summary>Validates the live account/session and publishes the realm principal.</summary>
    Task AuthenticateAsync(HttpContext context, AccessClaims claims, CancellationToken ct);
}
