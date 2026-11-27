using System.Net;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

public sealed class DashboardAuthorizationTests
{
    [Fact(Timeout = 180_000)]
    public async Task AllDashboardReadsRequireBothDashboardAndProjectList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        const string password = "Dashboard#2026";
        (string Account, string[] Grants, HttpStatusCode Expected)[] cases =
        [
            ("dash_none", [], HttpStatusCode.Forbidden),
            ("dash_only", ["dashboard"], HttpStatusCode.Forbidden),
            ("dash_projects", ["project:list"], HttpStatusCode.Forbidden),
            ("dash_both", ["dashboard", "project:list"], HttpStatusCode.OK),
        ];
        foreach (var (account, grants, expected) in cases)
        {
            await host.CreateInternalUserAsync(account, password, grants, null, ct);
            var client = await host.LoginInternalAsync(account, password, ct);
            await client.GetAsync("/api/v1/dashboard/summary", ct).Status(expected);
            await client.GetAsync("/api/v1/dashboard/pending-projects", ct).Status(expected);
            await client.GetAsync("/api/v1/dashboard/messages", ct).Status(expected);
        }
    }
}
