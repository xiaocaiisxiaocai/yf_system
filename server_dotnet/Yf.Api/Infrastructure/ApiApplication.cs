using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem;
using Yf.Api.Modules.Projects;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Infrastructure;

public static class ApiApplication
{
    public static async Task<WebApplication?> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var initializeDatabase = args.Contains("--initialize-database", StringComparer.Ordinal);
        var migrateDatabase = args.Contains("--migrate-database", StringComparer.Ordinal);
        var inspectDevelopment = args.Contains("--inspect-development-data", StringComparer.Ordinal);
        var resetDevelopment = args.Contains("--reset-development-data", StringComparer.Ordinal);
        var oemMarkRestored = args.Contains("--oem-mark-restored", StringComparer.Ordinal);
        if (new[] { initializeDatabase, migrateDatabase, inspectDevelopment, resetDevelopment, oemMarkRestored }.Count(value => value) > 1)
            throw new ArgumentException("Choose one database operation.");
        args = args.Where(x => x is not ("--initialize-database" or "--migrate-database" or "--inspect-development-data" or "--reset-development-data" or "--oem-mark-restored")).ToArray();
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
        if (inspectDevelopment || resetDevelopment)
        {
            var result = resetDevelopment
                ? await DevelopmentDataReset.ResetAsync(options, builder.Configuration["confirm-database"], builder.Configuration["confirm-storage-root"])
                : await DevelopmentDataReset.InspectAsync(options);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }));
            return null;
        }
        if (initializeDatabase)
        {
            await EfDatabaseLifecycle.InitializeEmptyAsync(new AppDb(options));
            return null;
        }
        if (oemMarkRestored)
        {
            // After a manual database restore: OEM content stays closed until storage is reconciled.
            await Modules.Oem.Maintenance.OemReconcileService.MarkRestoredAsync(new AppDb(options));
            Console.WriteLine("OEM storage reconciliation required; OEM file access stays closed until the reconcile job completes.");
            return null;
        }
        if (migrateDatabase)
        {
            await EfDatabaseLifecycle.MigrateAsync(new AppDb(options));
            return null;
        }
        await EfDatabaseLifecycle.ValidateReadyAsync(new AppDb(options));
        builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 64L * 1024 * 1024; });
        builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 64L * 1024 * 1024);
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        var efConnectionString = AppDb.BuildConnectionString(options);
        builder.Services.AddPooledDbContextFactory<YfDbContext>(db => db.UseMySql(
            efConnectionString, ServerVersion.AutoDetect(efConnectionString)));
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule().AddOemModule();
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
            .WithOrigins(new Uri(options.WebBaseUrl).GetLeftPart(UriPartial.Authority))
            .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        configure?.Invoke(builder);
        var app = builder.Build();
        var allowedWebOrigin = new Uri(options.WebBaseUrl).GetLeftPart(UriPartial.Authority);
        var webSocketOptions = new WebSocketOptions();
        webSocketOptions.AllowedOrigins.Add(allowedWebOrigin);
        app.UseWebSockets(webSocketOptions);
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
                if (limit is { IsReadOnly: false }
                    && !(HttpMethods.IsPut(context.Request.Method) && context.Request.Path.Value!.Contains("/chunks/", StringComparison.Ordinal)))
                    limit.MaxRequestBodySize = ProjectsModule.IsMessageImageUpload(context.Request)
                        ? MessageService.MultipartRequestLimitBytes
                        : 2L * 1024 * 1024;
            }
            await next(context);
        });
        app.UseCors();
        app.UseMiddleware<IdentityMiddleware>();
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule().MapProjectRealtime().MapOemModule();
        app.MapGet("/health", async (IDbContextFactory<YfDbContext> factory, CancellationToken ct) =>
        {
            try
            {
                await using var context = await factory.CreateDbContextAsync(ct);
                if (!await context.Database.CanConnectAsync(ct)) throw new InvalidOperationException("Database unavailable.");
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
        return app;
    }
}
