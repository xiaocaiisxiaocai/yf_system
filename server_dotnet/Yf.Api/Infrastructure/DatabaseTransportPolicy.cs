using MySqlConnector;

namespace Yf.Api.Infrastructure;

internal static class DatabaseTransportPolicy
{
    internal static void Validate(MySqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ConnectionProtocol != MySqlConnectionProtocol.Sockets)
            throw new InvalidOperationException("Database connections must use TCP/IP sockets.");

        var hosts = builder.Server.Split(',', StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("Database server configuration is invalid.");

        if (!hosts.All(IsLiteralLoopback) && builder.SslMode != MySqlSslMode.VerifyFull)
            throw new InvalidOperationException("Remote database connections require SslMode=VerifyFull.");
    }

    private static bool IsLiteralLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host == "127.0.0.1" || host is "::1" or "[::1]";
}
