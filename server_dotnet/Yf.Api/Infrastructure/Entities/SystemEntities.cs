using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public sealed class AuditLog
{
    public ulong Id { get; set; }
    public ulong? UserId { get; set; }
    public string? EmployeeNo { get; set; }
    public string Action { get; set; } = null!;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    // Stored as a JSON column and kept as an already-serialized payload. Business
    // queries do not deserialize or modify individual JSON fields in the database.
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class EmailOutbox
{
    public ulong Id { get; set; }
    public string EventType { get; set; } = null!;
    public string? DedupeKey { get; set; }
    public ulong? ProjectId { get; set; }
    public ulong? RecipientUserId { get; set; }
    public string RecipientEmail { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Body { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int RetryCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SystemConfig
{
    public string CfgKey { get; set; } = null!;
    public string? CfgValue { get; set; }
    public string? Description { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.EmployeeNo).HasColumnName("employee_no").HasMaxLength(64);
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(48).IsRequired();
        b.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(32);
        b.Property(x => x.TargetId).HasColumnName("target_id").HasMaxLength(64);
        b.Property(x => x.Detail).HasColumnName("detail").HasColumnType("json");
        b.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("idx_audit_time");
        b.HasIndex(x => x.UserId).HasDatabaseName("idx_audit_user");
        b.HasIndex(x => x.Action).HasDatabaseName("idx_audit_action");
        b.HasIndex(x => new { x.Action, x.CreatedAt, x.Id }).HasDatabaseName("idx_audit_action_time");
        b.HasIndex(x => new { x.TargetType, x.TargetId, x.Id }).HasDatabaseName("idx_audit_target");
        // Deliberately no FK on user_id: audit rows must survive account deletion/rename (employee_no is a snapshot).
    }
}

public sealed class EmailOutboxConfig : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> b)
    {
        b.ToTable("email_outbox");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(32).IsRequired();
        b.Property(x => x.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(128);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id");
        b.Property(x => x.RecipientEmail).HasColumnName("recipient_email").HasMaxLength(128).IsRequired();
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(255).IsRequired();
        b.Property(x => x.Body).HasColumnName("body").HasColumnType("text").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("PENDING");
        b.Property(x => x.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1024);
        b.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("datetime");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.DedupeKey).IsUnique().HasDatabaseName("uk_outbox_dedupe_key");
        b.HasIndex(x => new { x.Status, x.RetryCount }).HasDatabaseName("idx_outbox_status");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_outbox_status_next");
        b.HasIndex(x => x.ProjectId).HasDatabaseName("fk_outbox_project_v2");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_outbox_project_v2").OnDelete(DeleteBehavior.Restrict);
        // Deliberately no FK on recipient_user_id: recipient_email is the durable snapshot used at send time.
    }
}

public sealed class SystemConfigConfig : IEntityTypeConfiguration<SystemConfig>
{
    public void Configure(EntityTypeBuilder<SystemConfig> b)
    {
        b.ToTable("system_configs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.CfgKey);
        b.Property(x => x.CfgKey).HasColumnName("cfg_key").HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.CfgValue).HasColumnName("cfg_value").HasMaxLength(2048);
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(255);
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
    }
}
