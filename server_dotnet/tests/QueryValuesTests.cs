using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

public class QueryValuesTests
{
    [Theory]
    [InlineData("?departmentId=")]
    [InlineData("?departmentId=abc")]
    [InlineData("?departmentId=-1")]
    [InlineData("?departmentId=18446744073709551616")]
    [InlineData("?departmentId=1&departmentId=2")]
    public void InvalidPresentFilterDoesNotBecomeAnUnfilteredQuery(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        Assert.Throws<ApiException>(() => QueryValues.OptionalUInt64(context.Request, "departmentId"));
    }

    [Fact]
    public void OmittedFilterIsOptionalAndValidPagingStillClamps()
    {
        var context = new DefaultHttpContext();
        Assert.Null(QueryValues.OptionalUInt64(context.Request, "departmentId"));
        Assert.Equal((1UL, 20U, 0UL), QueryValues.Page(context.Request));
        context.Request.QueryString = new QueryString("?page=0&pageSize=101&departmentId=18446744073709551615");
        Assert.Equal(ulong.MaxValue, QueryValues.OptionalUInt64(context.Request, "departmentId"));
        Assert.Equal((1UL, 100U, 0UL), QueryValues.Page(context.Request));
    }
}
