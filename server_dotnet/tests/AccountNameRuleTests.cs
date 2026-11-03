using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Tests;

public sealed class AccountNameRuleTests
{
    [Theory]
    [InlineData("zhuhui.cai")]
    [InlineData("Robert.Lee")]
    [InlineData("a.b.c")]
    [InlineData("employee_1")]
    [InlineData("FANUC001")]
    public void DottedAndPlainAccountNamesAreAccepted(string name)
    {
        Assert.True(AccountNameRule.IsValid(name));
        AdminValidation.EmployeeNo(name);
        Assert.Equal(name, OemValidation.EmployeeNo(name));
    }

    [Theory]
    [InlineData(".lee")]
    [InlineData("lee.")]
    [InlineData("robert..lee")]
    [InlineData("ab")]
    [InlineData("robert-lee")]
    [InlineData("robert lee")]
    [InlineData("王小明")]
    public void MisplacedDotsAndOtherCharactersAreRejected(string name)
    {
        Assert.False(AccountNameRule.IsValid(name));
        Assert.Equal(AccountNameRule.Message, Assert.Throws<ApiException>(() => AdminValidation.EmployeeNo(name)).Message);
        Assert.Equal(AccountNameRule.Message, Assert.Throws<ApiException>(() => OemValidation.EmployeeNo(name)).Message);
    }

    [Fact]
    public void LengthCountsTheDots()
    {
        Assert.True(AccountNameRule.IsValid("a.b"));
        Assert.True(AccountNameRule.IsValid(new string('a', 15) + "." + new string('b', 16)));
        Assert.False(AccountNameRule.IsValid(new string('a', 16) + "." + new string('b', 16)));
    }
}
