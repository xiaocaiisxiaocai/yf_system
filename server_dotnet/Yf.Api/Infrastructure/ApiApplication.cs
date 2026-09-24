using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Identity;
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
        var checkDevelopmentReadiness = args.Contains("--check-development-readiness", StringComparer.Ordinal);
        if (new[] { initializeDatabase, migrateDatabase, inspectDevelopment, resetDevelopment, checkDevelopmentReadiness }.Count(value => value) > 1)
        {
            if (checkDevelopmentReadiness)
            {
                WriteDevelopmentReadiness(DevelopmentReadiness.ConfigurationFailure("operation-conflict"));
                return null;
            }
            throw new ArgumentException("Choose one database operation.");
        }
        args = args.Where(x => x is not ("--initialize-database" or "--migrate-database" or "--inspect-development-data" or "--reset-development-data" or "--check-development-readiness")).ToArray();
        if (checkDevelopmentReadiness)
        {
            DevelopmentReadinessReport result;
            try
            {
                var readinessBuilder = CreateConfiguredBuilder(args);
                configure?.Invoke(readinessBuilder);
                var readinessOptions = readinessBuilder.Configuration.GetSection("App").Get<AppOptions>() ?? new();
                using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                result = await DevelopmentReadiness.CheckAsync(
                    readinessOptions, readinessBuilder.Environment.ContentRootPath, readinessTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                result = DevelopmentReadiness.TimeoutFailure();
            }
            catch
            {
                // This diagnostic is intentionally safe to paste into tickets: never emit
                // configuration values, paths, connection strings, or exception messages.
                result = DevelopmentReadiness.ConfigurationFailure("configuration-invalid");
            }
            WriteDevelopmentReadiness(result);
            return null;
        }
        var builder = CreateConfiguredBuilder(args);
        var options = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new();
        options.Validate();
        options.ValidateStorageLocation(builder.Environment.ContentRootPath);
        if (inspectDevelopment || resetDevelopment)
        {
            var result = resetDevelopment
                ? await DevelopmentDataReset.ResetAsync(options, builder.Configuration["confirm-database"], builder.Configuration["confirm-storage-root"])
                : await DevelopmentDataReset.InspectAsync(options);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return null;
        }
        if (initializeDatabase)
        {
            await EfDatabaseLifecycle.InitializeEmptyAsync(new AppDb(options));
            return null;
        }
        if (migrateDatabase)
        {
            await EfDatabaseLifecycle.MigrateAsync(new AppDb(options));
            return null;
        }
        await EfDatabaseLifecycle.PrepareStartupAsync(options);
        builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 64L * 1024 * 1024; });
        builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 64L * 1024 * 1024);
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options).AddSingleton<AppDb>().AddSingleton<AccessService>().AddSingleton<AuditService>();
        builder.Services.AddResponseCompression(compression =>
        {
            compression.EnableForHttps = true;
            compression.MimeTypes = ResponseCompressionDefaults.MimeTypes
                .Concat(["application/javascript", "text/css", "application/wasm"]);
        });
        var efConnectionString = AppDb.BuildConnectionString(options);
        builder.Services.AddPooledDbContextFactory<YfDbContext>(db => db.UseMySql(
            efConnectionString, EfDb.ServerVersion));
        builder.Services.AddIdentityModule().AddAdminModule().AddProjectsModule().AddFilesModule().AddSystemModule();
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
            // Native downloads are navigated in a hidden same-origin frame so the SPA can read a JSON
            // error page and show its reason; every other response stays unframeable.
            var sameOriginFrame = IdentityMiddleware.IsNativeDownloadRequest(context.Request);
            context.Response.Headers.XFrameOptions = sameOriginFrame ? "SAMEORIGIN" : "DENY";
            // HTTPS-only deployments: keep browsers from ever downgrading to HTTP. Subdomains are
            // left out because other intranet services may still share the parent domain over HTTP.
            if (context.Request.IsHttps) context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.ContentSecurityPolicy = sameOriginFrame ? "frame-ancestors 'self'" : "frame-ancestors 'none'";
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
        app.MapIdentityModule().MapAdminModule().MapProjectsModule().MapFilesModule().MapSystemModule().MapProjectRealtime();
        var healthProbe = new DatabaseHealthProbe();
        app.MapGet("/health", async (IDbContextFactory<YfDbContext> factory, CancellationToken ct) =>
            await healthProbe.IsUpAsync(factory, ct)
                ? Results.Json(new { status = "ok", db = "up" })
                : Results.Json(new { status = "degraded", db = "down" }, statusCode: 503));
        app.Map("/api/{**path}", () => Results.Json(new ApiErrorResponse(40401, "接口不存在"), statusCode: 404));
        // Compress only the SPA/static branch. API responses deliberately bypass compression.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), branch =>
            branch.UseResponseCompression());
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // Vite emits content-hashed file names under /assets, so those never change in place.
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                context.File.Name == "index.html" ? "no-cache"
                : context.Context.Request.Path.StartsWithSegments("/assets") ? "public,max-age=31536000,immutable"
                : "public,max-age=3600"
        });
        if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html")))
            app.MapFallbackToFile("index.html");
        return app;
    }

    private static WebApplicationBuilder CreateConfiguredBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        var externalConfig = Environment.GetEnvironmentVariable("YF_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(externalConfig))
        {
            if (!Path.IsPathFullyQualified(externalConfig)) throw new InvalidOperationException("YF_CONFIG_PATH must be an absolute file path.");
            builder.Configuration.AddJsonFile(externalConfig, optional: false, reloadOnChange: false);
        }
        builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
        return builder;
    }

    private static void WriteDevelopmentReadiness(DevelopmentReadinessReport result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
        Environment.ExitCode = result.ReadyForStartup ? 0 : 1;
    }
}

/// <summary>
/// The anonymous health endpoint must not let callers drive one database connection per request.
/// Concurrent probes share one in-flight check, and a healthy result is reused briefly. A failed
/// check is not cached, so startup polling sees the database as soon as it becomes reachable.
/// </summary>
internal sealed class DatabaseHealthProbe(Func<DateTime>? clock = null)
{
    internal static readonly TimeSpan HealthyCacheDuration = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTime> clock = clock ?? (() => DateTime.UtcNow);
    private long healthyUntilTicks;

    internal async Task<bool> IsUpAsync(IDbContextFactory<YfDbContext> factory, CancellationToken ct) =>
        await IsUpAsync(async token =>
        {
            await using var context = await factory.CreateDbContextAsync(token);
            return await context.Database.CanConnectAsync(token);
        }, ct);

    internal async Task<bool> IsUpAsync(Func<CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        if (clock().Ticks < Interlocked.Read(ref healthyUntilTicks)) return true;
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (clock().Ticks < Interlocked.Read(ref healthyUntilTicks)) return true;
            bool up;
            try { up = await probe(ct); }
            catch { up = false; }
            Interlocked.Exchange(ref healthyUntilTicks, up ? (clock() + HealthyCacheDuration).Ticks : 0);
            return up;
        }
        finally { gate.Release(); }
    }
}
