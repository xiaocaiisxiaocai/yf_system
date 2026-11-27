using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class ListPaginationTests
{
    [Fact(Timeout = 180_000)]
    public async Task DirectoryPagesReturnFiveHundredRowsAndTheNextRowWithoutOverlap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        await using var context = await host.Service<IDbContextFactory<YfDbContext>>().CreateDbContextAsync(ct);
        var companies = Enumerable.Range(1, 501)
            .Select(i => new OemCompany { Name = $"分页样本{i:D3}", Status = "ACTIVE" }).ToArray();
        context.OemCompanies.AddRange(companies);
        await context.SaveChangesAsync(ct);

        var path = "/api/v1/oem/companies?keyword=" + Uri.EscapeDataString("分页样本");
        static ulong[] Ids(JsonNode page) => page["list"]!.AsArray().Select(row => row!.Id()).ToArray();
        var first = await admin.GetAsync(path + "&pageSize=500", ct).Ok();
        var next = await admin.GetAsync(path + "&pageSize=500&page=2", ct).Ok();
        var defaultPage = await admin.GetAsync(path, ct).Ok();
        Assert.Equal(501UL, first["total"]!.GetValue<ulong>());
        Assert.Equal(500U, first["pageSize"]!.GetValue<uint>());
        Assert.Equal(500, Ids(first).Length);
        Assert.Single(Ids(next));
        Assert.Equal(companies.Select(c => c.Id).OrderDescending(), Ids(first).Concat(Ids(next)));
        Assert.Equal(100U, defaultPage["pageSize"]!.GetValue<uint>());
        Assert.Equal(Ids(first).Take(100), Ids(defaultPage));
    }
}
