using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Infrastructure.Entities;

public sealed class OutsourceContactPerson
{
    public ulong Id { get; set; }
    public string ScopeType { get; set; } = null!;
    public ulong? VendorId { get; set; }
    public ulong? UserId { get; set; }
    public string Name { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string? Email { get; set; }
    public string? TitleHint { get; set; }
    public string Status { get; set; } = AccountStatuses.Active;
    public ulong? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OutsourceContactPersonConfig : IEntityTypeConfiguration<OutsourceContactPerson>
{
    public void Configure(EntityTypeBuilder<OutsourceContactPerson> b)
    {
        b.ToTable("outsource_contact_people");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ScopeType).HasColumnName("scope_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(32).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(128);
        b.Property(x => x.TitleHint).HasColumnName("title_hint").HasMaxLength(128);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("uk_ocp_user_id");
        b.HasIndex(x => new { x.ScopeType, x.VendorId, x.Status }).HasDatabaseName("idx_ocp_scope_vendor_status");
        b.HasIndex(x => x.Phone).HasDatabaseName("idx_ocp_phone");
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_ocp_vendor").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_ocp_user").OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class OutsourceContactColumn
{
    public ulong Id { get; set; }
    public string ColKey { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string DataType { get; set; } = null!;
    public string PersonDisplay { get; set; } = null!;
    public string? PhoneTitle { get; set; }
    public bool AutoMerge { get; set; }
    public bool VendorVisible { get; set; } = true;
    public bool IsGroupColumn { get; set; }
    public int SortNo { get; set; }
    public double WidthHint { get; set; } = 18;
    public string Status { get; set; } = AccountStatuses.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OutsourceContactColumnConfig : IEntityTypeConfiguration<OutsourceContactColumn>
{
    public void Configure(EntityTypeBuilder<OutsourceContactColumn> b)
    {
        b.ToTable("outsource_contact_columns");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ColKey).HasColumnName("col_key").HasMaxLength(64).IsRequired();
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(64).IsRequired();
        b.Property(x => x.DataType).HasColumnName("data_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.PersonDisplay).HasColumnName("person_display").HasMaxLength(16).IsRequired().HasDefaultValue("COMBINED");
        b.Property(x => x.PhoneTitle).HasColumnName("phone_title").HasMaxLength(64);
        b.Property(x => x.AutoMerge).HasColumnName("auto_merge").HasDefaultValue(false);
        b.Property(x => x.VendorVisible).HasColumnName("vendor_visible").HasDefaultValue(true);
        b.Property(x => x.IsGroupColumn).HasColumnName("is_group_column").HasDefaultValue(false);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.WidthHint).HasColumnName("width_hint").HasDefaultValue(18d);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.ColKey).IsUnique().HasDatabaseName("uk_occ_col_key");
        b.HasIndex(x => new { x.SortNo, x.Id }).HasDatabaseName("idx_occ_sort");
    }
}

public sealed class OutsourceContactRow
{
    public ulong Id { get; set; }
    public int SortNo { get; set; }
    public string Visibility { get; set; } = null!;
    public uint Version { get; set; } = 1;
    public ulong? CreatedBy { get; set; }
    public ulong? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OutsourceContactRowConfig : IEntityTypeConfiguration<OutsourceContactRow>
{
    public void Configure(EntityTypeBuilder<OutsourceContactRow> b)
    {
        b.ToTable("outsource_contact_rows");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(24).IsRequired().HasDefaultValue("INTERNAL_ONLY");
        b.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1u);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => new { x.SortNo, x.Id }).HasDatabaseName("idx_ocr_sort");
        b.HasIndex(x => x.Visibility).HasDatabaseName("idx_ocr_visibility");
    }
}

public sealed class OutsourceContactRowVendor
{
    public ulong RowId { get; set; }
    public ulong VendorId { get; set; }
}

public sealed class OutsourceContactRowVendorConfig : IEntityTypeConfiguration<OutsourceContactRowVendor>
{
    public void Configure(EntityTypeBuilder<OutsourceContactRowVendor> b)
    {
        b.ToTable("outsource_contact_row_vendors");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.RowId, x.VendorId });
        b.Property(x => x.RowId).HasColumnName("row_id");
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.HasIndex(x => x.VendorId).HasDatabaseName("idx_ocrv_vendor");
        b.HasOne<OutsourceContactRow>().WithMany().HasForeignKey(x => x.RowId)
            .HasConstraintName("fk_ocrv_row").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_ocrv_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceContactRowValue
{
    public ulong RowId { get; set; }
    public ulong ColumnId { get; set; }
    public string TextValue { get; set; } = "";
}

public sealed class OutsourceContactRowValueConfig : IEntityTypeConfiguration<OutsourceContactRowValue>
{
    public void Configure(EntityTypeBuilder<OutsourceContactRowValue> b)
    {
        b.ToTable("outsource_contact_row_values");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.RowId, x.ColumnId });
        b.Property(x => x.RowId).HasColumnName("row_id");
        b.Property(x => x.ColumnId).HasColumnName("column_id");
        b.Property(x => x.TextValue).HasColumnName("text_value").HasMaxLength(2000).IsRequired();
        b.HasIndex(x => x.ColumnId).HasDatabaseName("idx_ocrval_column");
        b.HasOne<OutsourceContactRow>().WithMany().HasForeignKey(x => x.RowId)
            .HasConstraintName("fk_ocrval_row").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OutsourceContactColumn>().WithMany().HasForeignKey(x => x.ColumnId)
            .HasConstraintName("fk_ocrval_column").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutsourceContactRowPerson
{
    public ulong Id { get; set; }
    public ulong RowId { get; set; }
    public ulong ColumnId { get; set; }
    public ulong PersonId { get; set; }
    public string? PrefixLabel { get; set; }
    public int SortNo { get; set; }
}

public sealed class OutsourceContactRowPersonConfig : IEntityTypeConfiguration<OutsourceContactRowPerson>
{
    public void Configure(EntityTypeBuilder<OutsourceContactRowPerson> b)
    {
        b.ToTable("outsource_contact_row_people");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.RowId).HasColumnName("row_id");
        b.Property(x => x.ColumnId).HasColumnName("column_id");
        b.Property(x => x.PersonId).HasColumnName("person_id");
        b.Property(x => x.PrefixLabel).HasColumnName("prefix_label").HasMaxLength(32);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.HasIndex(x => new { x.RowId, x.ColumnId, x.PersonId }).IsUnique().HasDatabaseName("uk_ocrp_row_col_person");
        b.HasIndex(x => x.ColumnId).HasDatabaseName("idx_ocrp_column");
        b.HasIndex(x => x.PersonId).HasDatabaseName("idx_ocrp_person");
        b.HasOne<OutsourceContactRow>().WithMany().HasForeignKey(x => x.RowId)
            .HasConstraintName("fk_ocrp_row").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OutsourceContactColumn>().WithMany().HasForeignKey(x => x.ColumnId)
            .HasConstraintName("fk_ocrp_column").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OutsourceContactPerson>().WithMany().HasForeignKey(x => x.PersonId)
            .HasConstraintName("fk_ocrp_person").OnDelete(DeleteBehavior.Restrict);
    }
}
