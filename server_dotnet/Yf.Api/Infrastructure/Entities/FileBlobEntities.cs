using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public sealed class FileBlob
{
    public ulong Id { get; set; }
    public string Sha256 { get; set; } = null!;
    public ulong SizeBytes { get; set; }
    public string StoragePath { get; set; } = null!;
    public string State { get; set; } = FileBlobStates.Ready;
    public DateTime? GarbageCollectionStartedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class FileBlobStates
{
    public const string Ready = "READY";
    public const string GarbageCollectionPending = "GC_PENDING";
}

public sealed class FileBlobConfig : IEntityTypeConfiguration<FileBlob>
{
    public void Configure(EntityTypeBuilder<FileBlob> b)
    {
        b.ToTable("file_blobs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sha256).HasColumnName("sha256").HasColumnType("char(64)")
            .UseCollation("ascii_bin").IsRequired();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(512)
            .UseCollation("ascii_bin").IsRequired();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired()
            .HasDefaultValue(FileBlobStates.Ready);
        b.Property(x => x.GarbageCollectionStartedAt).HasColumnName("gc_started_at").HasColumnType("datetime(6)");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.HasIndex(x => x.Sha256).IsUnique().HasDatabaseName("uk_file_blobs_sha256");
        b.HasIndex(x => x.StoragePath).IsUnique().HasDatabaseName("uk_file_blobs_storage_path");
        b.HasIndex(x => new { x.State, x.Id }).HasDatabaseName("idx_file_blobs_state");
    }
}
