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

/// <summary>
/// One outsourced work order (工令唯一). Purchasing edits only date, vendor, remark and status; every other
/// business field is a read-only IPMS snapshot. See 外协清单契约 → 数据与字段归属.
/// </summary>
public sealed class OutsourceItem
{
    public ulong Id { get; set; }
    public string WorkOrderNo { get; set; } = null!;
    public DateOnly OutsourceDate { get; set; }
    public ulong VendorId { get; set; }
    public string? Customer { get; set; }
    public string? MachineModel { get; set; }
    public string? MechanicalSection { get; set; }
    public string? MechanicalOwner { get; set; }
    public string? ElectricalOwner { get; set; }
    public DateOnly? WorkOrderIssuedOn { get; set; }
    public DateOnly? BomOn { get; set; }
    public DateOnly? DrawingReleasedOn { get; set; }
    /// <summary>Type exactly as IPMS reported it; the dictionary match is resolved on read.</summary>
    public string? TypeCode { get; set; }
    public int? Quantity { get; set; }
    /// <summary>Due date first obtained from IPMS; never changed afterwards.</summary>
    public DateOnly? OriginalDueDate { get; set; }
    public DateOnly? LatestDueDate { get; set; }
    public string? Remark { get; set; }
    /// <summary>NORMAL / PAUSED / CANCELLED, set by purchasing.</summary>
    public string Status { get; set; } = null!;
    /// <summary>UNSENT / SENT / PENDING_NOTICE.</summary>
    public string SendStatus { get; set; } = null!;
    /// <summary>Set when IPMS reports the work order finished; closed rows are hidden and no longer synchronized.</summary>
    public DateTime? ClosedAt { get; set; }
    public DateTime? IpmsSyncedAt { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Single-row (<c>id=1</c>) IPMS synchronization state: the daily time, a database-clock lease that keeps
/// scheduled and manual runs from overlapping, and the outcome of the last run.
/// </summary>
public sealed class OutsourceSyncState
{
    public byte Id { get; set; }
    /// <summary>Daily run time in business time (UTC+8), "HH:mm".</summary>
    public string SyncTime { get; set; } = null!;
    /// <summary>Business date the scheduled run last started for; a scheduled run happens at most once a day.</summary>
    public DateOnly? ScheduledFor { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastFinishedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public string? LastTrigger { get; set; }
    public int LastProcessed { get; set; }
    public int LastChanged { get; set; }
    public int LastClosed { get; set; }
    public int LastMissing { get; set; }
}

/// <summary>One changed field of an outsourcing item: IPMS sync or a purchasing edit. Kept with the item.</summary>
public sealed class OutsourceItemChange
{
    public ulong Id { get; set; }
    public ulong ItemId { get; set; }
    public string Field { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    /// <summary>EDIT, IPMS_SCHEDULED or IPMS_MANUAL.</summary>
    public string Source { get; set; } = null!;
    public ulong? ActorUserId { get; set; }
    public DateTime ChangedAt { get; set; }
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

public sealed class OutsourceItemConfig : IEntityTypeConfiguration<OutsourceItem>
{
    public void Configure(EntityTypeBuilder<OutsourceItem> b)
    {
        b.ToTable("outsource_items");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(32).IsRequired();
        b.Property(x => x.OutsourceDate).HasColumnName("outsource_date").HasColumnType("date");
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.Property(x => x.Customer).HasColumnName("customer").HasMaxLength(128);
        b.Property(x => x.MachineModel).HasColumnName("machine_model").HasMaxLength(128);
        b.Property(x => x.MechanicalSection).HasColumnName("mechanical_section").HasMaxLength(64);
        b.Property(x => x.MechanicalOwner).HasColumnName("mechanical_owner").HasMaxLength(64);
        b.Property(x => x.ElectricalOwner).HasColumnName("electrical_owner").HasMaxLength(64);
        b.Property(x => x.WorkOrderIssuedOn).HasColumnName("work_order_issued_on").HasColumnType("date");
        b.Property(x => x.BomOn).HasColumnName("bom_on").HasColumnType("date");
        b.Property(x => x.DrawingReleasedOn).HasColumnName("drawing_released_on").HasColumnType("date");
        b.Property(x => x.TypeCode).HasColumnName("type_code").HasMaxLength(64);
        b.Property(x => x.Quantity).HasColumnName("quantity");
        b.Property(x => x.OriginalDueDate).HasColumnName("original_due_date").HasColumnType("date");
        b.Property(x => x.LatestDueDate).HasColumnName("latest_due_date").HasColumnType("date");
        b.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(500);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("NORMAL");
        b.Property(x => x.SendStatus).HasColumnName("send_status").HasMaxLength(16).IsRequired().HasDefaultValue("UNSENT");
        b.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnType("datetime(3)");
        b.Property(x => x.IpmsSyncedAt).HasColumnName("ipms_synced_at").HasColumnType("datetime(3)");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.WorkOrderNo).IsUnique().HasDatabaseName("uk_outsource_items_work_order");
        b.HasIndex(x => new { x.VendorId, x.ClosedAt, x.OutsourceDate }).HasDatabaseName("idx_outsource_items_vendor_open");
        b.HasIndex(x => new { x.ClosedAt, x.OutsourceDate, x.Id }).HasDatabaseName("idx_outsource_items_open_date");
        b.HasOne<OutsourceVendor>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_outsource_items_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceSyncStateConfig : IEntityTypeConfiguration<OutsourceSyncState>
{
    public void Configure(EntityTypeBuilder<OutsourceSyncState> b)
    {
        b.ToTable("outsource_sync_state");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.SyncTime).HasColumnName("sync_time").HasMaxLength(5).IsRequired().HasDefaultValue("02:00");
        b.Property(x => x.ScheduledFor).HasColumnName("scheduled_for").HasColumnType("date");
        b.Property(x => x.LeaseUntil).HasColumnName("lease_until").HasColumnType("datetime(3)");
        b.Property(x => x.LastStartedAt).HasColumnName("last_started_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastFinishedAt).HasColumnName("last_finished_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastSuccessAt).HasColumnName("last_success_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(512);
        b.Property(x => x.LastTrigger).HasColumnName("last_trigger").HasMaxLength(16);
        b.Property(x => x.LastProcessed).HasColumnName("last_processed").HasDefaultValue(0);
        b.Property(x => x.LastChanged).HasColumnName("last_changed").HasDefaultValue(0);
        b.Property(x => x.LastClosed).HasColumnName("last_closed").HasDefaultValue(0);
        b.Property(x => x.LastMissing).HasColumnName("last_missing").HasDefaultValue(0);
    }
}

public sealed class OutsourceItemChangeConfig : IEntityTypeConfiguration<OutsourceItemChange>
{
    public void Configure(EntityTypeBuilder<OutsourceItemChange> b)
    {
        b.ToTable("outsource_item_changes");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemId).HasColumnName("item_id");
        b.Property(x => x.Field).HasColumnName("field").HasMaxLength(32).IsRequired();
        b.Property(x => x.OldValue).HasColumnName("old_value").HasMaxLength(500);
        b.Property(x => x.NewValue).HasColumnName("new_value").HasMaxLength(500);
        b.Property(x => x.Source).HasColumnName("source").HasMaxLength(16).IsRequired();
        b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        b.Property(x => x.ChangedAt).HasColumnName("changed_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => new { x.ItemId, x.Id }).HasDatabaseName("idx_outsource_item_changes_item");
        // History belongs to the item; only unsent items can be deleted, and then their history goes too.
        b.HasOne<OutsourceItem>().WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("fk_outsource_item_changes_item").OnDelete(DeleteBehavior.Cascade);
    }
}
