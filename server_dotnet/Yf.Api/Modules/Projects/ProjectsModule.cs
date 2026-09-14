using System.Globalization;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public static class ProjectsModule
{
    public static IServiceCollection AddProjectsModule(this IServiceCollection services)
    {
        services.AddScoped<ProjectService>();
        services.AddScoped<MessageService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<CollaborationService>();
        services.AddSignalR(options => options.EnableDetailedErrors = false);
        services.AddSingleton<RealtimeConnectionRegistry>();
        services.AddSingleton<ProjectRealtimeAuthorizer>();
        services.AddSingleton<ProjectRealtimePublisher>();
        services.AddSingleton<IProjectRealtimePublisher>(provider => provider.GetRequiredService<ProjectRealtimePublisher>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ProjectRealtimePublisher>());
        services.AddSingleton<ProjectActivityService>();
        services.AddSingleton<IProjectAuditCapture>(provider => provider.GetRequiredService<ProjectActivityService>());
        return services;
    }

    public static IEndpointRouteBuilder MapProjectsModule(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/projects", async (HttpContext context, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListAsync(
                conn,
                AccessService.GetCurrent(context),
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryString(context, "keyword"),
                QueryString(context, "status"),
                QueryNullableUlong(context, "supplierId"),
                ct));
        });
        api.MapPost("/projects", async (HttpContext context, ProjectUpsertRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CreateAsync(conn, AccessService.GetCurrent(context), request, Ip(context), ct));
        });
        api.MapGet("/projects/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.DetailAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapPut("/projects/{id:long}", async (HttpContext context, ulong id, ProjectUpsertRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.UpdateAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapDelete("/projects/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.DeleteAsync(conn, AccessService.GetCurrent(context), id, Ip(context), ct);
            return Results.Ok(new { });
        });
        api.MapPut("/projects/{id:long}/status", async (HttpContext context, ulong id, ProjectStatusRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SetStatusAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapPost("/projects/{id:long}/submit", async (HttpContext context, ulong id, ProjectSubmitRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SubmitAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapPost("/projects/{id:long}/confirm", async (HttpContext context, ulong id, ProjectDecisionRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ConfirmAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapPost("/projects/{id:long}/reject", async (HttpContext context, ulong id, ProjectRejectRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.RejectAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapPost("/projects/{id:long}/withdraw", async (HttpContext context, ulong id, ProjectDecisionRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.WithdrawAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapGet("/projects/{id:long}/members", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListMembersAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapPut("/projects/{id:long}/members", async (HttpContext context, ulong id, ProjectMembersRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.SetMembersAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct);
            return Results.Ok(new { });
        });
        api.MapGet("/projects/{id:long}/supplier-members", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListSupplierMembersAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapGet("/projects/{id:long}/summary", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SummaryAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapGet("/projects/{id:long}/activities", async (HttpContext context, ulong id, AppDb db, ProjectActivityService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListAsync(
                conn,
                AccessService.GetCurrent(context),
                id,
                QueryString(context, "type"),
                QueryString(context, "cursor"),
                QueryUlong(context, "pageSize", 20),
                ct));
        });

        api.MapGet("/projects/{id:long}/messages", async (HttpContext context, ulong id, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListAsync(
                conn,
                AccessService.GetCurrent(context),
                id,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryNullableUlong(context, "beforeId"),
                QueryNullableUlong(context, "targetId"),
                ct));
        });
        api.MapGet("/projects/{id:long}/message-receipts", async (HttpContext context, ulong id, AppDb db, MessageService service, CancellationToken ct) =>
        {
            var messageIds = ParseMessageIds(QueryString(context, "ids"));
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ReceiptsAsync(
                conn,
                AccessService.GetCurrent(context),
                id,
                messageIds,
                ct));
        });
        api.MapPost("/projects/{id:long}/messages", async (HttpContext context, ulong id, MessageCreateRequest request, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CreateAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapPost("/messages/read", async (HttpContext context, MarkMessagesReadRequest request, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.MarkReadAsync(conn, AccessService.GetCurrent(context), request, ct);
            return Results.Ok(new { });
        });
        api.MapGet("/messages/{id:long}/reads", async (HttpContext context, ulong id, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ReadsAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapDelete("/messages/{id:long}", async (HttpContext context, ulong id, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.DeleteAsync(conn, AccessService.GetCurrent(context), id, Ip(context), ct);
            return Results.Ok(new { });
        });

        api.MapGet("/dashboard/summary", async (HttpContext context, AppDb db, DashboardService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SummaryAsync(conn, AccessService.GetCurrent(context), ct));
        });
        api.MapGet("/dashboard/pending-projects", async (HttpContext context, AppDb db, DashboardService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.PendingProjectsAsync(
                conn,
                AccessService.GetCurrent(context),
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                ct));
        });
        api.MapGet("/dashboard/messages", async (HttpContext context, AppDb db, DashboardService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.MessagesAsync(
                conn,
                AccessService.GetCurrent(context),
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 10),
                QueryBool(context, "unreadOnly", false),
                ct));
        });
        api.MapGet("/collaboration/summary", async (HttpContext context, AppDb db, CollaborationService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SummaryAsync(conn, AccessService.GetCurrent(context), ct));
        });
        api.MapGet("/collaboration/notifications", async (HttpContext context, AppDb db, CollaborationService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.NotificationsAsync(
                conn,
                AccessService.GetCurrent(context),
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryBool(context, "unreadOnly", false),
                ct));
        });
        api.MapPost("/collaboration/reads", async (HttpContext context, MarkCollaborationReadRequest request, AppDb db, CollaborationService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.MarkReadAsync(conn, AccessService.GetCurrent(context), request, ct);
            return Results.Ok(new { });
        });
        api.MapGet("/supplier-options", async (HttpContext context, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.SupplierOptionsAsync(conn, AccessService.GetCurrent(context), ct));
        });
        api.MapGet("/internal-user-options", async (HttpContext context, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.InternalUserOptionsAsync(conn, AccessService.GetCurrent(context), ct));
        });

        return endpoints;
    }

    private static string? QueryString(HttpContext context, string name) =>
        context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;

    private static ulong QueryUlong(HttpContext context, string name, ulong fallback) =>
        QueryValues.OptionalUInt64(context.Request, name) ?? fallback;

    private static ulong? QueryNullableUlong(HttpContext context, string name) =>
        QueryValues.OptionalUInt64(context.Request, name);

    internal static ulong[] ParseMessageIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var ids = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var part in raw.Split(','))
        {
            var token = part.Trim();
            if (!ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                throw ApiException.BadRequest("请求参数错误: ids");
            }
            if (!seen.Add(id))
            {
                continue;
            }
            if (ids.Count == 500)
            {
                throw ApiException.BadRequest("单次查询数量超过上限");
            }
            ids.Add(id);
        }
        return ids.ToArray();
    }

    private static bool QueryBool(HttpContext context, string name, bool fallback)
    {
        if (!context.Request.Query.TryGetValue(name, out var values))
        {
            return fallback;
        }
        if (values.Count != 1 || !bool.TryParse(values[0], out var value))
        {
            throw ApiException.BadRequest("请求参数错误: " + name);
        }
        return value;
    }

    private static string Ip(HttpContext context) =>
        ClientIp.Resolve(context, context.RequestServices.GetRequiredService<AppOptions>());
}
