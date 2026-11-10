using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outsource_default_cc",
                columns: table => new
                {
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_default_cc", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_outsource_default_cc_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_types",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_types", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_vendors",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remark = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_vendors", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_type_vendors",
                columns: table => new
                {
                    type_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_type_vendors", x => new { x.type_id, x.vendor_id });
                    table.ForeignKey(
                        name: "fk_outsource_type_vendors_type",
                        column: x => x.type_id,
                        principalTable: "outsource_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outsource_type_vendors_vendor",
                        column: x => x.vendor_id,
                        principalTable: "outsource_vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_vendor_contacts",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_vendor_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_vendor_contacts_vendor",
                        column: x => x.vendor_id,
                        principalTable: "outsource_vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_type_vendors_vendor",
                table: "outsource_type_vendors",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "uk_outsource_types_code",
                table: "outsource_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outsource_vendor_contacts_vendor_status",
                table: "outsource_vendor_contacts",
                columns: new[] { "vendor_id", "status", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_outsource_vendor_contacts_vendor_email",
                table: "outsource_vendor_contacts",
                columns: new[] { "vendor_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outsource_vendors_status_sort",
                table: "outsource_vendors",
                columns: new[] { "status", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_outsource_vendors_name",
                table: "outsource_vendors",
                column: "name",
                unique: true);

            // Upgrades insert by code and let MySQL allocate IDs. A fresh install has an empty permission table
            // here and receives the same codes from BootstrapSeedCatalog instead.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'outsource','外协清单','MENU',NULL,10
                FROM (SELECT 1) seed
                WHERE EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='outsource');
                """);
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT action.code, action.name, 'ACTION', parent.id, action.sort_no
                FROM permissions parent
                CROSS JOIN (
                    SELECT 'outsource:view' AS code, '查看外协清单' AS name, 66 AS sort_no
                    UNION ALL SELECT 'outsource:import', '汇入外协清单', 67
                    UNION ALL SELECT 'outsource:manage', '编辑与发送外协清单', 68
                    UNION ALL SELECT 'outsource:settings', '外协清单设定维护', 69
                ) action
                WHERE parent.code='outsource'
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=action.code);
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code IN ('outsource','outsource:view','outsource:import','outsource:manage','outsource:settings')
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keeps the outsource permission codes and grants: reference data survives a rollback (round-trip convention).
            migrationBuilder.DropTable(
                name: "outsource_default_cc");

            migrationBuilder.DropTable(
                name: "outsource_type_vendors");

            migrationBuilder.DropTable(
                name: "outsource_vendor_contacts");

            migrationBuilder.DropTable(
                name: "outsource_types");

            migrationBuilder.DropTable(
                name: "outsource_vendors");
        }
    }
}
