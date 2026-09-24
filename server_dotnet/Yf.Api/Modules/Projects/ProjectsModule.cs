using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
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
        services.AddSingleton<ProjectCopyWakeSignal>();
        services.AddSingleton<ProjectCopyWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ProjectCopyWorker>());
        services.AddScoped<ProjectDictionaryService>();
        services.AddScoped<RobotPartService>();
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

        api.MapGet("/project-groups", (HttpContext context, ProjectGroupService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListAsync(
                conn,
                actor,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryString(context, "keyword"),
                QueryString(context, "status"),
                QueryNullableUlong(context, "supplierId"),
                ct)));
        api.MapPost("/project-groups", (HttpContext context, ProjectUpsertRequest request, ProjectGroupService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.CreateAsync(conn, actor, request, ip, ct)));
        api.MapGet("/project-groups/{id:long}", (HttpContext context, ulong id, ProjectGroupService service) =>
            WithDb(context, (conn, actor, _, ct) => service.DetailAsync(conn, actor, id, ct)));
        api.MapPut("/project-groups/{id:long}", (HttpContext context, ulong id, ProjectUpsertRequest request, ProjectGroupService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.UpdateAsync(conn, actor, id, request, ip, ct)));
        api.MapPut("/project-groups/{id:long}/responsible", (HttpContext context, ulong id, ProjectGroupTransferRequest request, ProjectGroupService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.TransferAsync(conn, actor, id, request, ip, ct)));
        api.MapDelete("/project-groups/{id:long}", (HttpContext context, ulong id, ProjectGroupService service) =>
            WithDb(context, (conn, actor, ip, ct) => Empty(service.DeleteAsync(conn, actor, id, ip, ct))));
        api.MapPost("/project-groups/{id:long}/projects", (HttpContext context, ulong id, SubprojectUpsertRequest request, ProjectGroupService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.CreateSubprojectAsync(conn, actor, id, request, ip, ct)));

        api.MapPost("/projects/{id:long}/copy", async (HttpContext context, ulong id, ProjectCopyRequest request,
            ProjectCopyService service) =>
        {
            var job = await WithDb(context,
                (conn, actor, ip, ct) => service.EnqueueAsync(conn, actor, id, request, ip, ct));
            return TypedResults.Accepted($"/api/v1/project-copy-jobs/{job.JobId}", job);
        });
        api.MapGet("/project-copy-jobs/{jobId:long}", (HttpContext context, ulong jobId, ProjectCopyService service) =>
            WithDb(context, (conn, actor, _, ct) => service.GetJobAsync(conn, actor, jobId, ct)));
        api.MapGet("/project-groups/{groupId:long}/copy-jobs", (HttpContext context, ulong groupId, ProjectCopyService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListJobsAsync(conn, actor, groupId, ct)));
        api.MapGet("/projects/{id:long}/copy-history", (HttpContext context, ulong id, ProjectCopyService service) =>
            WithDb(context, (conn, actor, _, ct) => service.HistoryAsync(conn, actor, id, ct)));
        api.MapGet("/project-copies/{copyId:long}/files", (HttpContext context, ulong copyId, ProjectCopyService service) =>
            WithDb(context, (conn, actor, _, ct) => service.FileHistoryAsync(conn, actor, copyId,
                QueryUlong(context, "page", 1), QueryUlong(context, "pageSize", 20), ct)));
        api.MapGet("/projects/{id:long}", (HttpContext context, ulong id, ProjectService service) =>
            WithDb(context, (conn, actor, _, ct) => service.DetailAsync(conn, actor, id, ct)));
        api.MapPut("/projects/{id:long}", (HttpContext context, ulong id, SubprojectUpsertRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.UpdateSubprojectAsync(conn, actor, id, request, ip, ct)));
        api.MapDelete("/projects/{id:long}", (HttpContext context, ulong id, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => Empty(service.DeleteAsync(conn, actor, id, ip, ct))));
        api.MapPut("/projects/{id:long}/status", (HttpContext context, ulong id, ProjectStatusRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.SetStatusAsync(conn, actor, id, request, ip, ct)));
        api.MapPost("/projects/{id:long}/submit", (HttpContext context, ulong id, ProjectSubmitRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.SubmitAsync(conn, actor, id, request, ip, ct)));
        api.MapPost("/projects/{id:long}/confirm", (HttpContext context, ulong id, ProjectDecisionRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.ConfirmAsync(conn, actor, id, request, ip, ct)));
        api.MapPost("/projects/{id:long}/reject", (HttpContext context, ulong id, ProjectRejectRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.RejectAsync(conn, actor, id, request, ip, ct)));
        api.MapPost("/projects/{id:long}/withdraw", (HttpContext context, ulong id, ProjectDecisionRequest request, ProjectService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.WithdrawAsync(conn, actor, id, request, ip, ct)));
        api.MapGet("/projects/{id:long}/summary", (HttpContext context, ulong id, ProjectService service) =>
            WithDb(context, (conn, actor, _, ct) => service.SummaryAsync(conn, actor, id, ct)));
        api.MapGet("/projects/{id:long}/activities", (HttpContext context, ulong id, ProjectActivityService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListAsync(
                conn,
                actor,
                id,
                QueryString(context, "type"),
                QueryString(context, "cursor"),
                QueryUlong(context, "pageSize", 20),
                ct)));

        api.MapGet("/projects/{id:long}/messages", (HttpContext context, ulong id, MessageService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListAsync(
                conn,
                actor,
                id,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryNullableUlong(context, "beforeId"),
                QueryNullableUlong(context, "targetId"),
                ct)));
        api.MapGet("/projects/{id:long}/message-receipts", (HttpContext context, ulong id, MessageService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ReceiptsAsync(
                conn,
                actor,
                id,
                ParseMessageIds(QueryString(context, "ids")),
                ct)));
        api.MapPost("/projects/{id:long}/messages", async (HttpContext context, ulong id, MessageService service) =>
        {
            if (!context.Request.HasFormContentType && !context.Request.HasJsonContentType())
                throw new ApiException(415, 41501, "留言仅支持 JSON 或 multipart/form-data");
            // This short preflight releases its pooled connection before waiting for the request body.
            // CreateAsync rechecks authorization and project state in its own write transaction.
            await WithDb(context, (conn, actor, _, ct) =>
                Empty(service.EnsureCreateAllowedAsync(conn, actor, id, ct)));
            MessageCreateRequest request;
            IFormFile[] images = [];
            if (context.Request.HasFormContentType)
            {
                IFormCollection form;
                try { form = await context.Request.ReadFormAsync(context.RequestAborted); }
                catch (InvalidDataException)
                {
                    throw ApiException.BadRequest("留言图片表单格式不正确或超过大小上限");
                }
                if (form.Files.Any(file => file.Name != "images"))
                    throw ApiException.BadRequest("图片表单字段必须命名为 images");
                if (form.TryGetValue("content", out var contents) && contents.Count > 1)
                    throw ApiException.BadRequest("content 表单字段只能出现一次");
                request = new MessageCreateRequest { Content = contents.Count == 0 ? null : contents[0] };
                images = form.Files.ToArray();
            }
            else
            {
                request = await context.Request.ReadFromJsonAsync<MessageCreateRequest>(cancellationToken: context.RequestAborted)
                    ?? throw ApiException.BadRequest("请求格式不正确");
            }
            return await WithDb(context, (conn, actor, ip, ct) =>
                service.CreateAsync(conn, actor, id, request, images, ip, ct));
        })
        .WithMetadata(new RequestSizeLimitAttribute(MessageService.MultipartRequestLimitBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MessageService.MultipartRequestLimitBytes,
            ValueLengthLimit = 32 * 1024,
            MultipartHeadersLengthLimit = 32 * 1024,
        });
        api.MapPost("/messages/read", (HttpContext context, MarkMessagesReadRequest request, MessageService service) =>
            WithDb(context, (conn, actor, _, ct) => Empty(service.MarkReadAsync(conn, actor, request, ct))));
        api.MapGet("/messages/{id:long}/reads", (HttpContext context, ulong id, MessageService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ReadsAsync(conn, actor, id, ct)));
        api.MapGet("/messages/{messageId:long}/images/{imageId:long}", async (
            HttpContext context,
            ulong messageId,
            ulong imageId,
            MessageService service) =>
        {
            var image = await WithDb(context, (conn, actor, _, ct) =>
                service.GetImageAsync(conn, actor, messageId, imageId, ct));
            var stream = OpenMessageImageStream(image.Path);
            try
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.ContentDisposition =
                    $"inline; filename*=UTF-8''{Uri.EscapeDataString(image.OriginalName)}";
                // The FileStreamHttpResult owns and disposes the stream after this point.
                return Results.File(stream, image.MimeType, enableRangeProcessing: false);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }).Produces(StatusCodes.Status200OK, null, "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp");
        api.MapDelete("/messages/{id:long}", (HttpContext context, ulong id, MessageService service) =>
            WithDb(context, (conn, actor, ip, ct) => Empty(service.DeleteAsync(conn, actor, id, ip, ct))));

        api.MapGet("/dashboard/summary", (HttpContext context, DashboardService service) =>
            WithDb(context, (conn, actor, _, ct) => service.SummaryAsync(conn, actor, ct)));
        api.MapGet("/dashboard/pending-projects", (HttpContext context, DashboardService service) =>
            WithDb(context, (conn, actor, _, ct) => service.PendingProjectsAsync(
                conn,
                actor,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                ct)));
        api.MapGet("/dashboard/messages", (HttpContext context, DashboardService service) =>
            WithDb(context, (conn, actor, _, ct) => service.MessagesAsync(
                conn,
                actor,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 10),
                QueryBool(context, "unreadOnly", false),
                ct)));
        api.MapGet("/collaboration/summary", (HttpContext context, CollaborationService service) =>
            WithDb(context, (conn, actor, _, ct) => service.SummaryAsync(conn, actor, ct)));
        api.MapGet("/collaboration/notifications", (HttpContext context, CollaborationService service) =>
            WithDb(context, (conn, actor, _, ct) => service.NotificationsAsync(
                conn,
                actor,
                QueryUlong(context, "page", 1),
                QueryUlong(context, "pageSize", 20),
                QueryBool(context, "unreadOnly", false),
                ct)));
        api.MapPost("/collaboration/reads", (HttpContext context, MarkCollaborationReadRequest request, CollaborationService service) =>
            WithDb(context, (conn, actor, _, ct) => Empty(service.MarkReadAsync(conn, actor, request, ct))));
        api.MapGet("/supplier-options", (HttpContext context, ProjectService service) =>
            WithDb(context, (conn, actor, _, ct) => service.SupplierOptionsAsync(conn, actor, ct)));
        api.MapGet("/project-owner-options", (HttpContext context, ProjectService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ProjectOwnerOptionsAsync(conn, actor, ct)));
        api.MapGet("/project-dictionaries", (HttpContext context, ProjectDictionaryService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListAsync(conn, actor, QueryString(context, "type"),
                QueryBool(context, "enabledOnly", false), QueryNullableUlong(context, "parentId"), ct)));
        api.MapPost("/project-dictionaries", (HttpContext context, ProjectDictionaryUpsertRequest request, ProjectDictionaryService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.CreateAsync(conn, actor, request, ip, ct)));
        api.MapPut("/project-dictionaries/{id:long}", (HttpContext context, ulong id, ProjectDictionaryUpsertRequest request, ProjectDictionaryService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.UpdateAsync(conn, actor, id, request, ip, ct)));
        api.MapDelete("/project-dictionaries/{id:long}", (HttpContext context, ulong id, ProjectDictionaryService service) =>
            WithDb(context, (conn, actor, ip, ct) => Empty(service.DeleteAsync(conn, actor, id, ip, ct))));
        api.MapGet("/robot-parts", (HttpContext context, RobotPartService service) =>
            WithDb(context, (conn, actor, _, ct) => service.ListAsync(conn, actor,
                QueryNullableUlong(context, "supplierId"), QueryBool(context, "enabledOnly", true), ct)));
        api.MapGet("/robot-part-supplier-options", (HttpContext context, RobotPartService service) =>
            WithDb(context, (conn, actor, _, ct) => service.SupplierOptionsAsync(conn, actor, ct)));
        api.MapPost("/robot-parts", (HttpContext context, RobotPartUpsertRequest request, RobotPartService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.CreateAsync(conn, actor, request, ip, ct)));
        api.MapPut("/robot-parts/{id:long}", (HttpContext context, ulong id, RobotPartUpsertRequest request, RobotPartService service) =>
            WithDb(context, (conn, actor, ip, ct) => service.UpdateAsync(conn, actor, id, request, ip, ct)));
        api.MapDelete("/robot-parts/{id:long}", (HttpContext context, ulong id, RobotPartService service) =>
            WithDb(context, (conn, actor, ip, ct) => Empty(service.DeleteAsync(conn, actor, id, ip, ct))));

        return endpoints;
    }

    internal static FileStream OpenMessageImageStream(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw ApiException.NotFound();
        }
    }

    private static async Task<T> WithDb<T>(
        HttpContext context,
        Func<MySqlConnection, CurrentUser, string, CancellationToken, Task<T>> action)
    {
        var ct = context.RequestAborted;
        var db = context.RequestServices.GetRequiredService<AppDb>();
        await using var conn = await db.OpenAsync(ct);
        return await action(conn, AccessService.GetCurrent(context), Ip(context), ct);
    }

    private static async Task<EmptyResponse> Empty(Task operation)
    {
        await operation;
        return EmptyResponse.Instance;
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
