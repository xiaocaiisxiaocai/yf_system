using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Infrastructure.Entities;

// Outsourcing vendors and their contacts are the ODM/OEMA companies (oem_companies, oem_company_contacts):
// every VendorId below is an oem_companies id (since 2026-11-17).

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
    /// <summary>The list's 序号: assigned at import, continuing from the highest number; never reused or renumbered.</summary>
    public uint SerialNo { get; set; }
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
    /// <summary>Vendor that last received this item; a different current vendor gets a withdrawal for the old one.</summary>
    public ulong? NotifiedVendorId { get; set; }
    /// <summary>JSON of the field values as last mailed, to highlight what changed since.</summary>
    public string? LastSentSnapshot { get; set; }
    public DateTime? LastSentAt { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Who last changed the item by hand (an edit or marking it finished) and when; IPMS syncs and sending do not count.</summary>
    public ulong? ModifiedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    /// <summary>Reserved for vendors filling columns in (not offered yet): when the vendor last did, and the name it signed with.</summary>
    public DateTime? VendorUpdatedAt { get; set; }
    public string? VendorUpdatedSigner { get; set; }
    /// <summary>Reserved: when internal staff marked the vendor's update as seen.</summary>
    public DateTime? VendorSeenAt { get; set; }
}

/// <summary>
/// One column of the outsourcing list and how it is handled (外协清单契约 → 栏位设定, 2026-11-19). Built-in rows are the
/// fixed columns (their key is the column key, their label and type never change); custom rows are added on the field
/// settings page and keep their values in <see cref="OutsourceItemValue"/>. Who may fill a column in is
/// <see cref="EditorScope"/> (internal staff) and <see cref="VendorEditable"/>.
/// </summary>
public sealed class OutsourceField
{
    public ulong Id { get; set; }
    public string FieldKey { get; set; } = null!;
    public bool BuiltIn { get; set; }
    public string Label { get; set; } = null!;
    /// <summary>TEXT / BOOLEAN / DATE / NUMBER / SELECT.</summary>
    public string DataType { get; set; } = null!;
    /// <summary>JSON array of the choices of a SELECT field.</summary>
    public string? Options { get; set; }
    public int SortNo { get; set; }
    /// <summary>ACTIVE / DISABLED; only custom fields can be disabled (their values are kept).</summary>
    public string Status { get; set; } = null!;
    /// <summary>
    /// Who fills it in internally: ALL (everyone with outsource:manage), ROLES (<see cref="OutsourceFieldEditorRole"/>),
    /// USERS (<see cref="OutsourceFieldEditorUser"/>), NONE.
    /// </summary>
    public string EditorScope { get; set; } = null!;
    public bool VendorVisible { get; set; }
    public bool VendorEditable { get; set; }
    public ulong? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>With editor scope ROLES: an internal role whose members may fill in the field (system administrators always may).</summary>
public sealed class OutsourceFieldEditorRole
{
    public ulong FieldId { get; set; }
    public ulong RoleId { get; set; }
}

/// <summary>With editor scope USERS: an internal account that may fill in the field (system administrators always may).</summary>
public sealed class OutsourceFieldEditorUser
{
    public ulong FieldId { get; set; }
    public ulong UserId { get; set; }
}

/// <summary>The value of a custom field on one item; no row means empty.</summary>
public sealed class OutsourceItemValue
{
    public ulong ItemId { get; set; }
    public ulong FieldId { get; set; }
    public string Value { get; set; } = null!;
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
    /// <summary>Whether the daily update of the outsourcing list runs; a manual sync works either way.</summary>
    public bool ScheduleEnabled { get; set; } = true;
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
    /// <summary>Reserved for a change the vendor makes in the portal (<c>VENDOR</c>): the account and the name it signed with.</summary>
    public ulong? ActorOemAccountId { get; set; }
    public string? SignerName { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>One confirmed send: makes a repeated confirm click idempotent.</summary>
public sealed class OutsourceMailBatch
{
    public string RequestKey { get; set; } = null!;
    public ulong CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One vendor mail (outsourcing notice or withdrawal) with all its recipients in one message. Content and the
/// attachment are frozen at confirmation; retries resend exactly this.
/// </summary>
public sealed class OutsourceMail
{
    public ulong Id { get; set; }
    public string BatchKey { get; set; } = null!;
    /// <summary>NOTICE or WITHDRAW.</summary>
    public string Kind { get; set; } = null!;
    public ulong VendorId { get; set; }
    public string Subject { get; set; } = null!;
    public string BodyHtml { get; set; } = null!;
    public string BodyText { get; set; } = null!;
    public string? AttachmentName { get; set; }
    public byte[]? Attachment { get; set; }
    /// <summary>QUEUED, SENDING, SENT, PARTIAL or FAILED.</summary>
    public string Status { get; set; } = null!;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public ulong CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public sealed class OutsourceMailRecipient
{
    public ulong Id { get; set; }
    public ulong MailId { get; set; }
    /// <summary>TO or CC.</summary>
    public string Kind { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? Name { get; set; }
    /// <summary>VENDOR, DEFAULT_CC or EXTRA.</summary>
    public string Source { get; set; } = null!;
    public bool Rejected { get; set; }
}

public sealed class OutsourceMailItem
{
    public ulong MailId { get; set; }
    public ulong ItemId { get; set; }
    public string WorkOrderNo { get; set; } = null!;
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
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_outsource_type_vendors_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// 客户全称 → 简称, generated from the IPMS 工单查询 list on demand. A short name set by hand (<see cref="Manual"/>) is kept
/// by later syncs. The outsourcing list shows the short name in place of the full one.
/// </summary>
public sealed class OutsourceCustomerAlias
{
    public ulong Id { get; set; }
    public string FullName { get; set; } = null!;
    public string ShortName { get; set; } = null!;
    public bool Manual { get; set; }
    public ulong? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OutsourceCustomerAliasConfig : IEntityTypeConfiguration<OutsourceCustomerAlias>
{
    public void Configure(EntityTypeBuilder<OutsourceCustomerAlias> b)
    {
        b.ToTable("outsource_customer_aliases");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(128).IsRequired();
        b.Property(x => x.ShortName).HasColumnName("short_name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Manual).HasColumnName("manual").HasDefaultValue(false);
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)");
        b.HasIndex(x => x.FullName).IsUnique().HasDatabaseName("uk_outsource_customer_aliases_full_name");
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
        b.Property(x => x.SerialNo).HasColumnName("serial_no");
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
        b.Property(x => x.NotifiedVendorId).HasColumnName("notified_vendor_id");
        b.Property(x => x.LastSentSnapshot).HasColumnName("last_sent_snapshot").HasColumnType("text");
        b.Property(x => x.LastSentAt).HasColumnName("last_sent_at").HasColumnType("datetime(3)");
        b.Property(x => x.ModifiedBy).HasColumnName("modified_by");
        b.Property(x => x.ModifiedAt).HasColumnName("modified_at").HasColumnType("datetime(3)");
        b.Property(x => x.VendorUpdatedAt).HasColumnName("vendor_updated_at").HasColumnType("datetime(3)");
        b.Property(x => x.VendorUpdatedSigner).HasColumnName("vendor_updated_signer").HasMaxLength(32);
        b.Property(x => x.VendorSeenAt).HasColumnName("vendor_seen_at").HasColumnType("datetime(3)");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.WorkOrderNo).IsUnique().HasDatabaseName("uk_outsource_items_work_order");
        b.HasIndex(x => x.SerialNo).IsUnique().HasDatabaseName("uk_outsource_items_serial");
        b.HasIndex(x => new { x.VendorId, x.ClosedAt, x.OutsourceDate }).HasDatabaseName("idx_outsource_items_vendor_open");
        b.HasIndex(x => new { x.ClosedAt, x.OutsourceDate, x.Id }).HasDatabaseName("idx_outsource_items_open_date");
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.VendorId)
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
        b.Property(x => x.ScheduleEnabled).HasColumnName("schedule_enabled").HasDefaultValue(true);
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
        b.Property(x => x.ActorOemAccountId).HasColumnName("actor_oem_account_id");
        b.Property(x => x.SignerName).HasColumnName("signer_name").HasMaxLength(32);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => new { x.ItemId, x.Id }).HasDatabaseName("idx_outsource_item_changes_item");
        // History belongs to the item; deleting an item deletes its history (the audit log keeps the deletion).
        b.HasOne<OutsourceItem>().WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("fk_outsource_item_changes_item").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutsourceMailBatchConfig : IEntityTypeConfiguration<OutsourceMailBatch>
{
    public void Configure(EntityTypeBuilder<OutsourceMailBatch> b)
    {
        b.ToTable("outsource_mail_batches");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.RequestKey);
        b.Property(x => x.RequestKey).HasColumnName("request_key").HasMaxLength(64);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
    }
}

public sealed class OutsourceMailConfig : IEntityTypeConfiguration<OutsourceMail>
{
    public void Configure(EntityTypeBuilder<OutsourceMail> b)
    {
        b.ToTable("outsource_mails");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.BatchKey).HasColumnName("batch_key").HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        b.Property(x => x.VendorId).HasColumnName("vendor_id");
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(255).IsRequired();
        b.Property(x => x.BodyHtml).HasColumnName("body_html").HasColumnType("mediumtext").IsRequired();
        b.Property(x => x.BodyText).HasColumnName("body_text").HasColumnType("mediumtext").IsRequired();
        b.Property(x => x.AttachmentName).HasColumnName("attachment_name").HasMaxLength(128);
        b.Property(x => x.Attachment).HasColumnName("attachment").HasColumnType("mediumblob");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(512);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("datetime(3)");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt, x.Id }).HasDatabaseName("idx_outsource_mails_queue");
        b.HasIndex(x => x.BatchKey).HasDatabaseName("idx_outsource_mails_batch");
        b.HasIndex(x => new { x.VendorId, x.Id }).HasDatabaseName("idx_outsource_mails_vendor");
        b.HasOne<OutsourceMailBatch>().WithMany().HasForeignKey(x => x.BatchKey)
            .HasConstraintName("fk_outsource_mails_batch").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.VendorId)
            .HasConstraintName("fk_outsource_mails_vendor").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceMailRecipientConfig : IEntityTypeConfiguration<OutsourceMailRecipient>
{
    public void Configure(EntityTypeBuilder<OutsourceMailRecipient> b)
    {
        b.ToTable("outsource_mail_recipients");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.MailId).HasColumnName("mail_id");
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(8).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(128).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64);
        b.Property(x => x.Source).HasColumnName("source").HasMaxLength(16).IsRequired();
        b.Property(x => x.Rejected).HasColumnName("rejected").HasDefaultValue(false);
        b.HasIndex(x => new { x.MailId, x.Id }).HasDatabaseName("idx_outsource_mail_recipients_mail");
        b.HasOne<OutsourceMail>().WithMany().HasForeignKey(x => x.MailId)
            .HasConstraintName("fk_outsource_mail_recipients_mail").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutsourceMailItemConfig : IEntityTypeConfiguration<OutsourceMailItem>
{
    public void Configure(EntityTypeBuilder<OutsourceMailItem> b)
    {
        b.ToTable("outsource_mail_items");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.MailId, x.ItemId });
        b.Property(x => x.MailId).HasColumnName("mail_id");
        // No FK to items: sent rows can never be deleted, and the work-order number keeps the record readable.
        b.Property(x => x.ItemId).HasColumnName("item_id");
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(32).IsRequired();
        b.HasIndex(x => x.ItemId).HasDatabaseName("idx_outsource_mail_items_item");
        b.HasOne<OutsourceMail>().WithMany().HasForeignKey(x => x.MailId)
            .HasConstraintName("fk_outsource_mail_items_mail").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutsourceFieldConfig : IEntityTypeConfiguration<OutsourceField>
{
    public void Configure(EntityTypeBuilder<OutsourceField> b)
    {
        b.ToTable("outsource_fields");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.FieldKey).HasColumnName("field_key").HasMaxLength(32).IsRequired();
        b.Property(x => x.BuiltIn).HasColumnName("built_in").HasDefaultValue(false);
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(32).IsRequired();
        b.Property(x => x.DataType).HasColumnName("data_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.Options).HasColumnName("options").HasMaxLength(2000);
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.EditorScope).HasColumnName("editor_scope").HasMaxLength(16).IsRequired().HasDefaultValue("ALL");
        b.Property(x => x.VendorVisible).HasColumnName("vendor_visible").HasDefaultValue(false);
        b.Property(x => x.VendorEditable).HasColumnName("vendor_editable").HasDefaultValue(false);
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.FieldKey).IsUnique().HasDatabaseName("uk_outsource_fields_key");
    }
}

public sealed class OutsourceFieldEditorRoleConfig : IEntityTypeConfiguration<OutsourceFieldEditorRole>
{
    public void Configure(EntityTypeBuilder<OutsourceFieldEditorRole> b)
    {
        b.ToTable("outsource_field_editor_roles");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.FieldId, x.RoleId });
        b.Property(x => x.FieldId).HasColumnName("field_id");
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.HasIndex(x => x.RoleId).HasDatabaseName("idx_outsource_field_editor_roles_role");
        b.HasOne<OutsourceField>().WithMany().HasForeignKey(x => x.FieldId)
            .HasConstraintName("fk_outsource_field_editor_roles_field").OnDelete(DeleteBehavior.Cascade);
        // Deleting a role removes it from every field's editors.
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
            .HasConstraintName("fk_outsource_field_editor_roles_role").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutsourceItemValueConfig : IEntityTypeConfiguration<OutsourceItemValue>
{
    public void Configure(EntityTypeBuilder<OutsourceItemValue> b)
    {
        b.ToTable("outsource_item_values");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.ItemId, x.FieldId });
        b.Property(x => x.ItemId).HasColumnName("item_id");
        b.Property(x => x.FieldId).HasColumnName("field_id");
        b.Property(x => x.Value).HasColumnName("value").HasMaxLength(500).IsRequired();
        b.HasIndex(x => x.FieldId).HasDatabaseName("idx_outsource_item_values_field");
        b.HasOne<OutsourceItem>().WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("fk_outsource_item_values_item").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OutsourceField>().WithMany().HasForeignKey(x => x.FieldId)
            .HasConstraintName("fk_outsource_item_values_field").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutsourceFieldEditorUserConfig : IEntityTypeConfiguration<OutsourceFieldEditorUser>
{
    public void Configure(EntityTypeBuilder<OutsourceFieldEditorUser> b)
    {
        b.ToTable("outsource_field_editor_users");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.FieldId, x.UserId });
        b.Property(x => x.FieldId).HasColumnName("field_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.HasIndex(x => x.UserId).HasDatabaseName("idx_outsource_field_editor_users_user");
        b.HasOne<OutsourceField>().WithMany().HasForeignKey(x => x.FieldId)
            .HasConstraintName("fk_outsource_field_editor_users_field").OnDelete(DeleteBehavior.Cascade);
        // Deleting an account removes it from every field's editors.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_outsource_field_editor_users_user").OnDelete(DeleteBehavior.Cascade);
    }
}
