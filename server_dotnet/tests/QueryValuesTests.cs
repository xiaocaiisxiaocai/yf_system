using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

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
        Assert.Equal((1UL, 100U, 0UL), QueryValues.Page(context.Request));
        context.Request.QueryString = new QueryString("?page=0&pageSize=501&departmentId=18446744073709551615");
        Assert.Equal(ulong.MaxValue, QueryValues.OptionalUInt64(context.Request, "departmentId"));
        Assert.Equal((1UL, 500U, 0UL), QueryValues.Page(context.Request));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public void PageSizesUseTheSameOffsetAcrossLists(int requested)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString($"?page=3&pageSize={requested}");
        var size = (uint)requested;
        Assert.Equal((3UL, size, 2UL * size), QueryValues.Page(request));
        Assert.Equal((3UL, (ulong)size), ProjectJson.ClampPage(3, size));
    }

    [Fact]
    public void LargePageNumbersCannotWrapToTheBeginning()
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString($"?page={ulong.MaxValue}&pageSize=500");
        var (page, size, offset) = QueryValues.Page(request);
        Assert.Equal(500U, size);
        Assert.Equal(ulong.MaxValue / size, page);
        Assert.Equal(checked((page - 1) * size), offset);
        Assert.True(offset > int.MaxValue);
    }

    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData("50%", @"%50\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"C:\dir", @"%C:\\dir%")]
    [InlineData(@"\%_", @"%\\\%\_%")]
    public void ContainsPatternMatchesUserInputLiterally(string term, string expected)
    {
        Assert.Equal(expected, QueryValues.ContainsPattern(term));
    }

    [Fact]
    public void ContainsPatternIsSentWithAnExplicitEscapeClause()
    {
        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql("Server=127.0.0.1;Database=offline", new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        using var context = new YfDbContext(options);
        var pattern = QueryValues.ContainsPattern("50%");
        var sql = context.Users.Where(user => EF.Functions.Like(user.RealName, pattern, QueryValues.LikeEscape)).ToQueryString();
        // ToQueryString inlines the parameter as a MySQL literal, where \\ denotes one backslash.
        Assert.Contains(@"LIKE '%50\\%%' ESCAPE '\\'", sql);
    }
}
