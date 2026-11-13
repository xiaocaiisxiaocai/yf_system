using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Tests;

/// <summary>App:Ipms defaults for the IPMS MES module (the settings page overrides address, account, password and time).</summary>
public sealed class MesMirrorOptionsTests
{
    private const string Password = "s3cret-PW";

    private static IpmsOptions Valid() => new()
    {
        MesBaseUrl = "http://127.0.0.1:6001",
        MesAccount = "acct-1",
        MesPassword = Password,
    };

    [Fact]
    public void DefaultsMatchTheDesign()
    {
        var options = new IpmsOptions();
        Assert.Equal(("03:00", 100, 2, 180, 50), (options.MesSyncTime, options.MesPageSize, options.MesPageDelaySeconds,
            options.MesPageTimeoutSeconds, options.MesMinRatioPercent));
    }

    [Fact]
    public void AcceptsACompleteMesMirrorConfiguration() => Valid().Validate();

    [Fact]
    public void AccountAndPasswordAreOptionalBecauseThePageCanSetThem()
    {
        new IpmsOptions().Validate();
        new IpmsOptions { Mode = "MesMirror" }.Validate();
    }

    public static TheoryData<string, string> InvalidSettings => new()
    {
        { nameof(IpmsOptions.MesBaseUrl), "not a url" },
        { nameof(IpmsOptions.MesBaseUrl), "ftp://127.0.0.1" },
        { nameof(IpmsOptions.MesSyncTime), "3am" },
        { nameof(IpmsOptions.MesSyncTime), "25:00" },
        { nameof(IpmsOptions.MesPageSize), "0" },
        { nameof(IpmsOptions.MesPageSize), "201" },
        { nameof(IpmsOptions.MesPageDelaySeconds), "-1" },
        { nameof(IpmsOptions.MesPageDelaySeconds), "61" },
        { nameof(IpmsOptions.MesPageTimeoutSeconds), "9" },
        { nameof(IpmsOptions.MesPageTimeoutSeconds), "601" },
        { nameof(IpmsOptions.MesMinRatioPercent), "0" },
        { nameof(IpmsOptions.MesMinRatioPercent), "101" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void RejectsInvalidMesMirrorSettingsWithoutLeakingThePassword(string property, string value)
    {
        var options = Valid();
        var info = typeof(IpmsOptions).GetProperty(property)!;
        info.SetValue(options, info.PropertyType == typeof(int) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value);

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
        Assert.Contains(property, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBaseUrlIsAllowedBecauseThePageCanSetIt()
    {
        var options = Valid();
        options.MesBaseUrl = "";
        options.Validate();
    }

    [Fact]
    public void MesSettingsAreNotCheckedWhenTestDataIsUsed()
    {
        new IpmsOptions { Mode = "Fake", FakeDataPath = Path.Combine(Path.GetTempPath(), "fake.json"), MesPageSize = 0, MesBaseUrl = "junk" }.Validate();
    }

    [Fact]
    public void UnknownModeIsStillRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new IpmsOptions { Mode = "X" }.Validate());
    }
}
