using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

public sealed class Department
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public ulong? ParentId { get; set; }
    public int SortNo { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Kind { get; set; } = null!;
    // business_parent_scope is a generated column (STORED, GENERATED ALWAYS AS ifnull(parent_id,0));
    // not settable, only used by the uk_departments_parent_kind_name unique index.
    public ulong BusinessParentScope { get; set; }
}

public sealed class Supplier
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Remark { get; set; }
    public string Status { get; set; } = null!;
    public ulong? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Role
{
    public ulong Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsBuiltIn { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Permission
{
    public ulong Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public ulong? ParentId { get; set; }
    public int SortNo { get; set; }
}

public sealed class RolePermission
{
    public ulong RoleId { get; set; }
    public ulong PermissionId { get; set; }
}

public sealed class UserRole
{
    public ulong UserId { get; set; }
    public ulong RoleId { get; set; }
}

public sealed class DepartmentConfig : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.ParentId).HasColumnName("parent_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("ACTIVE");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired().HasDefaultValue("DIVISION");
        // Generated column backing the uk_departments_parent_kind_name unique index; not written by application code.
        b.Property(x => x.BusinessParentScope).HasColumnName("business_parent_scope")
            .HasComputedColumnSql("ifnull(`parent_id`,0)", stored: true).ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => new { x.BusinessParentScope, x.Kind, x.Name }).IsUnique().HasDatabaseName("uk_departments_parent_kind_name");
        b.HasIndex(x => x.ParentId).HasDatabaseName("idx_departments_parent");
    }
}

public sealed class SupplierConfig : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("suppliers");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        b.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(512);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("ACTIVE");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_suppliers_name");
    }
}

public sealed class RoleConfig : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(255);
        b.Property(x => x.IsBuiltIn).HasColumnName("is_built_in").HasDefaultValue(false);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired().HasDefaultValue("ACTIVE");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_roles_name");
    }
}

public sealed class PermissionConfig : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(128).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Type).HasColumnName("type").HasMaxLength(16).IsRequired();
        b.Property(x => x.ParentId).HasColumnName("parent_id");
        b.Property(x => x.SortNo).HasColumnName("sort_no").HasDefaultValue(0);
        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("code");
        b.HasIndex(x => x.ParentId).HasDatabaseName("idx_permissions_parent");
    }
}

public sealed class RolePermissionConfig : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.RoleId, x.PermissionId });
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.Property(x => x.PermissionId).HasColumnName("permission_id");
        b.HasIndex(x => x.PermissionId).HasDatabaseName("fk_rp_perm");
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).HasConstraintName("fk_rp_role").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).HasConstraintName("fk_rp_perm").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserRoleConfig : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => new { x.UserId, x.RoleId });
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.RoleId).HasColumnName("role_id");
        // uk_user_roles_user_id enforces "at most one role per user" (single active internal role).
        b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("uk_user_roles_user_id");
        b.HasIndex(x => x.RoleId).HasDatabaseName("fk_ur_role");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_ur_user").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).HasConstraintName("fk_ur_role").OnDelete(DeleteBehavior.Restrict);
    }
}
