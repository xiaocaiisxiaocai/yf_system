using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;
using IpNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace Yf.Api.Modules.Identity;

public readonly record struct RequestNetworkOrigin(string ClientIp, string Host = "")
{
    internal static RequestNetworkOrigin Resolve(HttpContext context, AppOptions options) =>
        new(Infrastructure.ClientIp.Resolve(context, options), context.Request.Host.Value);
}

internal static class AccountNetworkAccess
{
    internal const string Any = "ANY";
    internal const string InternalOnly = "INTERNAL_ONLY";

    internal static string ValidateScope(string value) => value is Any or InternalOnly
        ? value : throw ApiException.BadRequest("访问范围只能为 INTERNAL_ONLY 或 ANY");

    internal static bool RequiresInternalNetwork(string userType, string scope) =>
        userType == UserTypes.Internal && scope == InternalOnly;

    internal static ApiException Denied() => new(403, 40305, "该账号仅限内网访问");

    internal static async Task<bool> IsDeniedAsync(
        YfDbContext context, string userType, string scope, RequestNetworkOrigin origin, CancellationToken ct) =>
        RequiresInternalNetwork(userType, scope) && await RuntimeSettings.IsExternalAsync(context, origin, ct);

    internal static string[] SplitEntries(string? value) =>
        (value ?? "").Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static bool IsExternal(RequestNetworkOrigin origin, string? networks, string? hosts)
    {
        if (networks is null || hosts is null || !TryParseAddress(origin.ClientIp, out var address)) return true;
        var externalHosts = SplitEntries(hosts);
        if (externalHosts.Any(host => !IsHostAndPort(host))
            || externalHosts.Contains(origin.Host, StringComparer.OrdinalIgnoreCase)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var inside = false;
        foreach (var entry in SplitEntries(networks))
        {
            if (!TryParseNetwork(entry, out var network)) return true;
            inside |= network.Contains(address);
        }
        return !inside;
    }

    internal static bool TryParseNetwork(string entry, out IpNetwork network)
    {
        network = null!;
        var slash = entry.IndexOf('/');
        var rawAddress = slash < 0 ? entry : entry[..slash];
        if (rawAddress.Contains('%') || !TryParseAddress(rawAddress, out var address)) return false;
        var prefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slash >= 0)
        {
            if (!int.TryParse(entry.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
                || bits > prefix) return false;
            prefix = bits;
        }
        if (address.IsIPv4MappedToIPv6 && prefix >= 96)
        {
            address = address.MapToIPv4();
            prefix -= 96;
        }
        network = new IpNetwork(address, prefix);
        return true;
    }

    internal static bool IsHostAndPort(string entry)
    {
        var colon = entry.LastIndexOf(':');
        if (colon < 1 || !ushort.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port == 0) return false;
        var host = entry[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
            return !host.Contains('%') && IPAddress.TryParse(host[1..^1], out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6;
        return Uri.CheckHostName(host) switch
        {
            UriHostNameType.IPv4 => TryParseAddress(host, out _),
            UriHostNameType.Dns => !host.Contains('_') && !host.All(c => char.IsAsciiDigit(c) || c == '.'),
            _ => false,
        };
    }

    private static bool TryParseAddress(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed)
            && (parsed.AddressFamily == AddressFamily.InterNetworkV6 && !value.StartsWith('[')
                || (parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() == value)))
        {
            address = parsed;
            return true;
        }
        address = null!;
        return false;
    }
}
