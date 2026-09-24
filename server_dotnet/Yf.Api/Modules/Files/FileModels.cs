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
