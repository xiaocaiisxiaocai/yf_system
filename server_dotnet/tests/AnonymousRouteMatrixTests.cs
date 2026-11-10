using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem;
using Yf.Api.Modules.Outsource;
using Yf.Api.Modules.Oem.Identity;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>
/// The route-to-permission matrix: the versioned contract lists routes but not their authentication. Every
/// registered /api/v1 route is sent through the real <see cref="IdentityMiddleware"/> without credentials; apart
/// from an explicit allowlist, the request must be rejected with 401 before the endpoint runs. The allowlist is
/// compared for equality, so a route that silently becomes anonymous (or an allowlisted one that disappears)
/// fails here. Needs no database: the anonymous path never reaches it.
/// </summary>
public sealed class AnonymousRouteMatrixTests
{
    // Routes that intentionally bypass bearer authentication, each guarded by its own mechanism:
    // credentials in the body, a short-lived media/download cookie, or a one-time download handle.
    private static readonly string[] IntentionallyAnonymous =
    [
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/refresh",
        "GET /api/v1/files/{}/media",
        "GET /api/v1/files/{}/native-download/{}",
        "GET /api/v1/files/batch-download/{}",
        "POST /api/v1/oem/auth/login",
        "POST /api/v1/oem/auth/logout",
        "POST /api/v1/oem/auth/refresh",
        "GET /api/v1/oem/files/{}/download",
    ];

    private static readonly Regex Placeholder = new(@"\{\*{0,2}(?<name>[^}:]+)[^}]*\}", RegexOptions.CultureInvariant);

    [Fact]
    public async Task EveryRouteOutsideTheAllowlistRejectsAnonymousRequestsWith401()
    {
        var routes = RegisteredRoutes().ToArray();
        Assert.True(routes.Length > 100, "The route table looks incomplete.");
        var realms = new IRealmIdentityExtension[] { new OemIdentityExtension(null!) };
        var exempt = new SortedSet<string>(StringComparer.Ordinal);
        var rejected = 0;

        foreach (var (method, template) in routes)
        {
            var key = method + " " + Placeholder.Replace(template.TrimEnd('/'), "{}");
            var reached = false;
            var middleware = new IdentityMiddleware(_ => { reached = true; return Task.CompletedTask; }, realms);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = Concrete(template);
            try
            {
                await middleware.InvokeAsync(context, null!, null!, new IdentityProjectionCache());
            }
            catch (ApiException error)
            {
                Assert.True(error.Status == 401, $"{key} answered {error.Status} instead of 401 to an anonymous request.");
                Assert.Equal(40101, error.Code);
                rejected++;
                continue;
            }
            Assert.True(reached, $"{key} neither rejected nor served the anonymous request.");
            exempt.Add(key);
        }

        Assert.Equal(IntentionallyAnonymous.Order(StringComparer.Ordinal), exempt);
        Assert.Equal(routes.Length, rejected + exempt.Count);
    }

    [Theory]
    [InlineData("GET", "/api/v1/collaboration/live")]
    [InlineData("POST", "/api/v1/collaboration/live/negotiate")]
    [InlineData("GET", "/api/v1/collaboration/live?access_token=")]
    public async Task RealtimeHubRejectsAnonymousConnections(string method, string pathAndQuery)
    {
        var realms = new IRealmIdentityExtension[] { new OemIdentityExtension(null!) };
        var reached = false;
        var middleware = new IdentityMiddleware(_ => { reached = true; return Task.CompletedTask; }, realms);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var split = pathAndQuery.Split('?', 2);
        context.Request.Path = split[0];
        if (split.Length == 2) context.Request.QueryString = new QueryString("?" + split[1]);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            middleware.InvokeAsync(context, null!, null!, new IdentityProjectionCache()));

        Assert.Equal(401, error.Status);
        Assert.False(reached);
    }

    [Theory]
    [InlineData("GET", "/api/v1/files/1/native-download/not-a-valid-handle")]
    [InlineData("GET", "/api/v1/files/batch-download/short")]
    [InlineData("POST", "/api/v1/files/1/media")]
    [InlineData("GET", "/api/v1/files/abc/media")]
    [InlineData("GET", "/api/v1/oem/files/1/download/extra")]
    [InlineData("PUT", "/api/v1/oem/auth/login")]
    public async Task MalformedLookalikesOfAnonymousRoutesAreNotExempt(string method, string path)
    {
        var realms = new IRealmIdentityExtension[] { new OemIdentityExtension(null!) };
        var reached = false;
        var middleware = new IdentityMiddleware(_ => { reached = true; return Task.CompletedTask; }, realms);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            middleware.InvokeAsync(context, null!, null!, new IdentityProjectionCache()));

        Assert.Equal(401, error.Status);
        Assert.False(reached);
    }

    private static IEnumerable<(string Method, string Template)> RegisteredRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(new AppOptions()).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule().AddOemModule().AddOutsourceModule();
        using var app = builder.Build();
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule().MapProjectRealtime().MapOemModule().MapOutsourceModule();
        // The route table stays valid after the application is disposed; endpoints are plain objects.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        return endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => (method, endpoint.RoutePattern.RawText!)))
            .Distinct()
            .ToArray();
    }

    private static string Concrete(string template) => Placeholder.Replace(template, match => match.Groups["name"].Value switch
    {
        "sessionId" => "00000000-0000-0000-0000-000000000001",
        "handle" => new string('a', 32),
        _ => "1",
    });
}
