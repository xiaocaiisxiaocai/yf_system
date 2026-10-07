using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Modules.Oem.Data;

// Fluent mappings for the OEM tables. References to users/departments are kept
// FK-free on purpose (the same convention the audit/outbox tables use): OEM
// history must survive account and organisation maintenance, and eligibility is
// always re-validated by the application at the moment it matters.

internal static class OemMapping
{
    // UUID-valued columns are VARCHAR(36), not CHAR(36): Pomelo reserves CHAR(36) for
    // its Guid storage format, which cannot map a string property.
    internal const string Collation = "utf8mb4_unicode_ci";
    internal const string Millis = "datetime(3)";

    internal static EntityTypeBuilder<T> Table<T>(this EntityTypeBuilder<T> b, string name) where T : class
    {
        b.ToTable(name);
        b.UseCollation(Collation);
        return b;
    }

    internal static PropertyBuilder<string> Text(this PropertyBuilder<string> p, string column, int max, bool required = true)
    {
        p.HasColumnName(column).HasMaxLength(max);
        return required ? p.IsRequired() : p;
    }

    internal static PropertyBuilder<string?> OptionalText(this PropertyBuilder<string?> p, string column, int max) =>
        p.HasColumnName(column).HasMaxLength(max);

    internal static PropertyBuilder<DateTime> Clock(this PropertyBuilder<DateTime> p, string column) =>
        p.HasColumnName(column).HasColumnType(Millis);

    internal static PropertyBuilder<DateTime?> OptionalClock(this PropertyBuilder<DateTime?> p, string column) =>
        p.HasColumnName(column).HasColumnType(Millis);

    internal static PropertyBuilder<ulong> Version(this PropertyBuilder<ulong> p) =>
        p.HasColumnName("concurrency_version").HasDefaultValue(0UL).IsConcurrencyToken();
}

public sealed class OemCompanyConfig : IEntityTypeConfiguration<OemCompany>
{
    public void Configure(EntityTypeBuilder<OemCompany> b)
    {
        b.Table("oem_companies").HasKey(x => x.Id);
        b.Property(x => x.Name).Text("name", 128);
        b.Property(x => x.ContactName).OptionalText("contact_name", 64);
        b.Property(x => x.ContactPhone).OptionalText("contact_phone", 32);
        b.Property(x => x.ContactEmail).OptionalText("contact_email", 128);
        b.Property(x => x.Remark).OptionalText("remark", 512);
        b.Property(x => x.Status).Text("status", 16).HasDefaultValue("ACTIVE");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_oem_companies_name");
    }
}

public sealed class OemAccountConfig : IEntityTypeConfiguration<OemAccount>
{
    public void Configure(EntityTypeBuilder<OemAccount> b)
    {
        b.Table("oem_accounts").HasKey(x => x.Id);
        b.Property(x => x.EmployeeNo).Text("employee_no", 64);
        b.Property(x => x.PasswordHash).Text("password_hash", 255);
        b.Property(x => x.RealName).Text("real_name", 64);
        b.Property(x => x.Email).Text("email", 128);
        b.Property(x => x.OemCompanyId).HasColumnName("oem_company_id");
        b.Property(x => x.Status).Text("status", 16).HasDefaultValue("ACTIVE");
        b.Property(x => x.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(true);
        b.Property(x => x.FailedLoginAttempts).HasColumnName("failed_login_attempts").HasDefaultValue(0);
        b.Property(x => x.LockedUntil).OptionalClock("locked_until");
        b.Property(x => x.LastLoginAt).OptionalClock("last_login_at");
        b.Property(x => x.LastLoginIp).OptionalText("last_login_ip", 64);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.EmployeeNo).IsUnique().HasDatabaseName("uk_oem_accounts_employee_no");
        b.HasIndex(x => x.OemCompanyId).HasDatabaseName("idx_oem_accounts_company");
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.OemCompanyId)
            .HasConstraintName("fk_oem_accounts_company").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemRefreshTokenConfig : IEntityTypeConfiguration<OemRefreshToken>
{
    public void Configure(EntityTypeBuilder<OemRefreshToken> b)
    {
        b.Table("oem_refresh_tokens").HasKey(x => x.Id);
        b.Property(x => x.AccountId).HasColumnName("account_id");
        b.Property(x => x.SessionId).Text("session_id", 64);
        b.Property(x => x.TokenHash).Text("token_hash", 64).IsFixedLength();
        b.Property(x => x.SessionExpiresAt).Clock("session_expires_at");
        b.Property(x => x.ExpiresAt).Clock("expires_at");
        b.Property(x => x.Revoked).HasColumnName("revoked").HasDefaultValue(false);
        b.Property(x => x.RevokeReason).OptionalText("revoke_reason", 32);
        b.Property(x => x.Ip).OptionalText("ip", 64);
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("uk_oem_refresh_tokens_hash");
        b.HasIndex(x => x.AccountId).HasDatabaseName("idx_oem_refresh_tokens_account");
        b.HasIndex(x => x.SessionId).HasDatabaseName("idx_oem_refresh_tokens_session");
        b.HasIndex(x => x.SessionExpiresAt).HasDatabaseName("idx_oem_refresh_tokens_session_expires");
        b.HasOne<OemAccount>().WithMany().HasForeignKey(x => x.AccountId)
            .HasConstraintName("fk_oem_refresh_tokens_account").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OemRetentionTemplateConfig : IEntityTypeConfiguration<OemRetentionTemplate>
{
    public void Configure(EntityTypeBuilder<OemRetentionTemplate> b)
    {
        b.Table("oem_retention_templates").HasKey(x => x.Id);
        b.Property(x => x.Name).Text("name", 64);
        b.Property(x => x.Mode).Text("mode", 32);
        b.Property(x => x.ReleaseTtlMinutes).HasColumnName("release_ttl_minutes");
        b.Property(x => x.ReceiptGraceMinutes).HasColumnName("receipt_grace_minutes");
        b.Property(x => x.Status).Text("status", 16).HasDefaultValue("ACTIVE");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_oem_retention_templates_name");
    }
}

public sealed class OemTransferConfig : IEntityTypeConfiguration<OemTransfer>
{
    public void Configure(EntityTypeBuilder<OemTransfer> b)
    {
        b.Table("oem_transfers").HasKey(x => x.Id);
        b.Property(x => x.Direction).Text("direction", 24);
        b.Property(x => x.OemCompanyId).HasColumnName("oem_company_id");
        b.Property(x => x.Title).Text("title", 128);
        b.Property(x => x.Description).OptionalText("description", 1024);
        b.Property(x => x.InternalSenderUserId).HasColumnName("internal_sender_user_id");
        b.Property(x => x.OemSenderAccountId).HasColumnName("oem_sender_account_id");
        b.Property(x => x.LifecycleStatus).Text("lifecycle_status", 24);
        b.Property(x => x.RetentionTemplateId).HasColumnName("retention_template_id");
        b.Property(x => x.RetentionMode).OptionalText("retention_mode", 32);
        b.Property(x => x.ReleaseTtlMinutes).HasColumnName("release_ttl_minutes");
        b.Property(x => x.ReceiptGraceMinutes).HasColumnName("receipt_grace_minutes");
        b.Property(x => x.ManifestSha256).OptionalText("manifest_sha256", 64).IsFixedLength();
        b.Property(x => x.SentAt).OptionalClock("sent_at");
        b.Property(x => x.ReleasedAt).OptionalClock("released_at");
        b.Property(x => x.ExpiresAt).OptionalClock("expires_at");
        b.Property(x => x.ClosedReason).OptionalText("closed_reason", 512);
        b.Property(x => x.ClosedByRealm).OptionalText("closed_by_realm", 16);
        b.Property(x => x.ClosedById).HasColumnName("closed_by_id");
        b.Property(x => x.ClosedAt).OptionalClock("closed_at");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.HasIndex(x => new { x.OemCompanyId, x.LifecycleStatus }).HasDatabaseName("idx_oem_transfers_company_status");
        b.HasIndex(x => x.InternalSenderUserId).HasDatabaseName("idx_oem_transfers_internal_sender");
        b.HasIndex(x => x.OemSenderAccountId).HasDatabaseName("idx_oem_transfers_oem_sender");
        b.HasIndex(x => new { x.LifecycleStatus, x.ReleasedAt }).HasDatabaseName("idx_oem_transfers_status_released");
        b.HasIndex(x => new { x.ClosedByRealm, x.ClosedById }).HasDatabaseName("idx_oem_transfers_closed_by");
        b.HasOne<OemCompany>().WithMany().HasForeignKey(x => x.OemCompanyId)
            .HasConstraintName("fk_oem_transfers_company").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OemAccount>().WithMany().HasForeignKey(x => x.OemSenderAccountId)
            .HasConstraintName("fk_oem_transfers_oem_sender").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OemRetentionTemplate>().WithMany().HasForeignKey(x => x.RetentionTemplateId)
            .HasConstraintName("fk_oem_transfers_retention").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemTransferFileConfig : IEntityTypeConfiguration<OemTransferFile>
{
    public void Configure(EntityTypeBuilder<OemTransferFile> b)
    {
        b.Table("oem_transfer_files").HasKey(x => x.Id);
        b.Property(x => x.TransferId).HasColumnName("transfer_id");
        b.Property(x => x.UploadedByInternalUserId).HasColumnName("uploaded_by_internal_user_id");
        b.Property(x => x.UploadedByOemAccountId).HasColumnName("uploaded_by_oem_account_id");
        b.Property(x => x.OriginalName).Text("original_name", 255);
        b.Property(x => x.StoredName).Text("stored_name", 36);
        b.Property(x => x.Ext).Text("ext", 16);
        b.Property(x => x.MimeType).OptionalText("mime_type", 128);
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.Md5).OptionalText("md5", 32).IsFixedLength();
        b.Property(x => x.Sha256).Text("sha256", 64).IsFixedLength();
        b.Property(x => x.StoragePath).Text("storage_path", 512);
        b.Property(x => x.PayloadStatus).Text("payload_status", 24);
        b.Property(x => x.ScanStatus).Text("scan_status", 16);
        b.Property(x => x.FirstRecipientDownloadAt).OptionalClock("first_recipient_download_at");
        b.Property(x => x.FirstRecipientRealm).OptionalText("first_recipient_realm", 16);
        b.Property(x => x.FirstRecipientId).HasColumnName("first_recipient_id");
        b.Property(x => x.PurgeDueAt).OptionalClock("purge_due_at");
        b.Property(x => x.PurgeReason).OptionalText("purge_reason", 32);
        b.Property(x => x.PurgeLeaseOwner).OptionalText("purge_lease_owner", 64);
        b.Property(x => x.PurgeLeaseUntil).OptionalClock("purge_lease_until");
        b.Property(x => x.PurgeAttemptCount).HasColumnName("purge_attempt_count").HasDefaultValue(0);
        b.Property(x => x.PurgeNextAttemptAt).OptionalClock("purge_next_attempt_at");
        b.Property(x => x.PurgeLastError).OptionalText("purge_last_error", 1024);
        b.Property(x => x.PurgedAt).OptionalClock("purged_at");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.StoredName).IsUnique().HasDatabaseName("uk_oem_transfer_files_stored_name");
        b.HasIndex(x => x.TransferId).HasDatabaseName("idx_oem_transfer_files_transfer");
        // Account-deletion history probes run under the exclusive management lock and must not scan.
        b.HasIndex(x => x.UploadedByOemAccountId).HasDatabaseName("idx_oem_transfer_files_oem_uploader");
        b.HasIndex(x => x.UploadedByInternalUserId).HasDatabaseName("idx_oem_transfer_files_internal_uploader");
        b.HasIndex(x => new { x.FirstRecipientRealm, x.FirstRecipientId }).HasDatabaseName("idx_oem_transfer_files_first_recipient");
        b.HasIndex(x => new { x.PayloadStatus, x.PurgeDueAt }).HasDatabaseName("idx_oem_transfer_files_purge_due");
        b.HasIndex(x => new { x.PayloadStatus, x.PurgeNextAttemptAt }).HasDatabaseName("idx_oem_transfer_files_purge_retry");
        b.HasOne<OemTransfer>().WithMany().HasForeignKey(x => x.TransferId)
            .HasConstraintName("fk_oem_transfer_files_transfer").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemUploadSessionConfig : IEntityTypeConfiguration<OemUploadSession>
{
    public void Configure(EntityTypeBuilder<OemUploadSession> b)
    {
        b.Table("oem_upload_sessions").HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
        b.Property(x => x.TransferId).HasColumnName("transfer_id");
        b.Property(x => x.UploaderRealm).Text("uploader_realm", 16);
        b.Property(x => x.UploaderId).HasColumnName("uploader_id");
        b.Property(x => x.FileName).Text("file_name", 255);
        b.Property(x => x.FileSize).HasColumnName("file_size");
        b.Property(x => x.FileMd5).OptionalText("file_md5", 32).IsFixedLength();
        b.Property(x => x.ChunkSize).HasColumnName("chunk_size");
        b.Property(x => x.TotalChunks).HasColumnName("total_chunks");
        b.Property(x => x.TempDir).Text("temp_dir", 512);
        b.Property(x => x.ReservedBytes).HasColumnName("reserved_bytes");
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.ResultFileId).HasColumnName("result_file_id");
        b.Property(x => x.ExpiresAt).Clock("expires_at");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => new { x.TransferId, x.Status }).HasDatabaseName("idx_oem_upload_sessions_transfer");
        b.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("idx_oem_upload_sessions_expiry");
        b.HasIndex(x => new { x.UploaderRealm, x.UploaderId }).HasDatabaseName("idx_oem_upload_sessions_uploader");
        b.HasOne<OemTransfer>().WithMany().HasForeignKey(x => x.TransferId)
            .HasConstraintName("fk_oem_upload_sessions_transfer").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemFileScanJobConfig : IEntityTypeConfiguration<OemFileScanJob>
{
    public void Configure(EntityTypeBuilder<OemFileScanJob> b)
    {
        b.Table("oem_file_scan_jobs").HasKey(x => x.Id);
        b.Property(x => x.FileId).HasColumnName("file_id");
        b.Property(x => x.FileSha256).Text("file_sha256", 64).IsFixedLength();
        b.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).OptionalClock("next_attempt_at");
        b.Property(x => x.LeaseOwner).OptionalText("lease_owner", 64);
        b.Property(x => x.LeaseUntil).OptionalClock("lease_until");
        b.Property(x => x.EngineName).OptionalText("engine_name", 64);
        b.Property(x => x.EngineVersion).OptionalText("engine_version", 64);
        b.Property(x => x.SignatureVersion).OptionalText("signature_version", 128);
        b.Property(x => x.ThreatName).OptionalText("threat_name", 255);
        b.Property(x => x.LastError).OptionalText("last_error", 1024);
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.StartedAt).OptionalClock("started_at");
        b.Property(x => x.CompletedAt).OptionalClock("completed_at");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_oem_scan_jobs_status_next");
        b.HasIndex(x => x.FileId).HasDatabaseName("idx_oem_scan_jobs_file");
        b.HasOne<OemTransferFile>().WithMany().HasForeignKey(x => x.FileId)
            .HasConstraintName("fk_oem_scan_jobs_file").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemFilePromotionConfig : IEntityTypeConfiguration<OemFilePromotion>
{
    public void Configure(EntityTypeBuilder<OemFilePromotion> b)
    {
        b.Table("oem_file_promotions").HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
        b.Property(x => x.FileId).HasColumnName("file_id");
        b.Property(x => x.FileSha256).Text("file_sha256", 64).IsFixedLength();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.SourcePath).Text("source_path", 512);
        b.Property(x => x.TargetPath).Text("target_path", 512);
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.LeaseOwner).OptionalText("lease_owner", 64);
        b.Property(x => x.LeaseUntil).OptionalClock("lease_until");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).OptionalClock("next_attempt_at");
        b.Property(x => x.LastError).OptionalText("last_error", 1024);
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.CompletedAt).OptionalClock("completed_at");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_oem_promotions_status_next");
        b.HasIndex(x => x.FileId).HasDatabaseName("idx_oem_promotions_file");
        b.HasOne<OemTransferFile>().WithMany().HasForeignKey(x => x.FileId)
            .HasConstraintName("fk_oem_promotions_file").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemFlowTemplateConfig : IEntityTypeConfiguration<OemFlowTemplate>
{
    public void Configure(EntityTypeBuilder<OemFlowTemplate> b)
    {
        b.Table("oem_flow_templates").HasKey(x => x.Id);
        b.Property(x => x.Name).Text("name", 64);
        b.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false);
        b.Property(x => x.Status).Text("status", 16).HasDefaultValue("ACTIVE");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_oem_flow_templates_name");
        // Default-template promotion locks the current defaults through this index.
        b.HasIndex(x => x.IsDefault).HasDatabaseName("idx_oem_flow_templates_default");
    }
}

public sealed class OemFlowTemplateScopeConfig : IEntityTypeConfiguration<OemFlowTemplateScope>
{
    public void Configure(EntityTypeBuilder<OemFlowTemplateScope> b)
    {
        b.Table("oem_flow_template_scopes").HasKey(x => x.Id);
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.HasIndex(x => x.DepartmentId).IsUnique().HasDatabaseName("uk_oem_flow_scopes_department");
        b.HasIndex(x => x.TemplateId).HasDatabaseName("idx_oem_flow_scopes_template");
        b.HasOne<OemFlowTemplate>().WithMany().HasForeignKey(x => x.TemplateId)
            .HasConstraintName("fk_oem_flow_scopes_template").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OemFlowTemplateNodeConfig : IEntityTypeConfiguration<OemFlowTemplateNode>
{
    public void Configure(EntityTypeBuilder<OemFlowTemplateNode> b)
    {
        b.Table("oem_flow_template_nodes").HasKey(x => x.Id);
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no");
        b.Property(x => x.Name).Text("name", 64);
        b.Property(x => x.ApproverSource).Text("approver_source", 32);
        b.Property(x => x.ApprovalMode).Text("approval_mode", 16);
        b.Property(x => x.SelfPolicy).Text("self_policy", 16);
        b.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        b.HasIndex(x => new { x.TemplateId, x.SortNo }).IsUnique().HasDatabaseName("uk_oem_flow_nodes_template_sort");
        b.HasOne<OemFlowTemplate>().WithMany().HasForeignKey(x => x.TemplateId)
            .HasConstraintName("fk_oem_flow_nodes_template").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OemFlowTemplateNodeUserConfig : IEntityTypeConfiguration<OemFlowTemplateNodeUser>
{
    public void Configure(EntityTypeBuilder<OemFlowTemplateNodeUser> b)
    {
        b.Table("oem_flow_template_node_users").HasKey(x => x.Id);
        b.Property(x => x.NodeId).HasColumnName("node_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Role).Text("role", 16);
        b.HasIndex(x => new { x.NodeId, x.Role, x.UserId }).IsUnique().HasDatabaseName("uk_oem_flow_node_users");
        b.HasOne<OemFlowTemplateNode>().WithMany().HasForeignKey(x => x.NodeId)
            .HasConstraintName("fk_oem_flow_node_users_node").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OemFlowInstanceConfig : IEntityTypeConfiguration<OemFlowInstance>
{
    public void Configure(EntityTypeBuilder<OemFlowInstance> b)
    {
        b.Table("oem_flow_instances").HasKey(x => x.Id);
        b.Property(x => x.TransferId).HasColumnName("transfer_id");
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.Property(x => x.InitiatorUserId).HasColumnName("initiator_user_id");
        b.Property(x => x.InitiatorSectionId).HasColumnName("initiator_section_id");
        b.Property(x => x.Status).Text("status", 24);
        b.Property(x => x.TemplateSnapshot).HasColumnName("template_snapshot").HasColumnType("json").IsRequired();
        b.Property(x => x.BlockedReason).OptionalText("blocked_reason", 128);
        b.Property(x => x.CurrentSortNo).HasColumnName("current_sort_no");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.UpdatedAt).Clock("updated_at");
        b.HasIndex(x => x.TransferId).IsUnique().HasDatabaseName("uk_oem_flow_instances_transfer");
        b.HasIndex(x => x.Status).HasDatabaseName("idx_oem_flow_instances_status");
        b.HasOne<OemTransfer>().WithMany().HasForeignKey(x => x.TransferId)
            .HasConstraintName("fk_oem_flow_instances_transfer").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OemFlowTemplate>().WithMany().HasForeignKey(x => x.TemplateId)
            .HasConstraintName("fk_oem_flow_instances_template").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemFlowInstanceNodeConfig : IEntityTypeConfiguration<OemFlowInstanceNode>
{
    public void Configure(EntityTypeBuilder<OemFlowInstanceNode> b)
    {
        b.Table("oem_flow_instance_nodes").HasKey(x => x.Id);
        b.Property(x => x.InstanceId).HasColumnName("instance_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no");
        b.Property(x => x.Name).Text("name", 64);
        b.Property(x => x.ApproverSource).Text("approver_source", 32);
        b.Property(x => x.ApprovalMode).Text("approval_mode", 16);
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.SkipReason).OptionalText("skip_reason", 32);
        b.Property(x => x.UsedFallback).HasColumnName("used_fallback").HasDefaultValue(false);
        b.Property(x => x.CompletedAt).OptionalClock("completed_at");
        b.HasIndex(x => new { x.InstanceId, x.SortNo }).IsUnique().HasDatabaseName("uk_oem_flow_instance_nodes_sort");
        b.HasOne<OemFlowInstance>().WithMany().HasForeignKey(x => x.InstanceId)
            .HasConstraintName("fk_oem_flow_instance_nodes_instance").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemFlowTaskConfig : IEntityTypeConfiguration<OemFlowTask>
{
    public void Configure(EntityTypeBuilder<OemFlowTask> b)
    {
        b.Table("oem_flow_tasks").HasKey(x => x.Id);
        b.Property(x => x.InstanceId).HasColumnName("instance_id");
        b.Property(x => x.InstanceNodeId).HasColumnName("instance_node_id");
        b.Property(x => x.ApproverUserId).HasColumnName("approver_user_id");
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.Reason).OptionalText("reason", 1024);
        b.Property(x => x.ReplacesTaskId).HasColumnName("replaces_task_id");
        b.Property(x => x.ReassignReason).OptionalText("reassign_reason", 512);
        b.Property(x => x.ReassignedBy).HasColumnName("reassigned_by");
        b.Property(x => x.DecidedAt).OptionalClock("decided_at");
        b.Property(x => x.ActivatedAt).OptionalClock("activated_at");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.HasIndex(x => x.InstanceNodeId).HasDatabaseName("idx_oem_flow_tasks_node");
        b.HasIndex(x => new { x.ApproverUserId, x.Status }).HasDatabaseName("idx_oem_flow_tasks_approver");
        b.HasIndex(x => x.InstanceId).HasDatabaseName("idx_oem_flow_tasks_instance");
        b.HasOne<OemFlowInstance>().WithMany().HasForeignKey(x => x.InstanceId)
            .HasConstraintName("fk_oem_flow_tasks_instance").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OemFlowInstanceNode>().WithMany().HasForeignKey(x => x.InstanceNodeId)
            .HasConstraintName("fk_oem_flow_tasks_node").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemDownloadSessionConfig : IEntityTypeConfiguration<OemDownloadSession>
{
    public void Configure(EntityTypeBuilder<OemDownloadSession> b)
    {
        b.Table("oem_download_sessions").HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
        b.Property(x => x.FileId).HasColumnName("file_id");
        b.Property(x => x.FileStoredName).Text("file_stored_name", 36);
        b.Property(x => x.FileSha256).Text("file_sha256", 64).IsFixedLength();
        b.Property(x => x.ActorRealm).Text("actor_realm", 16);
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.LoginSessionId).Text("login_session_id", 64);
        b.Property(x => x.Purpose).Text("purpose", 16);
        b.Property(x => x.RecipientSide).HasColumnName("recipient_side");
        b.Property(x => x.ExpectedSize).HasColumnName("expected_size");
        b.Property(x => x.Status).Text("status", 16);
        b.Property(x => x.LastProgressAt).OptionalClock("last_progress_at");
        b.Property(x => x.AbsoluteDeadline).Clock("absolute_deadline");
        b.Property(x => x.ConcurrencyVersion).Version();
        b.Property(x => x.CreatedAt).Clock("created_at");
        b.Property(x => x.CompletedAt).OptionalClock("completed_at");
        b.HasIndex(x => new { x.FileId, x.Status }).HasDatabaseName("idx_oem_download_sessions_file");
        b.HasIndex(x => new { x.ActorRealm, x.ActorId }).HasDatabaseName("idx_oem_download_sessions_actor");
        b.HasOne<OemTransferFile>().WithMany().HasForeignKey(x => x.FileId)
            .HasConstraintName("fk_oem_download_sessions_file").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OemDownloadRangeConfig : IEntityTypeConfiguration<OemDownloadRange>
{
    public void Configure(EntityTypeBuilder<OemDownloadRange> b)
    {
        b.Table("oem_download_ranges").HasKey(x => x.Id);
        b.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.StartOffset).HasColumnName("start_offset");
        b.Property(x => x.EndOffset).HasColumnName("end_offset");
        b.HasIndex(x => new { x.SessionId, x.StartOffset }).HasDatabaseName("idx_oem_download_ranges_session");
        b.HasOne<OemDownloadSession>().WithMany().HasForeignKey(x => x.SessionId)
            .HasConstraintName("fk_oem_download_ranges_session").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OemDownloadLeaseConfig : IEntityTypeConfiguration<OemDownloadLease>
{
    public void Configure(EntityTypeBuilder<OemDownloadLease> b)
    {
        b.Table("oem_download_leases").HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
        b.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.FileId).HasColumnName("file_id");
        b.Property(x => x.Owner).Text("owner", 64);
        b.Property(x => x.StartedAt).Clock("started_at");
        b.Property(x => x.LastProgressAt).OptionalClock("last_progress_at");
        b.Property(x => x.LeaseUntil).Clock("lease_until");
        b.Property(x => x.HardDeadline).Clock("hard_deadline");
        b.Property(x => x.Status).Text("status", 16);
        b.HasIndex(x => new { x.FileId, x.Status }).HasDatabaseName("idx_oem_download_leases_file");
        b.HasOne<OemDownloadSession>().WithMany().HasForeignKey(x => x.SessionId)
            .HasConstraintName("fk_oem_download_leases_session").OnDelete(DeleteBehavior.Cascade);
    }
}
