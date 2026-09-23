using System.Linq.Expressions;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Files;

internal static class FileRowMapping
{
    internal static readonly Expression<Func<FileRecord, FileRow>> Projection = file => new FileRow
    {
        Id = file.Id, ProjectId = file.ProjectId, UploaderId = file.UploaderId, Direction = file.Direction,
        OriginalName = file.OriginalName, StoredName = file.StoredName, Ext = file.Ext, SizeBytes = file.SizeBytes,
        MimeType = file.MimeType, Sha256 = file.Sha256, StoragePath = file.StoragePath, Status = file.Status,
        DeletedAt = file.DeletedAt, CreatedAt = file.CreatedAt,
    };

    private static readonly Func<FileRecord, FileRow> MapRecord = Projection.Compile();
    internal static FileRow Map(FileRecord file) => MapRecord(file);
}
