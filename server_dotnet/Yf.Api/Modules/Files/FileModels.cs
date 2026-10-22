using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Files;

public sealed record FileResponse(
    ulong Id,
    ulong ProjectId,
    ulong UploaderId,
    string Direction,
    string OriginalName,
    string Ext,
    ulong SizeBytes,
    string? MimeType,
    string? Sha256,
    DateTime CreatedAt);

public sealed record FileListItem(
    ulong Id,
    ulong ProjectId,
    ulong UploaderId,
    string Direction,
    string OriginalName,
    string Ext,
    ulong SizeBytes,
    string? MimeType,
    string? Sha256,
    DateTime CreatedAt,
    string? UploaderName,
    bool IsCopiedReference,
    bool CanDelete);

public sealed record UploadInitResponse(
    string SessionId,
    uint ChunkSize,
    uint TotalChunks,
    IReadOnlyList<UploadedChunkDigestResponse> UploadedChunks,
    // Present (true) only when an existing session is resumed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Resumed = null);

public sealed record UploadSessionResponse(
    string SessionId,
    string Status,
    uint ChunkSize,
    uint TotalChunks,
    IReadOnlyList<UploadedChunkDigestResponse> UploadedChunks,
    string FileName,
    ulong FileSize,
    ulong? ResultFileId);

public sealed record UploadedChunkDigestResponse(uint Index, string Sha256);

public sealed record MediaSessionResponse(string Url, int ExpiresInSeconds);

public sealed record ProjectUploadSessionListResponse(
    int Total,
    IReadOnlyList<ProjectUploadSessionItemResponse> Items);

public sealed record ProjectUploadSessionItemResponse(
    string SessionId,
    string FileName,
    ulong FileSize,
    int ReceivedChunks,
    int TotalChunks,
    string Status,
    ulong UploaderId,
    string UploaderName,
    string UploaderEmployeeNo,
    string UploaderUserType,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool CanResume,
    bool CanAbort);

/// <summary>
/// Signals a collaboration change after an upload write commits.
/// The realtime publisher itself stays internal to the projects module.
/// </summary>
public interface IStoredUploadNotifier
{
    Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default);
}
