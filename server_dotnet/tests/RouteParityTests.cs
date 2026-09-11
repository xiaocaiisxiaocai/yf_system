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

public class RouteParityTests
{
    [Fact]
    public async Task EveryRustApiMethodAndPathHasAnAspNetEndpoint()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "yf_server/server/src/handler/mod.rs"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var rust = await File.ReadAllTextAsync(Path.Combine(repository.FullName, "yf_server/server/src/handler/mod.rs"), TestContext.Current.CancellationToken);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match group in Regex.Matches(rust, @"let (public|authed|admin_\w+) = Router::new\(\)([\s\S]*?);"))
        {
            var text = group.Groups[2].Value;
            foreach (Match route in Regex.Matches(text, "\\.route\\(\\s*\"([^\"]+)\"\\s*,"))
            {
                var position = route.Index + route.Length;
                var start = position;
                var depth = 1;
                for (; position < text.Length && depth > 0; position++)
                {
                    if (text[position] == '(') depth++;
                    if (text[position] == ')') depth--;
                }
                foreach (Match method in Regex.Matches(text[start..position], @"\b(get|post|put|delete|patch)\("))
                    expected.Add(Normalize(method.Groups[1].Value.ToUpperInvariant(), "/api/v1" + (group.Groups[1].Value.StartsWith("admin_", StringComparison.Ordinal) ? "/admin" : "") + route.Groups[1].Value));
            }
        }
        Assert.True(expected.Count > 70, "Rust route extraction must cover the complete source router.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(new AppOptions()).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule();
        await using var app = builder.Build();
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule();
        var actual = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(method => Normalize(method, endpoint.RoutePattern.RawText!)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static string Normalize(string method, string path) => method + " " + Regex.Replace(path.TrimEnd('/'), @"\{[^}]+\}", "{}");
}
