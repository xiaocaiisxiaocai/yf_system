using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public sealed class Message
{
    public ulong Id { get; set; }
    public ulong ProjectId { get; set; }
    public ulong SenderId { get; set; }
    public string Content { get; set; } = null!;
    public string Status { get; set; } = null!;
    public ulong? DeletedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MessageRead
{
    public ulong MessageId { get; set; }
    public ulong UserId { get; set; }
    public DateTime ReadAt { get; set; }
}

public sealed class MessageImage
{
    public ulong Id { get; set; }
    public ulong MessageId { get; set; }
    public string OriginalName { get; set; } = null!;
    public string StoredName { get; set; } = null!;
    public string Ext { get; set; } = null!;
    public ulong SizeBytes { get; set; }
    public string MimeType { get; set; } = null!;
    public string StoragePath { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public sealed class MessageConfig : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.SenderId).HasColumnName("sender_id");
        b.Property(x => x.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("NORMAL");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("datetime");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.SenderId).HasDatabaseName("fk_msg_sender");
        b.HasIndex(x => new { x.ProjectId, x.CreatedAt }).HasDatabaseName("idx_msg_project_time");
        b.HasIndex(x => new { x.ProjectId, x.Status, x.Id }).HasDatabaseName("idx_msg_project_status_id");
        // Bounds cross-project unread counts to the recent unread window (see UnreadWindow).
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("idx_msg_created");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_msg_project").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).HasConstraintName("fk_msg_sender").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MessageReadConfig : IEntityTypeConfiguration<MessageRead>
{
    public void Configure(EntityTypeBuilder<MessageRead> b)
    {
        b.ToTable("message_reads");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.MessageId, x.UserId });
        b.Property(x => x.MessageId).HasColumnName("message_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.ReadAt).HasColumnName("read_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.UserId).HasDatabaseName("idx_mr_user");
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).HasConstraintName("fk_mr_msg").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_mr_user").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MessageImageConfig : IEntityTypeConfiguration<MessageImage>
{
    public void Configure(EntityTypeBuilder<MessageImage> b)
    {
        b.ToTable("message_images");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.MessageId).HasColumnName("message_id");
        b.Property(x => x.OriginalName).HasColumnName("original_name").HasMaxLength(255).IsRequired();
        b.Property(x => x.StoredName).HasColumnName("stored_name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Ext).HasColumnName("ext").HasMaxLength(8).IsRequired();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(32).IsRequired();
        b.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(512).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => x.StoredName).IsUnique().HasDatabaseName("uk_message_images_stored_name");
        b.HasIndex(x => new { x.MessageId, x.Id }).HasDatabaseName("idx_message_images_message");
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).HasConstraintName("fk_message_images_message").OnDelete(DeleteBehavior.Cascade);
    }
}
