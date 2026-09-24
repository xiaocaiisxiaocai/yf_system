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
    private readonly ConcurrentDictionary<string, Window> _passwordChangeRates = new();
    private readonly object _loginIpSync = new();
    private readonly object _loginAccountSync = new();
    private readonly object _passwordChangeSync = new();

    public bool AllowLogin(string clientIp, string employeeNo)
    {
        var normalizedIp = NormalizeIp(clientIp);
        return Allow(_loginIpRates, _loginIpSync, normalizedIp, 60)
            && Allow(_loginAccountRates, _loginAccountSync, $"{normalizedIp}:{employeeNo}", 10);
    }

    /// <summary>
    /// Every self-service old-password check costs one Argon2 derivation from the shared login pool, so
    /// attempts (not only failures) are limited per account, independent of the client IP.
    /// </summary>
    public bool AllowPasswordChange(ulong userId) =>
        Allow(_passwordChangeRates, _passwordChangeSync, userId.ToString(CultureInfo.InvariantCulture), MaximumPasswordChangeAttempts);

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
