using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public static class ProjectsModule
{
    public static IServiceCollection AddProjectsModule(this IServiceCollection services)
    {
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectGroupService>();
        services.AddScoped<ProjectGroupStatusService>();
        services.AddScoped<ProjectCopyService>();
        services.AddScoped<ProjectDictionaryService>();
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

        api.MapGet("/project-groups", async (HttpContext context, AppDb db, ProjectGroupService service, CancellationToken ct) =>
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
        api.MapPost("/project-groups", async (HttpContext context, ProjectUpsertRequest request, AppDb db, ProjectGroupService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CreateAsync(conn, AccessService.GetCurrent(context), request, Ip(context), ct));
        });
        api.MapGet("/project-groups/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectGroupService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.DetailAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapPut("/project-groups/{id:long}", async (HttpContext context, ulong id, ProjectUpsertRequest request, AppDb db, ProjectGroupService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.UpdateAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapDelete("/project-groups/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectGroupService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.DeleteAsync(conn, AccessService.GetCurrent(context), id, Ip(context), ct);
            return Results.Ok(new { });
        });
        api.MapPost("/project-groups/{id:long}/projects", async (HttpContext context, ulong id, SubprojectUpsertRequest request, AppDb db, ProjectGroupService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CreateSubprojectAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });

        api.MapPost("/projects/{id:long}/copy", async (HttpContext context, ulong id, ProjectCopyRequest request, AppDb db, ProjectCopyService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CopyAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapGet("/projects/{id:long}/copy-history", async (HttpContext context, ulong id, AppDb db, ProjectCopyService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.HistoryAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapGet("/project-copies/{copyId:long}/files", async (HttpContext context, ulong copyId, AppDb db, ProjectCopyService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.FileHistoryAsync(conn, AccessService.GetCurrent(context), copyId,
                QueryUlong(context, "page", 1), QueryUlong(context, "pageSize", 20), ct));
        });
        api.MapGet("/projects/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.DetailAsync(conn, AccessService.GetCurrent(context), id, ct));
        });
        api.MapPut("/projects/{id:long}", async (HttpContext context, ulong id, SubprojectUpsertRequest request, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.UpdateSubprojectAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
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
        api.MapPost("/projects/{id:long}/messages", async (HttpContext context, ulong id, AppDb db, MessageService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var actor = AccessService.GetCurrent(context);
            if (context.Request.HasFormContentType)
            {
                // Reject unauthorized or non-writable projects before ASP.NET buffers a large form.
                await service.EnsureCreateAllowedAsync(conn, actor, id, ct);
                IFormCollection form;
                try { form = await context.Request.ReadFormAsync(ct); }
                catch (InvalidDataException)
                {
                    throw ApiException.BadRequest("留言图片表单格式不正确或超过大小上限");
                }
                if (form.Files.Any(file => file.Name != "images"))
                    throw ApiException.BadRequest("图片表单字段必须命名为 images");
                if (form.TryGetValue("content", out var contents) && contents.Count > 1)
                    throw ApiException.BadRequest("content 表单字段只能出现一次");
                var request = new MessageCreateRequest { Content = contents.Count == 0 ? null : contents[0] };
                return Results.Ok(await service.CreateAsync(
                    conn, actor, id, request, form.Files.ToArray(), Ip(context), ct));
            }
            if (!context.Request.HasJsonContentType())
                throw new ApiException(415, 41501, "留言仅支持 JSON 或 multipart/form-data");
            var json = await context.Request.ReadFromJsonAsync<MessageCreateRequest>(cancellationToken: ct)
                ?? throw ApiException.BadRequest("请求格式不正确");
            return Results.Ok(await service.CreateAsync(conn, actor, id, json, Ip(context), ct));
        })
        .WithMetadata(new RequestSizeLimitAttribute(MessageService.MultipartRequestLimitBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MessageService.MultipartRequestLimitBytes,
            ValueLengthLimit = 32 * 1024,
            MultipartHeadersLengthLimit = 32 * 1024,
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
        api.MapGet("/messages/{messageId:long}/images/{imageId:long}", async (
            HttpContext context,
            ulong messageId,
            ulong imageId,
            AppDb db,
            MessageService service,
            CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var image = await service.GetImageAsync(
                conn, AccessService.GetCurrent(context), messageId, imageId, ct);
            var stream = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.ContentDisposition =
                    $"inline; filename*=UTF-8''{Uri.EscapeDataString(image.OriginalName)}";
                return Results.File(stream, image.MimeType, enableRangeProcessing: false);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
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
        api.MapGet("/project-owner-options", async (HttpContext context, AppDb db, ProjectService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ProjectOwnerOptionsAsync(conn, AccessService.GetCurrent(context), ct));
        });
        api.MapGet("/project-dictionaries", async (HttpContext context, AppDb db, ProjectDictionaryService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.ListAsync(conn, AccessService.GetCurrent(context), QueryString(context, "type"),
                QueryBool(context, "enabledOnly", false), QueryNullableUlong(context, "parentId"), ct));
        });
        api.MapPost("/project-dictionaries", async (HttpContext context, ProjectDictionaryUpsertRequest request, AppDb db, ProjectDictionaryService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.CreateAsync(conn, AccessService.GetCurrent(context), request, Ip(context), ct));
        });
        api.MapPut("/project-dictionaries/{id:long}", async (HttpContext context, ulong id, ProjectDictionaryUpsertRequest request, AppDb db, ProjectDictionaryService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await service.UpdateAsync(conn, AccessService.GetCurrent(context), id, request, Ip(context), ct));
        });
        api.MapDelete("/project-dictionaries/{id:long}", async (HttpContext context, ulong id, AppDb db, ProjectDictionaryService service, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            await service.DeleteAsync(conn, AccessService.GetCurrent(context), id, Ip(context), ct);
            return Results.NoContent();
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

    internal static bool IsMessageImageUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)
            || request.ContentType is null
            || !request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = request.Path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: 5 }
            && segments[0] == "api"
            && segments[1] == "v1"
            && segments[2] == "projects"
            && long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var projectId)
            && projectId > 0
            && segments[4] == "messages";
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
