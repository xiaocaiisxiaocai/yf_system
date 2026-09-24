using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yf.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Yf.Api.Infrastructure.Entities;

// Named FileRecord, not File, to avoid colliding with System.IO.File.
public sealed class FileRecord
{
    public ulong Id { get; set; }
    public ulong? BlobId { get; set; }
    public ulong ProjectId { get; set; }
    public ulong UploaderId { get; set; }
    public string Direction { get; set; } = null!;
    public string OriginalName { get; set; } = null!;
    public string StoredName { get; set; } = null!;
    public string Ext { get; set; } = null!;
    public ulong SizeBytes { get; set; }
    public string? MimeType { get; set; }
    public string? Sha256 { get; set; }
    public string StoragePath { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UploadSession
{
    public string Id { get; set; } = null!;
    public ulong ProjectId { get; set; }
    public ulong UploaderId { get; set; }
    public string FileName { get; set; } = null!;
    public ulong FileSize { get; set; }
    public long FileLastModified { get; set; }
    public string FileFingerprint { get; set; } = null!;
    public string? FileMd5 { get; set; }
    public uint ChunkSize { get; set; }
    public uint TotalChunks { get; set; }
    public string TempDir { get; set; } = null!;
    public string Status { get; set; } = null!;
    public ulong? ResultFileId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class FileCopyRef
{
    public ulong Id { get; set; }
    public ulong CopyId { get; set; }
    public ulong SourceFileId { get; set; }
    public ulong TargetFileId { get; set; }
    public string SourceFileName { get; set; } = null!;
    public string TargetFileName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public sealed class FileRecordConfig : IEntityTypeConfiguration<FileRecord>
{
    public void Configure(EntityTypeBuilder<FileRecord> b)
    {
        b.ToTable("files");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.BlobId).HasColumnName("blob_id");
        b.HasIndex(x => x.BlobId).HasDatabaseName("idx_files_blob");
        b.HasOne<FileBlob>().WithMany().HasForeignKey(x => x.BlobId)
            .HasConstraintName("fk_files_blob").OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.UploaderId).HasColumnName("uploader_id");
        b.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(8).IsRequired();
        b.Property(x => x.OriginalName).HasColumnName("original_name").HasMaxLength(255).IsRequired();
        b.Property(x => x.StoredName).HasColumnName("stored_name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Ext).HasColumnName("ext").HasMaxLength(16).IsRequired();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(128);
        b.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        b.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(512).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(FileStatuses.Available);
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("datetime(3)");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.StoredName).IsUnique().HasDatabaseName("stored_name");
        b.HasIndex(x => x.UploaderId).HasDatabaseName("fk_files_uploader");
        // Serves the file list: WHERE project_id=? AND status=? ORDER BY id DESC; also backs fk_files_project.
        b.HasIndex(x => new { x.ProjectId, x.Status, x.Id }).HasDatabaseName("idx_files_project_status");
        b.HasIndex(x => new { x.Status, x.DeletedAt }).HasDatabaseName("idx_files_status_deleted_at");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_files_project").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderId).HasConstraintName("fk_files_uploader").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UploadSessionConfig : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> b)
    {
        b.ToTable("upload_sessions");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.UploaderId).HasColumnName("uploader_id");
        b.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
        b.Property(x => x.FileSize).HasColumnName("file_size");
        b.Property(x => x.FileLastModified).HasColumnName("file_last_modified");
        b.Property(x => x.FileFingerprint).HasColumnName("file_fingerprint").HasMaxLength(64).IsRequired();
        b.Property(x => x.FileMd5).HasColumnName("file_md5").HasMaxLength(32);
        b.Property(x => x.ChunkSize).HasColumnName("chunk_size");
        b.Property(x => x.TotalChunks).HasColumnName("total_chunks");
        b.Property(x => x.TempDir).HasColumnName("temp_dir").HasMaxLength(512).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("UPLOADING");
        b.Property(x => x.ResultFileId).HasColumnName("result_file_id");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("datetime");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        var updatedAt = b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime")
            .HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        // Upload initialization uses one database timestamp for created/expires/updated.
        // Keep the database's ON UPDATE behavior while still sending that explicit value on insert.
        updatedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Save);
        b.HasIndex(x => x.ProjectId).HasDatabaseName("fk_us_project");
        b.HasIndex(x => x.UploaderId).HasDatabaseName("idx_us_uploader");
        b.HasIndex(x => new { x.ProjectId, x.UploaderId, x.FileFingerprint, x.Status, x.ExpiresAt })
            .HasDatabaseName("idx_us_resume");
        b.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("idx_us_status_exp");
        b.HasIndex(x => x.ResultFileId).HasDatabaseName("idx_upload_result_file");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_us_project").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderId).HasConstraintName("fk_us_uploader").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FileCopyRefConfig : IEntityTypeConfiguration<FileCopyRef>
{
    public void Configure(EntityTypeBuilder<FileCopyRef> b)
    {
        b.ToTable("file_copy_refs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.CopyId).HasColumnName("copy_id");
        b.Property(x => x.SourceFileId).HasColumnName("source_file_id");
        b.Property(x => x.TargetFileId).HasColumnName("target_file_id");
        b.Property(x => x.SourceFileName).HasColumnName("source_file_name").HasMaxLength(255).IsRequired();
        b.Property(x => x.TargetFileName).HasColumnName("target_file_name").HasMaxLength(255).IsRequired();
        // No column default: the app always supplies an exact timestamp.
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.HasIndex(x => x.TargetFileId).IsUnique().HasDatabaseName("uk_file_copy_refs_target");
        b.HasIndex(x => new { x.CopyId, x.Id }).HasDatabaseName("idx_file_copy_refs_copy");
        b.HasIndex(x => x.SourceFileId).HasDatabaseName("idx_file_copy_refs_source");
        b.HasOne<ProjectCopy>().WithMany().HasForeignKey(x => x.CopyId).HasConstraintName("fk_file_copy_refs_copy").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<FileRecord>().WithMany().HasForeignKey(x => x.SourceFileId).HasConstraintName("fk_file_copy_refs_source").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FileRecord>().WithMany().HasForeignKey(x => x.TargetFileId).HasConstraintName("fk_file_copy_refs_target").OnDelete(DeleteBehavior.Restrict);
    }
}
