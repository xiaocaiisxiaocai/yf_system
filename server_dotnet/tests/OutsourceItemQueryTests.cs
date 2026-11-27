using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;

namespace Yf.Api.Tests;

public sealed class OutsourceItemQueryTests
{
    [Fact]
    public void SplitsSingleLineOnWhitespaceAndPunctuation()
    {
        var query = Parse(" A01\tA02, A03，A04;A05；A06、A07 a01 ");
        Assert.Equal(["A01", "A02", "A03", "A04", "A05", "A06", "A07"], query.WorkOrders);
        Assert.Empty(Parse(" ,\t；、 ").WorkOrders!);
    }

    [Fact]
    public void KeepsWholePastedLinesAndIgnoresBlankLines()
    {
        var query = Parse("  A 01, B  \r\n\r\n  C; D  \r\nA 01, B\r\n");
        Assert.Equal(["A 01, B", "C; D"], query.WorkOrders);
        Assert.Equal(["A 01, B"], Parse("  A 01, B\n").WorkOrders);
    }

    [Fact]
    public void AllowsOneHundredDistinctTermsAndSixtyFourCharactersPerTerm()
    {
        var longest = new string('A', 64);
        var terms = Enumerable.Range(1, 99).Select(i => $"WO-{i}").Append(longest).ToArray();
        Assert.Equal(terms, Parse(string.Join(',', terms.Concat(terms))).WorkOrders);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsTooManyTermsOrAnOverlongTerm(bool tooManyTerms)
    {
        var value = tooManyTerms ? string.Join(',', Enumerable.Range(1, 101)) : new string('A', 65);
        var error = Assert.Throws<ApiException>(() => Parse(value));
        Assert.Equal(400, error.Status);
        Assert.Equal("请求参数错误: workOrders", error.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"quantity":2}""")]
    [InlineData("""{"quantity":"2","quantity":"3"}""")]
    public void ColumnFiltersRequireAnObjectWithDistinctKeysAndStringValues(string json)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = QueryString.Create("columnFilters", json);
        var error = Assert.Throws<ApiException>(() => OutsourceItemQuery.From(request));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void MaximumTermsTranslateToEscapedSqlDisjunctionWithoutADatabase()
    {
        var options = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql("Server=127.0.0.1;Database=offline", EfDb.ServerVersion).Options;
        using var context = new YfDbContext(options);
        var terms = Enumerable.Range(1, 98).Select(i => $"WO-{i}").Concat(["50%", "A_B"]).ToArray();
        var sql = context.OutsourceItems.Where(OutsourceItemService.MatchWorkOrders(terms)).ToQueryString();

        Assert.Equal(100, sql.Split(" LIKE ", StringSplitOptions.None).Length - 1);
        Assert.Equal(99, sql.Split(" OR ", StringSplitOptions.None).Length - 1);
        Assert.Contains(@"LIKE '%50\\%%' ESCAPE '\\'", sql);
        Assert.Contains(@"LIKE '%A\\_B%' ESCAPE '\\'", sql);
    }

    private static OutsourceItemQuery Parse(string value)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = QueryString.Create("workOrders", value);
        return OutsourceItemQuery.From(request);
    }
}
