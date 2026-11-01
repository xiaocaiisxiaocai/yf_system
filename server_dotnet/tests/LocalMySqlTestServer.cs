using MySqlConnector;

namespace Yf.Api.Tests;

/// <summary>
/// Shared bootstrap for every DB-backed test scope: skips when YF_TEST_DATABASE_URL is not set,
/// refuses anything that is not a local MySQL and returns administration connection options.
/// </summary>
internal static class LocalMySqlTestServer
{
    /// <summary>
    /// <see cref="Uri.Host"/> brackets IPv6 literals ("[::1]"), so both spellings must be accepted;
    /// anything else (including other loopback-looking names) is refused.
    /// </summary>
    internal static bool IsLocalHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    internal static MySqlConnectionStringBuilder AdministrationOrSkip(string purpose)
    {
        var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
        return Parse(raw, purpose);
    }

    internal static MySqlConnectionStringBuilder Parse(string raw, string purpose)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
            throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
        if (!IsLocalHost(uri.Host))
            throw new InvalidOperationException($"{purpose} only allow local MySQL");
        var credentials = uri.UserInfo.Split(':', 2);
        return new MySqlConnectionStringBuilder
        {
            Server = uri.Host.Trim('[', ']'),
            Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
            UserID = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
            DateTimeKind = MySqlDateTimeKind.Utc,
            SslMode = MySqlSslMode.None,
        };
    }
}

public sealed class LocalMySqlTestServerTests
{
    [Theory]
    [InlineData("mysql://root:pw@127.0.0.1:3306/ignored", "127.0.0.1")]
    [InlineData("mysql://root:pw@localhost/ignored", "localhost")]
    [InlineData("mysql://root:pw@[::1]:3307/ignored", "::1")]
    public void LoopbackHostsAreAcceptedIncludingBracketedIpv6(string url, string expectedServer)
    {
        var builder = LocalMySqlTestServer.Parse(url, "Test");
        Assert.Equal(expectedServer, builder.Server);
        Assert.Equal("root", builder.UserID);
        Assert.Equal("pw", builder.Password);
    }

    [Fact]
    public void UriHostBracketsIpv6SoTheBareLiteralAloneNeverMatched()
    {
        Assert.Equal("[::1]", new Uri("mysql://u:p@[::1]:3306/x").Host);
        Assert.True(LocalMySqlTestServer.IsLocalHost("[::1]"));
        Assert.True(LocalMySqlTestServer.IsLocalHost("::1"));
    }

    [Theory]
    [InlineData("mysql://root:pw@10.0.0.5:3306/ignored")]
    [InlineData("mysql://root:pw@db.example.com/ignored")]
    [InlineData("mysql://root:pw@[::2]/ignored")]
    [InlineData("mysql://root:pw@127.0.0.2/ignored")]
    public void NonLocalHostsAreRejected(string url) =>
        Assert.Throws<InvalidOperationException>(() => LocalMySqlTestServer.Parse(url, "Test"));

    [Fact]
    public void NonMySqlSchemeIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => LocalMySqlTestServer.Parse("postgres://root@127.0.0.1/x", "Test"));
}
