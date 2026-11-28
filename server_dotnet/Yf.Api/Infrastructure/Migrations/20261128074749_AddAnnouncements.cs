using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "announcements",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    publisher_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    publisher_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    department_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    department_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    body = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    internal_scope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    external_scope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    published_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    pinned_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    withdrawn_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    withdrawn_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    withdrawn_by_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcements", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "announcement_companies",
                columns: table => new
                {
                    announcement_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    company_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    company_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_companies", x => new { x.announcement_id, x.company_id });
                    table.ForeignKey(
                        name: "fk_announcement_companies_announcement",
                        column: x => x.announcement_id,
                        principalTable: "announcements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "announcement_departments",
                columns: table => new
                {
                    announcement_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    department_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    department_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_departments", x => new { x.announcement_id, x.department_id });
                    table.ForeignKey(
                        name: "fk_announcement_departments_announcement",
                        column: x => x.announcement_id,
                        principalTable: "announcements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "announcement_reads",
                columns: table => new
                {
                    announcement_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    realm = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    account_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    account_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    display_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    first_read_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    last_read_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_reads", x => new { x.announcement_id, x.realm, x.account_id });
                    table.ForeignKey(
                        name: "fk_announcement_reads_announcement",
                        column: x => x.announcement_id,
                        principalTable: "announcements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_announcement_companies_company",
                table: "announcement_companies",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "idx_announcement_departments_department",
                table: "announcement_departments",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "idx_announcement_reads_last",
                table: "announcement_reads",
                columns: new[] { "announcement_id", "last_read_at" });

            migrationBuilder.CreateIndex(
                name: "idx_announcements_creator_status",
                table: "announcements",
                columns: new[] { "created_by", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_announcements_department_status",
                table: "announcements",
                columns: new[] { "department_id", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_announcements_reader",
                table: "announcements",
                columns: new[] { "status", "pinned_at", "published_at", "id" });

            // The bootstrap catalog seeds new installations after migrations. Resolve IDs by code
            // on existing installations so locally created permissions retain their IDs and grants.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'announcement','公告管理','MENU',NULL,11
                FROM permissions WHERE code='dashboard'
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE code='announcement');
                """);
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT item.code,item.name,'ACTION',parent.id,item.sort_no FROM
                    (SELECT 'announcement:publish' code,'公告对外发布' name,82 sort_no
                     UNION ALL SELECT 'announcement:withdraw','公告下架',83
                     UNION ALL SELECT 'announcement:pin','公告置顶管理',84
                     UNION ALL SELECT 'announcement:reads','查看公告阅读明细',85) item
                JOIN permissions parent ON parent.code='announcement'
                WHERE NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=item.code);
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r CROSS JOIN permissions p
                WHERE r.is_built_in=1 AND r.name='系统管理员'
                  AND p.code IN ('announcement','announcement:publish','announcement:withdraw','announcement:pin','announcement:reads');
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_data_scopes(role_id,`domain`,`scope`)
                SELECT id,'announcement','ALL' FROM roles WHERE is_built_in=1 AND name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Retain the permission catalog, customized grants and management scopes across a schema rollback.
            migrationBuilder.DropTable(
                name: "announcement_companies");

            migrationBuilder.DropTable(
                name: "announcement_departments");

            migrationBuilder.DropTable(
                name: "announcement_reads");

            migrationBuilder.DropTable(
                name: "announcements");
        }
    }
}
