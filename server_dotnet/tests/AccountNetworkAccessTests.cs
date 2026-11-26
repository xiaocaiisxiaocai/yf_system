using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class AccountNetworkAccessTests
{
    private const string Networks = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1,fd00::/8,2001:db8::123";

    [Theory]
    [InlineData("10.255.255.255", false)]
    [InlineData("11.0.0.0", true)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.0", true)]
    [InlineData("192.168.1.2", false)]
    [InlineData("::ffff:192.168.1.2", false)]
    [InlineData("::ffff:203.0.113.7", true)]
    [InlineData("::1", false)]
    [InlineData("fdba::1234", false)]
    [InlineData("2001:db8::123", false)]
    [InlineData("2001:db8::124", true)]
    [InlineData("not-an-ip", true)]
    [InlineData("10.1", true)]
    [InlineData("", true)]
    public void SourceMustBelongToAnInternalNetwork(string source, bool external) =>
        Assert.Equal(external, AccountNetworkAccess.IsExternal(new(source, "intranet"), Networks, ""));

    [Theory]
    [InlineData("PUBLIC.Example:8090", true)]
    [InlineData("public.example:8091", false)]
    [InlineData("public.example", false)]
    [InlineData("183.238.37.108:8090", true)]
    [InlineData("[2001:DB8::1]:8443", true)]
    public void ExternalHostTakesPrecedenceOverInternalIp(string host, bool external) =>
        Assert.Equal(external, AccountNetworkAccess.IsExternal(new("10.0.0.1", host), Networks,
            "public.example:8090,183.238.37.108:8090,[2001:db8::1]:8443"));

    [Fact]
    public void NormalizesMappedCidrsAndFailsClosedWithMissingOrInvalidStoredSettings()
    {
        var origin = new RequestNetworkOrigin("192.168.1.2", "intranet");
        Assert.False(AccountNetworkAccess.IsExternal(origin, "::ffff:192.168.0.0/112", ""));
        Assert.True(AccountNetworkAccess.IsExternal(origin, null, ""));
        Assert.True(AccountNetworkAccess.IsExternal(origin, Networks, null));
        Assert.True(AccountNetworkAccess.IsExternal(origin, Networks + ",invalid", ""));
        Assert.True(AccountNetworkAccess.IsExternal(origin, Networks, "missing-port"));
    }

    [Theory]
    [InlineData(RuntimeSettings.InternalNetworksKey, "10.0.0.0/33", "内网网段")]
    [InlineData(RuntimeSettings.InternalNetworksKey, "::1/-1", "内网网段")]
    [InlineData(RuntimeSettings.InternalNetworksKey, "192.168.1.2/24/7", "内网网段")]
    [InlineData(RuntimeSettings.InternalNetworksKey, "10.1", "内网网段")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "https://example.org:443", "外网访问地址")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "example.org", "外网访问地址")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "example.org:0", "外网访问地址")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "example.org:65536", "外网访问地址")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "2001:db8::1:443", "外网访问地址")]
    [InlineData(RuntimeSettings.ExternalHostsKey, "999.1.2.3:443", "外网访问地址")]
    public void InvalidSettingsReturnAnActionableBadRequest(string key, string value, string label)
    {
        var error = Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, value));
        Assert.Equal(400, error.Status);
        Assert.Contains(label, error.Message);
    }

    [Fact]
    public void MultilineSettingsAreStoredAsCommaSeparatedLists()
    {
        Assert.Equal("10.0.0.0/8,::1", SystemService.NormalizeConfig(RuntimeSettings.InternalNetworksKey, " 10.0.0.0/8\r\n\r\n ::1 "));
        Assert.Equal("Public.Example:8090,[::1]:8091", SystemService.NormalizeConfig(RuntimeSettings.ExternalHostsKey,
            " Public.Example:8090\r\npublic.example:8090, [::1]:8091 "));
    }
}
