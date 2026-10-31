using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 存储路径改为页面配置（存储路径页面配置与在线迁移方案 2026-10-31）：storage_locations 记录三类存储的生效路径与在线
    /// 迁移进度，首次启动时由配置文件路径补齐；新增 storage:manage 权限点并授予内置系统管理员。
    /// </summary>
    public partial class AddStorageLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "storage_locations",
                columns: table => new
                {
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    path = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    previous_path = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    pending_path = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phase = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    total_files = table.Column<long>(type: "bigint", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    copied_files = table.Column<long>(type: "bigint", nullable: false),
                    copied_bytes = table.Column<long>(type: "bigint", nullable: false),
                    deleted_files = table.Column<long>(type: "bigint", nullable: false),
                    skipped_files = table.Column<long>(type: "bigint", nullable: false),
                    last_error = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    requested_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    started_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_locations", x => x.kind);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            // Insert by code so installations with custom permission IDs remain valid; a fresh install has no
            // system:config parent yet and receives the permission from BootstrapSeedCatalog instead.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'storage:manage','存储位置管理','ACTION',parent.id,65
                FROM permissions parent
                WHERE parent.code='system:config'
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='storage:manage');
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code='storage:manage'
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keeps the storage:manage permission and its grants: reference data survives a rollback (round-trip convention).
            migrationBuilder.DropTable(
                name: "storage_locations");
        }
    }
}
