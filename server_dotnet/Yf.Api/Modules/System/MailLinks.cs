using System.Net;
using System.Net.Sockets;
using System.Text;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Notification links always point at <c>App:WebBaseUrl</c> (the address vendors use from outside). Internal staff may
/// also be on the company network, so their mail gets a second link through the first private-network origin in
/// <c>App:AdditionalWebOrigins</c>: an action line becomes "外网访问：URL" + "内网访问：URL", and an indented list URL
/// gets an "内网访问" sibling. Vendor mail and deployments without a private origin are left untouched.
/// Applied at send time, so the outbox keeps one body per message.
/// </summary>
internal static class MailLinks
{
    internal const string External = "外网访问";
    internal const string Intranet = "内网访问";

    internal static string ForRecipient(string body, AppOptions options, bool internalStaff)
    {
        if (!internalStaff || IntranetOrigin(options) is not { } intranet) return body;
        var external = options.WebBaseUrl.TrimEnd('/');
        if (IsPrivate(external)) return body;
        var output = new StringBuilder(body.Length * 2);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var indent = line[..(line.Length - line.TrimStart().Length)];
            var trimmed = line.Trim();
            var separator = trimmed.LastIndexOf('：');
            var url = separator >= 0 ? trimmed[(separator + 1)..].Trim() : trimmed;
            if (Belongs(url, external))
            {
                var intranetUrl = intranet + url[external.Length..];
                output.Append(indent).Append(External).Append('：').Append(url).Append('\n')
                    .Append(indent).Append(Intranet).Append('：').Append(intranetUrl);
            }
            else output.Append(line);
            if (index < lines.Length - 1) output.Append('\n');
        }
        return output.ToString();
    }

    internal static bool IsInternalStaff(string? recipientUserType, string? recipientRealm, ulong? oemTransferId) =>
        recipientUserType == UserTypes.Internal
        || oemTransferId is not null && recipientRealm == Oem.Common.OemRealms.Internal;

    private static string? IntranetOrigin(AppOptions options) =>
        options.AdditionalWebOrigins.Select(origin => origin.TrimEnd('/')).FirstOrDefault(IsPrivate);

    private static bool Belongs(string url, string origin) =>
        url.StartsWith(origin, StringComparison.OrdinalIgnoreCase)
        && (url.Length == origin.Length || url[origin.Length] is '/' or '?' or '#');

    /// <summary>Loopback, RFC 1918/link-local IPv4, unique-local IPv6, or a single-label host name.</summary>
    internal static bool IsPrivate(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) return !uri.Host.Contains('.');
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
    }
}
