using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outsource_contact_columns",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    col_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    title = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    person_display = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "COMBINED", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone_title = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    auto_merge = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    vendor_visible = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    is_group_column = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    width_hint = table.Column<double>(type: "double", nullable: false, defaultValue: 18.0),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_columns", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_contact_people",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    scope_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    title_hint = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_ocp_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ocp_vendor",
                        column: x => x.vendor_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_contact_rows",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    visibility = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, defaultValue: "INTERNAL_ONLY", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<uint>(type: "int unsigned", nullable: false, defaultValue: 1u),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    updated_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_rows", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_contact_row_people",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    row_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    column_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    person_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    prefix_label = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_row_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_ocrp_column",
                        column: x => x.column_id,
                        principalTable: "outsource_contact_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ocrp_person",
                        column: x => x.person_id,
                        principalTable: "outsource_contact_people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ocrp_row",
                        column: x => x.row_id,
                        principalTable: "outsource_contact_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_contact_row_values",
                columns: table => new
                {
                    row_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    column_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    text_value = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_row_values", x => new { x.row_id, x.column_id });
                    table.ForeignKey(
                        name: "fk_ocrval_column",
                        column: x => x.column_id,
                        principalTable: "outsource_contact_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ocrval_row",
                        column: x => x.row_id,
                        principalTable: "outsource_contact_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_contact_row_vendors",
                columns: table => new
                {
                    row_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_contact_row_vendors", x => new { x.row_id, x.vendor_id });
                    table.ForeignKey(
                        name: "fk_ocrv_row",
                        column: x => x.row_id,
                        principalTable: "outsource_contact_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ocrv_vendor",
                        column: x => x.vendor_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_occ_sort",
                table: "outsource_contact_columns",
                columns: new[] { "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_occ_col_key",
                table: "outsource_contact_columns",
                column: "col_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_ocp_phone",
                table: "outsource_contact_people",
                column: "phone");

            migrationBuilder.CreateIndex(
                name: "idx_ocp_scope_vendor_status",
                table: "outsource_contact_people",
                columns: new[] { "scope_type", "vendor_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_outsource_contact_people_vendor_id",
                table: "outsource_contact_people",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "uk_ocp_user_id",
                table: "outsource_contact_people",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_ocrp_column",
                table: "outsource_contact_row_people",
                column: "column_id");

            migrationBuilder.CreateIndex(
                name: "idx_ocrp_person",
                table: "outsource_contact_row_people",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "uk_ocrp_row_col_person",
                table: "outsource_contact_row_people",
                columns: new[] { "row_id", "column_id", "person_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_ocrval_column",
                table: "outsource_contact_row_values",
                column: "column_id");

            migrationBuilder.CreateIndex(
                name: "idx_ocrv_vendor",
                table: "outsource_contact_row_vendors",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "idx_ocr_sort",
                table: "outsource_contact_rows",
                columns: new[] { "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_ocr_visibility",
                table: "outsource_contact_rows",
                column: "visibility");

            // The bootstrap catalog seeds new installations after migrations. Resolve IDs by code
            // on existing installations so locally created permissions retain their IDs and grants.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT item.code,item.name,'ACTION',parent.id,item.sort_no FROM
                    (SELECT 'outsource:contact_manage' code,'管理对接联络表' name,86 sort_no
                     UNION ALL SELECT 'outsource:contact_settings','管理联络表列布局',87) item
                JOIN permissions parent ON parent.code='outsource'
                WHERE NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=item.code);
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r CROSS JOIN permissions p
                WHERE r.is_built_in=1 AND r.name='系统管理员'
                  AND p.code IN ('outsource:contact_manage','outsource:contact_settings');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outsource_contact_row_people");

            migrationBuilder.DropTable(
                name: "outsource_contact_row_values");

            migrationBuilder.DropTable(
                name: "outsource_contact_row_vendors");

            migrationBuilder.DropTable(
                name: "outsource_contact_people");

            migrationBuilder.DropTable(
                name: "outsource_contact_columns");

            migrationBuilder.DropTable(
                name: "outsource_contact_rows");
        }
    }
}
