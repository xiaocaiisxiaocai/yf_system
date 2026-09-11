using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

var initializeDatabase = args.Contains("--initialize-database", StringComparer.Ordinal);
args = args.Where(x => x != "--initialize-database").ToArray();
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var externalConfig = Environment.GetEnvironmentVariable("YF_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(externalConfig))
{
    if (!Path.IsPathFullyQualified(externalConfig)) throw new InvalidOperationException("YF_CONFIG_PATH must be an absolute file path.");
    builder.Configuration.AddJsonFile(externalConfig, optional: false, reloadOnChange: false);
}
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
var options = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new();
options.Validate();
options.ValidateStorageLocation(builder.Environment.ContentRootPath);
if (initializeDatabase)
{
    await SchemaBootstrap.InitializeEmptyAsync(new AppDb(options));
    return;
}
await SchemaBootstrap.ValidateAsync(new AppDb(options));
DefaultTypeMap.MatchNamesWithUnderscores = true;
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 64L * 1024 * 1024; });
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 64L * 1024 * 1024);
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddSingleton(options).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule();
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(new Uri(options.WebBaseUrl).GetLeftPart(UriPartial.Authority))
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
var app = builder.Build();
app.UseMiddleware<ApiErrorMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.XFrameOptions = "DENY";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false } && !(HttpMethods.IsPut(context.Request.Method) && context.Request.Path.Value!.Contains("/chunks/", StringComparison.Ordinal)))
            limit.MaxRequestBodySize = 2L * 1024 * 1024;
    }
    await next(context);
});
app.UseCors();
app.UseMiddleware<IdentityMiddleware>();
app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule();
app.MapGet("/health", async (AppDb db, CancellationToken ct) =>
{
    try
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
        return Results.Json(new { status = "ok", db = "up" });
    }
    catch { return Results.Json(new { status = "degraded", db = "down" }, statusCode: 503); }
});
app.Map("/api/{**path}", () => Results.Json(new { code = 40401, message = "接口不存在" }, statusCode: 404));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = context.File.Name == "index.html" ? "no-cache" : "public,max-age=3600"
});
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html")))
    app.MapFallbackToFile("index.html");
await app.RunAsync();

public partial class Program;
