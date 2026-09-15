using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

public static class FilesModule
{
    public static IServiceCollection AddFilesModule(this IServiceCollection services)
    {
        services.AddSingleton<BatchDownloadLimiter>();
        services.AddSingleton<MediaGrantService>();
        services.AddScoped<FileService>();
        services.AddScoped<UploadService>();
        services.AddHostedService<FilesMaintenanceService>();
        return services;
    }

    public static IEndpointRouteBuilder MapFilesModule(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/projects/{projectId:long}/files", async (
            HttpContext context, ulong projectId, FileService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(context, projectId, ct)));

        api.MapPost("/uploads/init", async (
            HttpContext context, InitUploadRequest request, UploadService service, CancellationToken ct) =>
            Results.Ok(await service.InitAsync(context, request, ct)));
        api.MapGet("/uploads/{sessionId}", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(context, sessionId, ct)));
        api.MapPut("/uploads/{sessionId}/chunks/{index:int}", async (
            HttpContext context, string sessionId, int index, UploadService service, CancellationToken ct) =>
        {
            await service.PutChunkAsync(context, sessionId, index, context.Request.Body, ct);
            return Results.Ok(new { });
        }).DisableAntiforgery();
        api.MapPost("/uploads/{sessionId}/merge", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
            Results.Ok(await service.MergeAsync(context, sessionId, ct)));
        api.MapDelete("/uploads/{sessionId}", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
        {
            await service.AbortAsync(context, sessionId, ct);
            return Results.Ok(new { });
        });

        api.MapGet("/files/{id:long}/download", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamAsync(context, id, inline: false, ct));
        api.MapGet("/files/{id:long}/content", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamAsync(context, id, inline: true, ct));
        api.MapPost("/files/{id:long}/media-session", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            Results.Ok(await service.CreateMediaSessionAsync(context, id, ct)));
        api.MapGet("/files/{id:long}/media", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamMediaAsync(context, id, ct));
        api.MapDelete("/files/{id:long}", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(context, id, ct);
            return Results.Ok(new { });
        });
        api.MapPost("/files/batch-download", async (
            HttpContext context, BatchDownloadRequest request, FileService service, CancellationToken ct) =>
            await service.BatchDownloadAsync(context, request, ct));

        return endpoints;
    }
}

public sealed record InitUploadRequest(
    ulong ProjectId,
    string FileName,
    ulong FileSize,
    string? FileMd5);

public sealed record BatchDownloadRequest(IReadOnlyList<ulong> Ids);

internal sealed class FileRow
{
    public ulong Id { get; set; }
    public ulong ProjectId { get; set; }
    public ulong UploaderId { get; set; }
    public string Direction { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string Ext { get; set; } = "";
    public ulong SizeBytes { get; set; }
    public string? MimeType { get; set; }
    public string? Sha256 { get; set; }
    public string StoragePath { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class UploadSessionRow
{
    public string Id { get; set; } = "";
    public ulong ProjectId { get; set; }
    public ulong UploaderId { get; set; }
    public string FileName { get; set; } = "";
    public ulong FileSize { get; set; }
    public string? FileMd5 { get; set; }
    public uint ChunkSize { get; set; }
    public uint TotalChunks { get; set; }
    public string TempDir { get; set; } = "";
    public string Status { get; set; } = "";
    public ulong? ResultFileId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsExpired { get; set; }
}

internal readonly record struct SessionCommitRecoveryDecision(bool Acknowledge, bool DeleteDirectory);
