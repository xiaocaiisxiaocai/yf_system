using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public sealed class DatabaseTransportTests
{
    [Theory]
    [InlineData("db.example.invalid", MySqlSslMode.Disabled)]
    [InlineData("192.0.2.10", MySqlSslMode.Preferred)]
    [InlineData("db.example.invalid", MySqlSslMode.Required)]
    [InlineData("db.example.invalid", MySqlSslMode.VerifyCA)]
    [InlineData("localhost.example.invalid", MySqlSslMode.Preferred)]
    [InlineData("127.0.0.2", MySqlSslMode.Preferred)]
    public void RemoteTcpRejectsModesWithoutFullCertificateVerification(string server, MySqlSslMode sslMode)
    {
        var builder = Builder(server, sslMode);

        var error = Assert.Throws<InvalidOperationException>(() => DatabaseTransportPolicy.Validate(builder));

        Assert.Equal("Remote database connections require SslMode=VerifyFull.", error.Message);
    }

    [Theory]
    [InlineData("localhost", MySqlSslMode.Disabled)]
    [InlineData("LOCALHOST", MySqlSslMode.Preferred)]
    [InlineData("127.0.0.1", MySqlSslMode.None)]
    [InlineData("::1", MySqlSslMode.Required)]
    [InlineData("[::1]", MySqlSslMode.Disabled)]
    [InlineData("localhost, 127.0.0.1, ::1", MySqlSslMode.Disabled)]
    public void LiteralLoopbackTcpHostsAllowLocalTestModes(string server, MySqlSslMode sslMode)
    {
        DatabaseTransportPolicy.Validate(Builder(server, sslMode));
    }

    [Fact]
    public void MixedHostListIsRemoteAndRequiresVerifyFull()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseTransportPolicy.Validate(
            Builder("localhost,db.example.invalid", MySqlSslMode.Preferred)));

        DatabaseTransportPolicy.Validate(Builder("localhost,db.example.invalid", MySqlSslMode.VerifyFull));
    }

    [Fact]
    public void RemoteTcpAllowsVerifyFull()
    {
        DatabaseTransportPolicy.Validate(Builder("db.example.invalid", MySqlSslMode.VerifyFull));
    }

    [Theory]
    [InlineData(MySqlConnectionProtocol.Pipe)]
    [InlineData(MySqlConnectionProtocol.UnixSocket)]
    [InlineData(MySqlConnectionProtocol.SharedMemory)]
    public void NonTcpProtocolsCannotBypassTheRemoteTransportPolicy(MySqlConnectionProtocol protocol)
    {
        var builder = Builder("db.example.invalid", MySqlSslMode.VerifyFull);
        builder.ConnectionProtocol = protocol;

        var error = Assert.Throws<InvalidOperationException>(() => DatabaseTransportPolicy.Validate(builder));

        Assert.Equal("Database connections must use TCP/IP sockets.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost,")]
    [InlineData("localhost,,127.0.0.1")]
    public void MissingHostEntriesAreRejected(string server)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseTransportPolicy.Validate(
            Builder(server, MySqlSslMode.VerifyFull)));

        Assert.Equal("Database server configuration is invalid.", error.Message);
    }

    [Fact]
    public void RejectionDoesNotExposeConnectionDetails()
    {
        var builder = Builder("sensitive-host.example.invalid", MySqlSslMode.Preferred);
        builder.UserID = "sensitive-user";
        builder.Password = "sensitive-password";

        var error = Assert.Throws<InvalidOperationException>(() => DatabaseTransportPolicy.Validate(builder));

        Assert.DoesNotContain("sensitive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(builder.ConnectionString, error.Message, StringComparison.Ordinal);
    }

    private static MySqlConnectionStringBuilder Builder(string server, MySqlSslMode sslMode) => new()
    {
        Server = server,
        ConnectionProtocol = MySqlConnectionProtocol.Sockets,
        SslMode = sslMode
    };
}
