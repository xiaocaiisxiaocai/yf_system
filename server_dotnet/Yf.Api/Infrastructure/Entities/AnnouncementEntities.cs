using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public sealed class Announcement
{
    public ulong Id { get; set; }
    public ulong CreatedBy { get; set; }
    public string? CreateKey { get; set; }
    public ulong? PublisherId { get; set; }
    public string? PublisherName { get; set; }
    public ulong? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? DocumentJson { get; set; }
    public string InternalScope { get; set; } = "OWN";
    public string? ExternalScope { get; set; }
    public string Status { get; set; } = "DRAFT";
    public ulong Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? PinnedAt { get; set; }
    public int? NormalSortOrder { get; set; }
    public int? PinnedSortOrder { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public ulong? WithdrawnBy { get; set; }
    public string? WithdrawnByName { get; set; }
}

// Directory IDs decide live visibility; snapshot names support archive inspection after a rename or deletion.
public sealed class AnnouncementDepartment
{
    public ulong AnnouncementId { get; set; }
    public ulong DepartmentId { get; set; }
    public string DepartmentName { get; set; } = "";
}

public sealed class AnnouncementCompany
{
    public ulong AnnouncementId { get; set; }
    public ulong CompanyId { get; set; }
    public string CompanyName { get; set; } = "";
}

public sealed class AnnouncementRead
{
    public ulong AnnouncementId { get; set; }
    public string Realm { get; set; } = null!;
    public ulong AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime FirstReadAt { get; set; }
    public DateTime LastReadAt { get; set; }
}

// Each announcement holds its own link so retiring the source announcement cannot break a copy.
public sealed class AnnouncementAsset
{
    public ulong Id { get; set; }
    public string Kind { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = null!;
    public string StoragePath { get; set; } = null!;
    public string? ThumbnailStoragePath { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AnnouncementAssetLink
{
    public ulong AnnouncementId { get; set; }
    public ulong AssetId { get; set; }
    public AnnouncementAsset Asset { get; set; } = null!;
    // A newly uploaded draft asset is pending until its position is saved with the document.
    public int? SortOrder { get; set; }
    public string? UploadKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AnnouncementConfig : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("announcements");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreateKey).HasColumnName("create_key").HasMaxLength(32).UseCollation("ascii_bin");
        b.HasIndex(x => new { x.CreatedBy, x.CreateKey }).IsUnique().HasDatabaseName("uk_announcements_create");
        b.Property(x => x.PublisherId).HasColumnName("publisher_id");
        b.Property(x => x.PublisherName).HasColumnName("publisher_name").HasMaxLength(64);
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.DepartmentName).HasColumnName("department_name").HasMaxLength(64);
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(200);
        b.Property(x => x.Body).HasColumnName("body").HasColumnType("longtext");
        b.Property(x => x.DocumentJson).HasColumnName("document_json").HasColumnType("longtext");
        b.Property(x => x.InternalScope).HasColumnName("internal_scope").HasMaxLength(16);
        b.Property(x => x.ExternalScope).HasColumnName("external_scope").HasMaxLength(16);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(6)");
        b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("datetime(6)");
        b.Property(x => x.PinnedAt).HasColumnName("pinned_at").HasColumnType("datetime(6)");
        b.Property(x => x.NormalSortOrder).HasColumnName("normal_sort_order");
        b.Property(x => x.PinnedSortOrder).HasColumnName("pinned_sort_order");
        b.Property(x => x.WithdrawnAt).HasColumnName("withdrawn_at").HasColumnType("datetime(6)");
        b.Property(x => x.WithdrawnBy).HasColumnName("withdrawn_by");
        b.Property(x => x.WithdrawnByName).HasColumnName("withdrawn_by_name").HasMaxLength(64);
        b.HasIndex(x => new { x.Status, x.PinnedAt, x.PublishedAt, x.Id }).HasDatabaseName("idx_announcements_reader");
        b.HasIndex(x => new { x.DepartmentId, x.Status }).HasDatabaseName("idx_announcements_department_status");
        b.HasIndex(x => new { x.CreatedBy, x.Status }).HasDatabaseName("idx_announcements_creator_status");
    }
}

public sealed class AnnouncementAssetConfig : IEntityTypeConfiguration<AnnouncementAsset>
{
    public void Configure(EntityTypeBuilder<AnnouncementAsset> b)
    {
        b.ToTable("announcement_assets");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16);
        b.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
        b.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(128);
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.Sha256).HasColumnName("sha256").HasColumnType("char(64)").UseCollation("ascii_bin");
        b.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(512).UseCollation("ascii_bin");
        b.Property(x => x.ThumbnailStoragePath).HasColumnName("thumbnail_storage_path").HasMaxLength(512).UseCollation("ascii_bin");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.HasIndex(x => x.StoragePath).IsUnique().HasDatabaseName("uk_announcement_assets_path");
    }
}

public sealed class AnnouncementAssetLinkConfig : IEntityTypeConfiguration<AnnouncementAssetLink>
{
    public void Configure(EntityTypeBuilder<AnnouncementAssetLink> b)
    {
        b.ToTable("announcement_asset_links");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.AnnouncementId, x.AssetId });
        b.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.UploadKey).HasColumnName("upload_key").HasMaxLength(36).UseCollation("ascii_bin");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.HasOne<Announcement>().WithMany().HasForeignKey(x => x.AnnouncementId)
            .HasConstraintName("fk_announcement_asset_links_announcement").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId)
            .HasConstraintName("fk_announcement_asset_links_asset").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AnnouncementId, x.UploadKey }).IsUnique().HasDatabaseName("uk_announcement_asset_links_upload");
        b.HasIndex(x => new { x.SortOrder, x.CreatedAt }).HasDatabaseName("idx_announcement_asset_links_pending");
    }
}

public sealed class AnnouncementDepartmentConfig : IEntityTypeConfiguration<AnnouncementDepartment>
{
    public void Configure(EntityTypeBuilder<AnnouncementDepartment> b)
    {
        b.ToTable("announcement_departments");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.AnnouncementId, x.DepartmentId });
        b.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.DepartmentName).HasColumnName("department_name").HasMaxLength(64);
        b.HasOne<Announcement>().WithMany().HasForeignKey(x => x.AnnouncementId)
            .HasConstraintName("fk_announcement_departments_announcement").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.DepartmentId).HasDatabaseName("idx_announcement_departments_department");
    }
}

public sealed class AnnouncementCompanyConfig : IEntityTypeConfiguration<AnnouncementCompany>
{
    public void Configure(EntityTypeBuilder<AnnouncementCompany> b)
    {
        b.ToTable("announcement_companies");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.AnnouncementId, x.CompanyId });
        b.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
        b.Property(x => x.CompanyId).HasColumnName("company_id");
        b.Property(x => x.CompanyName).HasColumnName("company_name").HasMaxLength(128);
        b.HasOne<Announcement>().WithMany().HasForeignKey(x => x.AnnouncementId)
            .HasConstraintName("fk_announcement_companies_announcement").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.CompanyId).HasDatabaseName("idx_announcement_companies_company");
    }
}

public sealed class AnnouncementReadConfig : IEntityTypeConfiguration<AnnouncementRead>
{
    public void Configure(EntityTypeBuilder<AnnouncementRead> b)
    {
        b.ToTable("announcement_reads");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.AnnouncementId, x.Realm, x.AccountId });
        b.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
        b.Property(x => x.Realm).HasColumnName("realm").HasMaxLength(16);
        b.Property(x => x.AccountId).HasColumnName("account_id");
        b.Property(x => x.AccountName).HasColumnName("account_name").HasMaxLength(64);
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(64);
        b.Property(x => x.FirstReadAt).HasColumnName("first_read_at").HasColumnType("datetime(6)");
        b.Property(x => x.LastReadAt).HasColumnName("last_read_at").HasColumnType("datetime(6)");
        b.HasOne<Announcement>().WithMany().HasForeignKey(x => x.AnnouncementId)
            .HasConstraintName("fk_announcement_reads_announcement").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.AnnouncementId, x.LastReadAt }).HasDatabaseName("idx_announcement_reads_last");
    }
}
