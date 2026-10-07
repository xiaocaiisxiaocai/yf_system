using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Yf.Api.Modules.Identity;

public sealed class LoginRateLimiter
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);
    internal const int MaximumKeysPerPurpose = 8192;
    private readonly ConcurrentDictionary<string, Window> _loginIpRates = new();
    private readonly ConcurrentDictionary<string, Window> _loginAccountRates = new();
    private readonly ConcurrentDictionary<string, Window> _loginGlobalAccountRates = new();
    private readonly ConcurrentDictionary<string, Window> _passwordChangeRates = new();
    private readonly object _loginIpSync = new();
    private readonly object _loginAccountSync = new();
    private readonly object _loginGlobalAccountSync = new();
    private readonly object _passwordChangeSync = new();

    public bool AllowLogin(string clientIp, string employeeNo)
        => AllowLogin(IdentityRealms.Internal, clientIp, employeeNo);

    public bool AllowLogin(string realm, string clientIp, string employeeNo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        var normalizedIp = NormalizeIp(clientIp);
        return Allow(_loginIpRates, _loginIpSync, normalizedIp, 60)
            && Allow(_loginAccountRates, _loginAccountSync, $"{realm}:{normalizedIp}:{NormalizeLogin(employeeNo)}", 10);
    }

    /// <summary>
    /// Caps password attempts per account across all source addresses, so spreading guesses over
    /// many IPs does not lift the per-account budget. It throttles instead of locking: nothing is
    /// persisted, existing sessions are untouched, and the window resets after a minute. Callers
    /// key a known account by its database id (the lookup collation already folds case) and an
    /// unknown name by its normalized text, so both answer the same way and a 429 does not reveal
    /// whether the account exists.
    /// </summary>
    public bool AllowAccountLogin(string realm, ulong? accountId, string employeeNo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        var key = accountId is { } id
            ? $"{realm}:id:{id.ToString(CultureInfo.InvariantCulture)}"
            : $"{realm}:name:{NormalizeLogin(employeeNo)}";
        return Allow(_loginGlobalAccountRates, _loginGlobalAccountSync, key, MaximumAccountLoginAttempts);
    }

    internal const int MaximumAccountLoginAttempts = 30;

    private static string NormalizeLogin(string employeeNo) => employeeNo.Trim().ToUpperInvariant();

    /// <summary>
    /// Every self-service old-password check costs one Argon2 derivation from the shared login pool, so
    /// attempts (not only failures) are limited per account, independent of the client IP.
    /// </summary>
    public bool AllowPasswordChange(ulong userId) => AllowPasswordChange(IdentityRealms.Internal, userId);

    public bool AllowPasswordChange(string realm, ulong userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        return Allow(_passwordChangeRates, _passwordChangeSync,
            $"{realm}:{userId.ToString(CultureInfo.InvariantCulture)}", MaximumPasswordChangeAttempts);
    }

    internal const int MaximumPasswordChangeAttempts = 5;

    private static bool Allow(ConcurrentDictionary<string, Window> rates, object sync, string key, int limit)
    {
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (rates.Count >= MaximumKeysPerPurpose && !rates.ContainsKey(key))
            {
                foreach (var expired in rates.Where(x => now - x.Value.Start >= WindowDuration))
                    rates.TryRemove(expired.Key, out _);
                if (rates.Count >= MaximumKeysPerPurpose) return false;
            }
            var window = rates.AddOrUpdate(key, _ => new(now, 1),
                (_, old) => now - old.Start >= WindowDuration ? new(now, 1) : old with { Count = old.Count + 1 });
            return window.Count <= limit;
        }
    }

    internal static string NormalizeIp(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return value;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
