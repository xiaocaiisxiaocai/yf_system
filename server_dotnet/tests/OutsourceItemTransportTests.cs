using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;

namespace Yf.Api.Tests;

public sealed class OutsourceItemTransportTests
{
    [Fact]
    public void QueryStringAndJsonBodyParseTheSameFiltersAndPagination()
    {
        Dictionary<string, string> parameters = new()
        {
            ["page"] = "3", ["pageSize"] = "12", ["vendorId"] = "21", ["typeId"] = "7",
            ["status"] = "NORMAL", ["sendStatus"] = "SENT", ["keyword"] = "光学 & A+B%",
            ["dateFrom"] = "2026-11-01", ["dateTo"] = "2026-11-26",
            ["includeClosed"] = "true", ["sendQueue"] = "false", ["workOrders"] = "甲\n乙",
            ["columnFilters"] = """{"customer":"研发","modifiedAt":"2026-11-26"}""",
            ["columnFilterTimeZone"] = "Asia/Shanghai",
        };
        var request = new DefaultHttpContext().Request;
        request.QueryString = QueryString.Create(parameters.Select(p => KeyValuePair.Create<string, string?>(p.Key, p.Value)));
        var body = OutsourceModule.BodyParameters(parameters);
        var fromQuery = OutsourceItemQuery.From(request);
        var fromBody = OutsourceItemQuery.From(body);

        Assert.Equal(JsonSerializer.Serialize(fromQuery), JsonSerializer.Serialize(fromBody));
        Assert.Equal(QueryValues.Page(request), QueryValues.Page(body));
        Assert.Equal((3UL, 12U, 24UL), QueryValues.Page(body));
    }

    [Theory]
    [InlineData("vendorId", "-1")]
    [InlineData("includeClosed", "yes")]
    [InlineData("columnFilters", "{")]
    public void JsonBodyUsesTheSameValidationErrorsAsTheQueryString(string key, string value)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = QueryString.Create(key, value);
        var body = OutsourceModule.BodyParameters(new() { [key] = value });
        var queryError = Assert.Throws<ApiException>(() => OutsourceItemQuery.From(request));
        var bodyError = Assert.Throws<ApiException>(() => OutsourceItemQuery.From(body));
        Assert.Equal(400, bodyError.Status);
        Assert.Equal(queryError.Message, bodyError.Message);
    }
}
