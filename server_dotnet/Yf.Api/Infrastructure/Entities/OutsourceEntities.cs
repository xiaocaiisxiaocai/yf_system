using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

/// <summary>
/// Outsourcing-list vendor. Independent of collaboration suppliers and ODM/OEMA companies even when the
/// same company appears there; never deleted, only disabled. See 外协清单契约.
/// </summary>
public sealed class OutsourceVendor
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Remark { get; set; }
    public string Status { get; set; } = null!;
    public int SortNo { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A vendor mail recipient. The address is required; the person's name is optional.</summary>
public sealed class OutsourceVendorContact
{
    public ulong Id { get; set; }
    public ulong VendorId { get; set; }
    public string? Name { get; set; }
    public string Email { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int SortNo { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Outsourcing type dictionary; <see cref="Code"/> is what IPMS reports.</summary>
public sealed class OutsourceType
{
    public ulong Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int SortNo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Which vendors may receive which outsourcing type (attachment 2 of the requirement).</summary>
public sealed class OutsourceTypeVendor
{
    public ulong TypeId { get; set; }
    public ulong VendorId { get; set; }
}

/// <summary>Internal accounts copied on every vendor mail; senders cannot remove them per mail.</summary>
public sealed class OutsourceDefaultCc
{
    public ulong UserId { get; set; }
    public int SortNo { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OutsourceVendorConfig : IEntityTypeConfiguration<OutsourceVendor>
{
    public void Configure(EntityTypeBuilder<OutsourceVendor> b)
    {
        b.ToTable("outsource_vendors");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(255);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_outsource_vendors_name");
        b.HasIndex(x => new { x.Status, x.SortNo, x.Id }).HasDatabaseName("idx_outsource_vendors_status_sort");
    }
}

public sealed class OutsourceVendorContactConfig : IEntityTypeConfiguration<OutsourceVendorContact>
{
    public void Configure(EntityTypeBuilder<OutsourceVendorContact> b)
    {
        b.ToTable("outsource_vendor_contacts");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(32);
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => new { x.VendorId, x.Email }).IsUnique().HasDatabaseName("uk_outsource_vendor_contacts_vendor_email");
        b.HasIndex(x => new { x.VendorId, x.Status, x.SortNo, x.Id }).HasDatabaseName("idx_outsource_vendor_contacts_vendor_status");
        b.HasOne<OutsourceVendor>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_outsource_vendor_contacts_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceTypeConfig : IEntityTypeConfiguration<OutsourceType>
{
    public void Configure(EntityTypeBuilder<OutsourceType> b)
    {
        b.ToTable("outsource_types");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("uk_outsource_types_code");
    }
}

public sealed class OutsourceTypeVendorConfig : IEntityTypeConfiguration<OutsourceTypeVendor>
{
    public void Configure(EntityTypeBuilder<OutsourceTypeVendor> b)
    {
        b.ToTable("outsource_type_vendors");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.TypeId, x.VendorId });
        b.Property(x => x.TypeId).HasColumnName("type_id");
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.HasIndex(x => x.VendorId).HasDatabaseName("idx_outsource_type_vendors_vendor");
        b.HasOne<OutsourceType>().WithMany().HasForeignKey(x => x.TypeId)
            .HasConstraintName("fk_outsource_type_vendors_type").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OutsourceVendor>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_outsource_type_vendors_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceDefaultCcConfig : IEntityTypeConfiguration<OutsourceDefaultCc>
{
    public void Configure(EntityTypeBuilder<OutsourceDefaultCc> b)
    {
        b.ToTable("outsource_default_cc");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        // Deleting an internal account drops it from the default copy list.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_outsource_default_cc_user").OnDelete(DeleteBehavior.Cascade);
    }
}
