using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

public static class FilesModule
{
    public static IServiceCollection AddFilesModule(this IServiceCollection services)
    {
        services.AddSingleton<BatchDownloadLimiter>();
        services.AddSingleton<MediaGrantService>();
        services.AddSingleton<DownloadGrantService>();
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
            await service.ListAsync(context, projectId, ct));

        api.MapPost("/uploads/init", async (
            HttpContext context, InitUploadRequest request, UploadService service, CancellationToken ct) =>
            await service.InitAsync(context, request, ct));
        api.MapGet("/uploads/{sessionId}", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
            await service.GetAsync(context, sessionId, ct));
        api.MapPut("/uploads/{sessionId}/chunks/{index:int}", async (
            HttpContext context, string sessionId, int index, UploadService service, CancellationToken ct) =>
        {
            await service.PutChunkAsync(context, sessionId, index, context.Request.Body, ct);
            return EmptyResponse.Instance;
        }).DisableAntiforgery();
        api.MapPost("/uploads/{sessionId}/md5", async (
            HttpContext context, string sessionId, SubmitUploadMd5Request request,
            UploadService service, CancellationToken ct) =>
        {
            await service.SubmitMd5Async(context, sessionId, request, ct);
            return EmptyResponse.Instance;
        });
        api.MapPost("/uploads/{sessionId}/merge", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
            await service.MergeAsync(context, sessionId, ct));
        api.MapDelete("/uploads/{sessionId}", async (
            HttpContext context, string sessionId, UploadService service, CancellationToken ct) =>
        {
            await service.AbortAsync(context, sessionId, ct);
            return EmptyResponse.Instance;
        });

        api.MapGet("/files/{id:long}/download", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamAsync(context, id, inline: false, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        api.MapPost("/files/{id:long}/download-grant", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.CreateDownloadGrantAsync(context, id, ct));
        api.MapGet("/files/{id:long}/native-download/{handle}", async (
            HttpContext context, ulong id, string handle, FileService service, CancellationToken ct) =>
            await service.StreamNativeAsync(context, id, handle, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        api.MapGet("/files/{id:long}/content", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamAsync(context, id, inline: true, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        api.MapPost("/files/{id:long}/media-session", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.CreateMediaSessionAsync(context, id, ct));
        api.MapGet("/files/{id:long}/media", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
            await service.StreamMediaAsync(context, id, ct))
            .Produces(StatusCodes.Status200OK, contentType: "video/mp4");
        api.MapDelete("/files/{id:long}", async (
            HttpContext context, ulong id, FileService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(context, id, ct);
            return EmptyResponse.Instance;
        });
        api.MapPost("/files/batch-download", async (
            HttpContext context, BatchDownloadRequest request, FileService service, CancellationToken ct) =>
            await service.BatchDownloadAsync(context, request, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");
        api.MapPost("/files/batch-download-grant", async (
            HttpContext context, BatchDownloadRequest request, FileService service, CancellationToken ct) =>
            await service.CreateBatchDownloadGrantAsync(context, request, ct));
        api.MapGet("/files/batch-download/{handle}", async (
            HttpContext context, string handle, FileService service, CancellationToken ct) =>
            await service.BatchDownloadNativeAsync(context, handle, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");

        return endpoints;
    }
}

public sealed record InitUploadRequest(
    ulong ProjectId,
    string FileName,
    ulong FileSize,
    long FileLastModified,
    string FileFingerprint);

public sealed record SubmitUploadMd5Request(string FileMd5);

public sealed record BatchDownloadRequest(IReadOnlyList<ulong> Ids);
public sealed record DownloadGrantResponse(string Url, int ExpiresInSeconds);

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
    public long FileLastModified { get; set; }
    public string FileFingerprint { get; set; } = "";
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
