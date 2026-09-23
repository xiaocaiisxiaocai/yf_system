using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>
/// Generates the OpenAPI document from the registered endpoints (no database needed) and compares it
/// with tests/Contracts/openapi-v1.json, which web/ turns into TypeScript types. After changing a route,
/// request or response type, regenerate with:  YF_UPDATE_OPENAPI=1 dotnet test --filter OpenApiContractTests
/// </summary>
public sealed class OpenApiContractTests
{
    // Endpoints whose success body is not JSON.
    private static readonly HashSet<string> BinaryEndpoints = new(StringComparer.Ordinal)
    {
        "GET /api/v1/files/{id}/download",
        "GET /api/v1/files/{id}/content",
        "GET /api/v1/files/{id}/media",
        "POST /api/v1/files/batch-download",
        "GET /api/v1/messages/{messageId}/images/{imageId}",
    };

    [Fact]
    public async Task OpenApiDocumentIsCurrent()
    {
        var generated = await GenerateAsync();
        var path = Path.Combine(SourceDirectory(), "Contracts", "openapi-v1.json");
        if (Environment.GetEnvironmentVariable("YF_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, generated, TestContext.Current.CancellationToken);
            return;
        }
        var committed = File.Exists(path)
            ? (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n")
            : "";
        Assert.True(committed == generated,
            "tests/Contracts/openapi-v1.json is stale. Run: YF_UPDATE_OPENAPI=1 dotnet test --filter OpenApiContractTests, "
            + "then regenerate web types with: npm run generate:api-types");
    }

    [Fact]
    public async Task EveryJsonEndpointDeclaresItsResponseType()
    {
        var document = await DocumentAsync();
        var untyped = new List<string>();
        foreach (var (route, item) in document.Paths)
        {
            foreach (var (method, operation) in item.Operations ?? [])
            {
                var key = method.ToString().ToUpperInvariant() + " " + route;
                if (BinaryEndpoints.Contains(key)) continue;
                if (operation.Responses?.ContainsKey("204") == true) continue; // declared as "no content"
                var success = operation.Responses?.Where(response => response.Key.StartsWith('2')).Select(response => response.Value).FirstOrDefault();
                var schema = success?.Content?.TryGetValue("application/json", out var media) == true ? media.Schema : null;
                if (!IsTyped(schema)) untyped.Add(key);
            }
        }
        Assert.True(untyped.Count == 0, "Endpoints without a typed JSON response:\n" + string.Join("\n", untyped.Order()));
    }

    /// <summary>`object` handlers produce an empty schema ({}), which tells clients nothing about the body.</summary>
    private static bool IsTyped(IOpenApiSchema? schema) => schema switch
    {
        null => false,
        OpenApiSchemaReference => true,
        _ when schema.Type is { } type && type.HasFlag(JsonSchemaType.Array) => IsTyped(schema.Items),
        _ when schema.Type is { } type && !type.HasFlag(JsonSchemaType.Object) => true,
        _ => schema.Properties?.Count > 0 || IsTyped(schema.AdditionalProperties) || schema.AllOf?.Count > 0,
    };

    private static async Task<OpenApiDocument> DocumentAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(new AppOptions { WorkerEnabled = false }).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "YF supplier collaboration API", Version = "v1" });
            options.CustomSchemaIds(SchemaId);
            options.SupportNonNullableReferenceTypes();
            options.OperationFilter<ErrorResponseFilter>();
            options.SchemaFilter<PresentPropertiesRequiredFilter>();
        });
        await using var app = builder.Build();
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule();
        // UseEndpoints publishes the mapped routes to the global EndpointDataSource that ApiExplorer reads;
        // the server and background workers are never started.
        app.UseRouting();
        app.UseEndpoints(_ => { });
        return app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    }

    private static async Task<string> GenerateAsync()
    {
        var document = await DocumentAsync();
        using var writer = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(writer));
        return writer.ToString().Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Readable, collision-free schema names: nested types are prefixed with their declaring type.</summary>
    private static string SchemaId(Type type)
    {
        var name = type.Name.Split('`')[0];
        if (type.IsGenericType) name += "Of" + string.Concat(type.GetGenericArguments().Select(SchemaId));
        return type.IsNested && type.DeclaringType is { } parent ? SchemaId(parent) + name : name;
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>
    /// The API always writes every property (null included) except those marked
    /// [JsonIgnore(Condition = WhenWritingNull/WhenWritingDefault)], so all others are required.
    /// </summary>
    private sealed class PresentPropertiesRequiredFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema is not OpenApiSchema concrete || concrete.Properties is not { Count: > 0 } properties) return;
            var omittable = context.Type.GetProperties()
                .Where(property => property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
                    .OfType<System.Text.Json.Serialization.JsonIgnoreAttribute>()
                    .Any(ignore => ignore.Condition is System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                        or System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault))
                .Select(property => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name))
                .ToHashSet(StringComparer.Ordinal);
            concrete.Required = new SortedSet<string>(properties.Keys.Where(name => !omittable.Contains(name)), StringComparer.Ordinal);
        }
    }

    /// <summary>Every operation may fail with the shared { code, message, requestId? } body.</summary>
    private sealed class ErrorResponseFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var schema = context.SchemaGenerator.GenerateSchema(typeof(ApiErrorResponse), context.SchemaRepository);
            operation.Responses ??= new OpenApiResponses();
            operation.Responses["default"] = new OpenApiResponse
            {
                Description = "Error",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
            };
        }
    }
}
