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
    private readonly ConcurrentDictionary<string, KnownSources> _knownLoginSources = new();
    private readonly ConcurrentDictionary<string, Window> _passwordChangeRates = new();
    private readonly object _loginIpSync = new();
    private readonly object _loginAccountSync = new();
    private readonly object _loginGlobalAccountSync = new();
    private readonly object _knownLoginSourcesSync = new();
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
    /// A source that recently signed in to this account successfully skips this shared budget, so
    /// a flood from elsewhere cannot keep the owner out; the IP and IP+account limits still apply.
    /// </summary>
    public bool AllowAccountLogin(string realm, ulong? accountId, string employeeNo, string clientIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        if (accountId is { } known && IsKnownLoginSource(realm, known, clientIp)) return true;
        var key = accountId is { } id
            ? $"{realm}:id:{id.ToString(CultureInfo.InvariantCulture)}"
            : $"{realm}:name:{NormalizeLogin(employeeNo)}";
        return Allow(_loginGlobalAccountRates, _loginGlobalAccountSync, key, MaximumAccountLoginAttempts);
    }

    /// <summary>Remembers the source of a successful password sign-in for <see cref="AllowAccountLogin"/>.</summary>
    public void RecordSuccessfulLogin(string realm, ulong accountId, string clientIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        var key = KnownSourceKey(realm, accountId);
        var source = NormalizeIp(clientIp);
        lock (_knownLoginSourcesSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_knownLoginSources.Count >= MaximumKeysPerPurpose && !_knownLoginSources.ContainsKey(key))
            {
                foreach (var expired in _knownLoginSources.Where(x => now - x.Value.LastSeen >= KnownSourceLifetime))
                    _knownLoginSources.TryRemove(expired.Key, out _);
                if (_knownLoginSources.Count >= MaximumKeysPerPurpose) return;
            }
            var sources = _knownLoginSources.GetOrAdd(key, _ => new KnownSources());
            sources.Seen[source] = now;
            sources.LastSeen = now;
            foreach (var stale in sources.Seen.Where(x => now - x.Value >= KnownSourceLifetime).Select(x => x.Key).ToArray())
                sources.Seen.Remove(stale);
            while (sources.Seen.Count > MaximumKnownSourcesPerAccount)
                sources.Seen.Remove(sources.Seen.MinBy(x => x.Value).Key);
        }
    }

    private bool IsKnownLoginSource(string realm, ulong accountId, string clientIp)
    {
        lock (_knownLoginSourcesSync)
        {
            return _knownLoginSources.TryGetValue(KnownSourceKey(realm, accountId), out var sources)
                && sources.Seen.TryGetValue(NormalizeIp(clientIp), out var seen)
                && DateTimeOffset.UtcNow - seen < KnownSourceLifetime;
        }
    }

    private static string KnownSourceKey(string realm, ulong accountId) =>
        $"{realm}:{accountId.ToString(CultureInfo.InvariantCulture)}";

    internal const int MaximumAccountLoginAttempts = 30;
    internal const int MaximumKnownSourcesPerAccount = 8;
    private static readonly TimeSpan KnownSourceLifetime = TimeSpan.FromDays(30);

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

    private sealed class KnownSources
    {
        public Dictionary<string, DateTimeOffset> Seen { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastSeen { get; set; }
    }
}
