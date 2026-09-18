using System.Collections.Concurrent;

namespace Yf.Api.Modules.Identity;

public sealed class LoginRateLimiter
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Window> _rates = new();

    public bool AllowLogin(string clientIp, string employeeNo) =>
        AllowIpAndAccount(clientIp, employeeNo) && AllowIp(clientIp);

    /// <summary>
    /// Realm-scoped variant for identity realms other than the internal/supplier
    /// `users` realm. Account buckets never collide across realms even when two
    /// realms contain the same login name; the per-IP bucket stays shared.
    /// </summary>
    public bool AllowLogin(string realm, string clientIp, string employeeNo) =>
        Allow($"login:{realm}:{clientIp}:{employeeNo}", 10) && AllowIp(clientIp);

    private bool AllowIpAndAccount(string clientIp, string employeeNo) =>
        Allow($"login:{clientIp}:{employeeNo}", 10);

    private bool AllowIp(string clientIp) => Allow("login:" + clientIp, 60);

    private bool Allow(string key, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        if (_rates.Count >= 8192)
        {
            foreach (var expired in _rates.Where(x => now - x.Value.Start >= WindowDuration))
                _rates.TryRemove(expired.Key, out _);
            while (_rates.Count >= 8192)
            {
                var oldest = _rates.MinBy(x => x.Value.Start);
                if (string.IsNullOrEmpty(oldest.Key) || !_rates.TryRemove(oldest.Key, out _)) break;
            }
        }
        var window = _rates.AddOrUpdate(key, _ => new(now, 1),
            (_, old) => now - old.Start >= WindowDuration ? new(now, 1) : old with { Count = old.Count + 1 });
        return window.Count <= limit;
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
