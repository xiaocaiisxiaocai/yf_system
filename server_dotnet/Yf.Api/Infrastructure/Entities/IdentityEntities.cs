using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yf.Api.Infrastructure;

namespace Yf.Api.Infrastructure.Entities;

public sealed class User
{
    public ulong Id { get; set; }
    public string EmployeeNo { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string RealName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string UserType { get; set; } = null!;
    public ulong? SupplierId { get; set; }
    public ulong? DepartmentId { get; set; }
    public string Status { get; set; } = null!;
    public bool MustChangePassword { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RefreshToken
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }
    public string SessionId { get; set; } = null!;
    public string TokenHash { get; set; } = null!;
    public DateTime SessionCreatedAt { get; set; }
    public DateTime SessionExpiresAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.EmployeeNo).HasColumnName("employee_no").HasMaxLength(64).IsRequired();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
        b.Property(x => x.RealName).HasColumnName("real_name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(128).IsRequired();
        b.Property(x => x.UserType).HasColumnName("user_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.SupplierId).HasColumnName("supplier_id");
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue(AccountStatuses.Active);
        b.Property(x => x.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(true);
        b.Property(x => x.FailedLoginAttempts).HasColumnName("failed_login_attempts").HasDefaultValue(0);
        b.Property(x => x.LockedUntil).HasColumnName("locked_until").HasColumnType("datetime");
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at").HasColumnType("datetime");
        b.Property(x => x.LastLoginIp).HasColumnName("last_login_ip").HasMaxLength(64);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.EmployeeNo).IsUnique().HasDatabaseName("uk_users_employee_no");
        b.HasIndex(x => x.SupplierId).HasDatabaseName("idx_users_supplier");
        b.HasIndex(x => x.DepartmentId).HasDatabaseName("idx_users_department");
        b.HasIndex(x => x.CreatedBy).HasDatabaseName("idx_users_created_by");
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).HasConstraintName("fk_users_supplier").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).HasConstraintName("fk_users_department").OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).HasConstraintName("fk_users_created_by").OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.SessionCreatedAt).HasColumnName("session_created_at").HasColumnType("datetime");
        b.Property(x => x.SessionExpiresAt).HasColumnName("session_expires_at").HasColumnType("datetime");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("datetime");
        b.Property(x => x.Revoked).HasColumnName("revoked").HasDefaultValue(false);
        b.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("token_hash");
        b.HasIndex(x => x.UserId).HasDatabaseName("fk_rt_user");
        b.HasIndex(x => new { x.SessionId, x.UserId, x.Revoked, x.ExpiresAt }).HasDatabaseName("idx_refresh_tokens_session_state");
        // Supports ordinary active-token lookups and operational expiry inspection.
        b.HasIndex(x => x.ExpiresAt).HasDatabaseName("idx_refresh_tokens_expires");
        // SessionCleanupService retains rotated hashes until the whole family reaches its absolute deadline.
        b.HasIndex(x => x.SessionExpiresAt).HasDatabaseName("idx_refresh_tokens_session_expires");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_rt_user").OnDelete(DeleteBehavior.Restrict);
    }
}
