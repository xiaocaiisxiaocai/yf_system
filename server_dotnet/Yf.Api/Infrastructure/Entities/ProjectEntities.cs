using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yf.Api.Infrastructure;

namespace Yf.Api.Infrastructure.Entities;

public sealed class ProjectGroup
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public ulong SupplierId { get; set; }
    public string Status { get; set; } = null!;
    public ulong CreatedBy { get; set; }
    public string? MachineModel { get; set; }
    public ulong? RobotPartId { get; set; }
    public string? LegacyRobotModelName { get; set; }
    public ulong? ResponsibleUserId { get; set; }
    public ulong? SectionId { get; set; }
    public ulong? PriorityId { get; set; }
    public DateOnly? ExpectedCompletionDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProjectGroupWorkOrder
{
    public ulong Id { get; set; }
    public ulong ProjectGroupId { get; set; }
    public string WorkOrderNo { get; set; } = null!;
    public int SortNo { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ProjectGroupStatusLog
{
    public ulong Id { get; set; }
    public ulong ProjectGroupId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = null!;
    public string Action { get; set; } = null!;
    public ulong? TriggerProjectId { get; set; }
    public ulong OperatorId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Project
{
    public ulong Id { get; set; }
    public ulong ProjectGroupId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public ulong SupplierId { get; set; }
    public string Status { get; set; } = null!;
    public string? ConfirmSide { get; set; }
    public ulong CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? MachineModel { get; set; }
    public ulong? RobotPartId { get; set; }
    public string? LegacyRobotModelName { get; set; }
    public ulong? ResponsibleUserId { get; set; }
    public ulong? SectionId { get; set; }
    public ulong? PriorityId { get; set; }
    public DateOnly? ExpectedCompletionDate { get; set; }
}

public sealed class ProjectWorkOrder
{
    public ulong Id { get; set; }
    public ulong ProjectId { get; set; }
    public string WorkOrderNo { get; set; } = null!;
    public int SortNo { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ProjectStatusLog
{
    public ulong Id { get; set; }
    public ulong ProjectId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = null!;
    public string Action { get; set; } = null!;
    public ulong OperatorId { get; set; }
    public string? ConfirmSide { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ProjectDictionary
{
    public ulong Id { get; set; }
    public string Type { get; set; } = null!;
    public string Name { get; set; } = null!;
    public ulong? ParentId { get; set; }
    public int SortNo { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RobotPart
{
    public ulong Id { get; set; }
    public ulong SupplierId { get; set; }
    public string PartNumber { get; set; } = null!;
    public string Model { get; set; } = null!;
    public int SortNo { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProjectCopy
{
    public ulong Id { get; set; }
    public ulong SourceProjectId { get; set; }
    public ulong TargetProjectId { get; set; }
    public string SourceProjectName { get; set; } = null!;
    public string TargetProjectName { get; set; } = null!;
    public ulong CopiedBy { get; set; }
    public string CopiedByName { get; set; } = null!;
    public ulong FileCount { get; set; }
    public ulong TotalBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ProjectActivity
{
    public ulong Id { get; set; }
    public ulong ProjectId { get; set; }
    public string ActivityType { get; set; } = null!;
    public string Action { get; set; } = null!;
    public ulong? ActorId { get; set; }
    public string ActorName { get; set; } = null!;
    public DateTime OccurredAt { get; set; }
    public string Title { get; set; } = null!;
    public string? Summary { get; set; }
    public ulong? TargetId { get; set; }
    public string SourceKey { get; set; } = null!;
}

public sealed class CollaborationRead
{
    public ulong ActivityId { get; set; }
    public ulong UserId { get; set; }
    public DateTime ReadAt { get; set; }
}

public sealed class ProjectGroupConfig : IEntityTypeConfiguration<ProjectGroup>
{
    public void Configure(EntityTypeBuilder<ProjectGroup> b)
    {
        b.ToTable("project_groups");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
        b.Property(x => x.SupplierId).HasColumnName("supplier_id");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired().HasDefaultValue("DRAFT");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.MachineModel).HasColumnName("machine_model").HasMaxLength(128);
        b.Property(x => x.RobotPartId).HasColumnName("robot_part_id");
        b.Property(x => x.LegacyRobotModelName).HasColumnName("legacy_robot_model_name").HasMaxLength(512);
        b.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id");
        b.Property(x => x.SectionId).HasColumnName("section_id");
        b.Property(x => x.PriorityId).HasColumnName("priority_id");
        b.Property(x => x.ExpectedCompletionDate).HasColumnName("expected_completion_date").HasColumnType("date");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("datetime(3)");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_project_groups_name");
        b.HasIndex(x => x.SupplierId).HasDatabaseName("idx_project_groups_supplier");
        b.HasIndex(x => x.Status).HasDatabaseName("idx_project_groups_status");
        b.HasIndex(x => x.ResponsibleUserId).HasDatabaseName("idx_project_groups_responsible_user");
        b.HasIndex(x => x.ExpectedCompletionDate).HasDatabaseName("idx_project_groups_expected_completion");
        b.HasIndex(x => x.CreatedBy).HasDatabaseName("fk_project_groups_created_by");
        b.HasIndex(x => x.RobotPartId).HasDatabaseName("idx_project_groups_robot_part");
        b.HasIndex(x => x.SectionId).HasDatabaseName("fk_project_groups_section");
        b.HasIndex(x => x.PriorityId).HasDatabaseName("fk_project_groups_priority");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).HasConstraintName("fk_project_groups_created_by").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectDictionary>().WithMany().HasForeignKey(x => x.PriorityId).HasConstraintName("fk_project_groups_priority").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).HasConstraintName("fk_project_groups_responsible_user").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RobotPart>().WithMany().HasForeignKey(x => x.RobotPartId).HasConstraintName("fk_project_groups_robot_part").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.SectionId).HasConstraintName("fk_project_groups_section").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).HasConstraintName("fk_project_groups_supplier").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectGroupWorkOrderConfig : IEntityTypeConfiguration<ProjectGroupWorkOrder>
{
    public void Configure(EntityTypeBuilder<ProjectGroupWorkOrder> b)
    {
        b.ToTable("project_group_work_orders");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectGroupId).HasColumnName("project_group_id");
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(128).IsRequired();
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => new { x.ProjectGroupId, x.WorkOrderNo }).IsUnique().HasDatabaseName("uk_project_group_work_orders_group_no");
        b.HasIndex(x => new { x.ProjectGroupId, x.SortNo, x.Id }).HasDatabaseName("idx_project_group_work_orders_group_sort");
        b.HasOne<ProjectGroup>().WithMany().HasForeignKey(x => x.ProjectGroupId).HasConstraintName("fk_project_group_work_orders_group").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProjectGroupStatusLogConfig : IEntityTypeConfiguration<ProjectGroupStatusLog>
{
    public void Configure(EntityTypeBuilder<ProjectGroupStatusLog> b)
    {
        b.ToTable("project_group_status_logs");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectGroupId).HasColumnName("project_group_id");
        b.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(24);
        b.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(24).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(32).IsRequired();
        b.Property(x => x.TriggerProjectId).HasColumnName("trigger_project_id");
        b.Property(x => x.OperatorId).HasColumnName("operator_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)");
        b.HasIndex(x => new { x.ProjectGroupId, x.CreatedAt, x.Id }).HasDatabaseName("idx_project_group_status_logs_group_time");
        b.HasIndex(x => x.TriggerProjectId).HasDatabaseName("idx_project_group_status_logs_project");
        b.HasIndex(x => x.OperatorId).HasDatabaseName("idx_project_group_status_logs_operator");
        b.HasOne<ProjectGroup>().WithMany().HasForeignKey(x => x.ProjectGroupId).HasConstraintName("fk_project_group_status_logs_group").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorId).HasConstraintName("fk_project_group_status_logs_operator").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.TriggerProjectId).HasConstraintName("fk_project_group_status_logs_project").OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ProjectConfig : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectGroupId).HasColumnName("project_group_id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
        b.Property(x => x.SupplierId).HasColumnName("supplier_id");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired().HasDefaultValue("DRAFT");
        b.Property(x => x.ConfirmSide).HasColumnName("confirm_side").HasMaxLength(16);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        b.Property(x => x.MachineModel).HasColumnName("machine_model").HasMaxLength(128);
        b.Property(x => x.RobotPartId).HasColumnName("robot_part_id");
        b.Property(x => x.LegacyRobotModelName).HasColumnName("legacy_robot_model_name").HasMaxLength(512);
        b.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id");
        b.Property(x => x.SectionId).HasColumnName("section_id");
        b.Property(x => x.PriorityId).HasColumnName("priority_id");
        b.Property(x => x.ExpectedCompletionDate).HasColumnName("expected_completion_date").HasColumnType("date");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_projects_name");
        b.HasIndex(x => x.SupplierId).HasDatabaseName("idx_projects_supplier");
        b.HasIndex(x => x.Status).HasDatabaseName("idx_projects_status");
        b.HasIndex(x => x.RobotPartId).HasDatabaseName("idx_projects_robot_part");
        b.HasIndex(x => x.ResponsibleUserId).HasDatabaseName("idx_projects_responsible_user");
        b.HasIndex(x => x.SectionId).HasDatabaseName("idx_projects_section");
        b.HasIndex(x => x.PriorityId).HasDatabaseName("idx_projects_priority");
        b.HasIndex(x => x.ExpectedCompletionDate).HasDatabaseName("idx_projects_expected_completion");
        b.HasIndex(x => new { x.ProjectGroupId, x.Id }).HasDatabaseName("idx_projects_group");
        b.HasOne<ProjectGroup>().WithMany().HasForeignKey(x => x.ProjectGroupId).HasConstraintName("fk_projects_group").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectDictionary>().WithMany().HasForeignKey(x => x.PriorityId).HasConstraintName("fk_projects_priority").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).HasConstraintName("fk_projects_responsible_user").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RobotPart>().WithMany().HasForeignKey(x => x.RobotPartId).HasConstraintName("fk_projects_robot_part").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.SectionId).HasConstraintName("fk_projects_section").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).HasConstraintName("fk_projects_supplier").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectWorkOrderConfig : IEntityTypeConfiguration<ProjectWorkOrder>
{
    public void Configure(EntityTypeBuilder<ProjectWorkOrder> b)
    {
        b.ToTable("project_work_orders");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(128).IsRequired();
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => new { x.ProjectId, x.WorkOrderNo }).IsUnique().HasDatabaseName("uk_project_work_orders_project_no");
        b.HasIndex(x => new { x.ProjectId, x.SortNo, x.Id }).HasDatabaseName("idx_project_work_orders_project_sort");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_project_work_orders_project").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProjectStatusLogConfig : IEntityTypeConfiguration<ProjectStatusLog>
{
    public void Configure(EntityTypeBuilder<ProjectStatusLog> b)
    {
        b.ToTable("project_status_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(24);
        b.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(24).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(16).IsRequired();
        b.Property(x => x.OperatorId).HasColumnName("operator_id");
        b.Property(x => x.ConfirmSide).HasColumnName("confirm_side").HasMaxLength(16);
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1024);
        // No column default: the app always supplies an exact timestamp for this audit trail.
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)");
        b.HasIndex(x => new { x.ProjectId, x.CreatedAt, x.Id }).HasDatabaseName("idx_project_status_logs_project_time");
        b.HasIndex(x => x.OperatorId).HasDatabaseName("fk_project_status_logs_operator");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorId).HasConstraintName("fk_project_status_logs_operator").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_project_status_logs_project").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectDictionaryConfig : IEntityTypeConfiguration<ProjectDictionary>
{
    public void Configure(EntityTypeBuilder<ProjectDictionary> b)
    {
        b.ToTable("project_dictionaries");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasColumnName("type").HasMaxLength(32).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        b.Property(x => x.ParentId).HasColumnName("parent_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => new { x.Type, x.Name }).IsUnique().HasDatabaseName("uk_project_dictionaries_type_name");
        b.HasIndex(x => new { x.Type, x.Status, x.SortNo, x.Id }).HasDatabaseName("idx_project_dictionaries_type_status_sort");
        b.HasIndex(x => x.ParentId).HasDatabaseName("idx_project_dictionaries_parent");
        b.HasOne<ProjectDictionary>().WithMany().HasForeignKey(x => x.ParentId).HasConstraintName("fk_project_dictionaries_parent").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RobotPartConfig : IEntityTypeConfiguration<RobotPart>
{
    public void Configure(EntityTypeBuilder<RobotPart> b)
    {
        b.ToTable("robot_parts");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.SupplierId).HasColumnName("supplier_id");
        b.Property(x => x.PartNumber).HasColumnName("part_number").HasMaxLength(128).IsRequired();
        b.Property(x => x.Model).HasColumnName("model").HasMaxLength(512).IsRequired();
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => new { x.SupplierId, x.PartNumber }).IsUnique().HasDatabaseName("uk_robot_parts_supplier_part_number");
        b.HasIndex(x => new { x.SupplierId, x.Status, x.SortNo, x.Id }).HasDatabaseName("idx_robot_parts_supplier_status_sort");
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).HasConstraintName("fk_robot_parts_supplier").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectCopyConfig : IEntityTypeConfiguration<ProjectCopy>
{
    public void Configure(EntityTypeBuilder<ProjectCopy> b)
    {
        b.ToTable("project_copies");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceProjectId).HasColumnName("source_project_id");
        b.Property(x => x.TargetProjectId).HasColumnName("target_project_id");
        b.Property(x => x.SourceProjectName).HasColumnName("source_project_name").HasMaxLength(128).IsRequired();
        b.Property(x => x.TargetProjectName).HasColumnName("target_project_name").HasMaxLength(128).IsRequired();
        b.Property(x => x.CopiedBy).HasColumnName("copied_by");
        b.Property(x => x.CopiedByName).HasColumnName("copied_by_name").HasMaxLength(128).IsRequired();
        b.Property(x => x.FileCount).HasColumnName("file_count");
        b.Property(x => x.TotalBytes).HasColumnName("total_bytes");
        // No column default: the app always supplies an exact timestamp.
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime(6)");
        b.HasIndex(x => x.TargetProjectId).IsUnique().HasDatabaseName("uk_project_copies_target");
        b.HasIndex(x => new { x.SourceProjectId, x.CreatedAt, x.Id }).HasDatabaseName("idx_project_copies_source_time");
        b.HasIndex(x => x.CopiedBy).HasDatabaseName("idx_project_copies_copied_by");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CopiedBy).HasConstraintName("fk_project_copies_copied_by").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.SourceProjectId).HasConstraintName("fk_project_copies_source").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.TargetProjectId).HasConstraintName("fk_project_copies_target").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProjectActivityConfig : IEntityTypeConfiguration<ProjectActivity>
{
    public void Configure(EntityTypeBuilder<ProjectActivity> b)
    {
        b.ToTable("project_activities");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.ActivityType).HasColumnName("activity_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(32).IsRequired();
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(128).IsRequired();
        // No column default: the app always supplies an exact timestamp.
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("datetime(3)");
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(160).IsRequired();
        b.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(160);
        b.Property(x => x.TargetId).HasColumnName("target_id");
        b.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(191).IsRequired();
        b.HasIndex(x => x.SourceKey).IsUnique().HasDatabaseName("uk_project_activities_source");
        b.HasIndex(x => new { x.ProjectId, x.OccurredAt, x.Id }).HasDatabaseName("idx_project_activities_project_time");
        b.HasIndex(x => new { x.ProjectId, x.ActivityType, x.OccurredAt, x.Id }).HasDatabaseName("idx_project_activities_project_type_time");
        // Bounds the collaboration summary to the recent unread window (see UnreadWindow).
        b.HasIndex(x => x.OccurredAt).HasDatabaseName("idx_project_activities_occurred");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).HasConstraintName("fk_project_activities_project").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CollaborationReadConfig : IEntityTypeConfiguration<CollaborationRead>
{
    public void Configure(EntityTypeBuilder<CollaborationRead> b)
    {
        b.ToTable("collaboration_reads");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.ActivityId, x.UserId });
        b.Property(x => x.ActivityId).HasColumnName("activity_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.ReadAt).HasColumnName("read_at").HasColumnType("datetime(3)").HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
        b.HasIndex(x => new { x.UserId, x.ActivityId }).HasDatabaseName("idx_collaboration_reads_user_activity");
        b.HasOne<ProjectActivity>().WithMany().HasForeignKey(x => x.ActivityId).HasConstraintName("fk_collaboration_reads_activity").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_collaboration_reads_user").OnDelete(DeleteBehavior.Restrict);
    }
}
