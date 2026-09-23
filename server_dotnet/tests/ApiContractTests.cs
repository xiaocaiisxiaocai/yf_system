using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public class ApiContractTests
{
    [Fact]
    public async Task RegisteredEndpointsMatchVersionedApiContract()
    {
        var expected = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Contracts/api-v1.json"), TestContext.Current.CancellationToken))!;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(new AppOptions()).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule();
        await using var app = builder.Build();
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule().MapProjectRealtime();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/collaboration/live");
        // SignalR validates transport methods inside its request delegate rather than HttpMethodMetadata.
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/collaboration/live/negotiate");
        var actual = endpoints.Where(endpoint => !endpoint.RoutePattern.RawText!.StartsWith("/api/v1/collaboration/live", StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(method => Normalize(method, endpoint.RoutePattern.RawText!)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static string Normalize(string method, string path) => method + " " + Regex.Replace(path.TrimEnd('/'), @"\{[^}]+\}", "{}");
}
