using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public static class ProjectCopyJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class ProjectCopyJob
{
    public ulong Id { get; set; }
    public ulong SourceProjectId { get; set; }
    public ulong ProjectGroupId { get; set; }
    public ulong RequestedBy { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string TargetName { get; set; } = null!;
    public string RequestIp { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? ExecutionToken { get; set; }
    public ulong WorkerEpoch { get; set; }
    public ulong FilesTotal { get; set; }
    public ulong FilesCopied { get; set; }
    public ulong BytesTotal { get; set; }
    public ulong BytesCopied { get; set; }
    public string? Error { get; set; }
    public ulong? ResultProjectId { get; set; }
    public ulong? ResultCopyId { get; set; }
    public ulong? ResultCopyFileCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class ProjectCopyWorkerState
{
    public byte Id { get; set; }
    public ulong Epoch { get; set; }
}

public sealed class ProjectCopyJobConfig : IEntityTypeConfiguration<ProjectCopyJob>
{
    public void Configure(EntityTypeBuilder<ProjectCopyJob> b)
    {
        b.ToTable("project_copy_jobs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceProjectId).HasColumnName("source_project_id");
        b.Property(x => x.ProjectGroupId).HasColumnName("project_group_id");
        b.Property(x => x.RequestedBy).HasColumnName("requested_by");
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(64).IsRequired();
        b.Property(x => x.TargetName).HasColumnName("target_name").HasMaxLength(128).IsRequired();
        b.Property(x => x.RequestIp).HasColumnName("request_ip").HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        b.Property(x => x.ExecutionToken).HasColumnName("execution_token").HasMaxLength(32);
        b.Property(x => x.WorkerEpoch).HasColumnName("worker_epoch");
        b.Property(x => x.FilesTotal).HasColumnName("files_total");
        b.Property(x => x.FilesCopied).HasColumnName("files_copied");
        b.Property(x => x.BytesTotal).HasColumnName("bytes_total");
        b.Property(x => x.BytesCopied).HasColumnName("bytes_copied");
        b.Property(x => x.Error).HasColumnName("error").HasMaxLength(255);
        b.Property(x => x.ResultProjectId).HasColumnName("result_project_id");
        b.Property(x => x.ResultCopyId).HasColumnName("result_copy_id");
        b.Property(x => x.ResultCopyFileCount).HasColumnName("result_copy_file_count");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(6)");
        b.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("datetime(6)");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("datetime(6)");
        b.HasIndex(x => new { x.RequestedBy, x.IdempotencyKey }).IsUnique()
            .HasDatabaseName("uk_project_copy_jobs_actor_key");
        b.HasIndex(x => new { x.Status, x.CreatedAt, x.Id })
            .HasDatabaseName("idx_project_copy_jobs_queue");
        b.HasIndex(x => new { x.ProjectGroupId, x.RequestedBy, x.Status, x.CreatedAt, x.Id })
            .HasDatabaseName("idx_project_copy_jobs_group_actor_status_time");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy)
            .HasConstraintName("fk_project_copy_jobs_requested_by").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.SourceProjectId)
            .HasConstraintName("fk_project_copy_jobs_source").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProjectGroup>().WithMany().HasForeignKey(x => x.ProjectGroupId)
            .HasConstraintName("fk_project_copy_jobs_group").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ResultProjectId)
            .HasConstraintName("fk_project_copy_jobs_result_project").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectCopy>().WithMany().HasForeignKey(x => x.ResultCopyId)
            .HasConstraintName("fk_project_copy_jobs_result_copy").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectCopyWorkerStateConfig : IEntityTypeConfiguration<ProjectCopyWorkerState>
{
    public void Configure(EntityTypeBuilder<ProjectCopyWorkerState> b)
    {
        b.ToTable("project_copy_worker_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.Epoch).HasColumnName("epoch");
        b.HasData(new ProjectCopyWorkerState { Id = 1, Epoch = 0 });
    }
}
