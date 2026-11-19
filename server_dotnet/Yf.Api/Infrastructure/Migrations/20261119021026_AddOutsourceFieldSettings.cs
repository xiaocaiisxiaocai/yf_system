using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceFieldSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "vendor_seen_at",
                table: "outsource_items",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "vendor_updated_at",
                table: "outsource_items",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vendor_updated_signer",
                table: "outsource_items",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<ulong>(
                name: "actor_oem_account_id",
                table: "outsource_item_changes",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                table: "outsource_item_changes",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "outsource_fields",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    field_key = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    built_in = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    label = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    data_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    options = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    editor_scope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ALL", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    vendor_visible = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    vendor_editable = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    updated_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_fields", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_field_editor_roles",
                columns: table => new
                {
                    field_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    role_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_field_editor_roles", x => new { x.field_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_outsource_field_editor_roles_field",
                        column: x => x.field_id,
                        principalTable: "outsource_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outsource_field_editor_roles_role",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_item_values",
                columns: table => new
                {
                    item_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    field_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    value = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_item_values", x => new { x.item_id, x.field_id });
                    table.ForeignKey(
                        name: "fk_outsource_item_values_field",
                        column: x => x.field_id,
                        principalTable: "outsource_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outsource_item_values_item",
                        column: x => x.item_id,
                        principalTable: "outsource_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_field_editor_roles_role",
                table: "outsource_field_editor_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "uk_outsource_fields_key",
                table: "outsource_fields",
                column: "field_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outsource_item_values_field",
                table: "outsource_item_values",
                column: "field_id");

            // The list's columns in today's order (外协清单契约 → 页面, 2026-11-17), every one shown to vendors as before,
            // plus the first custom field 是否有RobotV after 机型: hidden from vendors and filled in internally.
            // Editor scope ALL keeps today's rule (everyone with outsource:manage); IPMS and fixed columns are NONE.
            migrationBuilder.Sql("""
                INSERT INTO outsource_fields(field_key,built_in,label,data_type,sort_no,status,vendor_visible,vendor_editable,editor_scope)
                VALUES
                  ('outsourceDate',1,'外发日期','DATE',10,'ACTIVE',1,0,'ALL'),
                  ('status',1,'状态','SELECT',20,'ACTIVE',1,0,'ALL'),
                  ('workOrderNo',1,'工令','TEXT',30,'ACTIVE',1,0,'NONE'),
                  ('customer',1,'客户','TEXT',40,'ACTIVE',1,0,'NONE'),
                  ('machineModel',1,'机型','TEXT',50,'ACTIVE',1,0,'NONE'),
                  ('hasRobotV',0,'是否有RobotV','BOOLEAN',60,'ACTIVE',0,0,'ALL'),
                  ('type',1,'外发类型','SELECT',70,'ACTIVE',1,0,'ALL'),
                  ('vendor',1,'外发厂商','SELECT',80,'ACTIVE',1,0,'ALL'),
                  ('mechanicalSection',1,'机构课别','TEXT',90,'ACTIVE',1,0,'NONE'),
                  ('mechanicalOwner',1,'机构执行人','TEXT',100,'ACTIVE',1,0,'NONE'),
                  ('electricalOwner',1,'电控执行人','TEXT',110,'ACTIVE',1,0,'NONE'),
                  ('workOrderIssuedOn',1,'工令下发','DATE',120,'ACTIVE',1,0,'NONE'),
                  ('bomOn',1,'BOM时间','DATE',130,'ACTIVE',1,0,'ALL'),
                  ('drawingReleasedOn',1,'发图','DATE',140,'ACTIVE',1,0,'ALL'),
                  ('originalDueDate',1,'原始交期','DATE',150,'ACTIVE',1,0,'NONE'),
                  ('latestDueDate',1,'最新交期','DATE',160,'ACTIVE',1,0,'ALL'),
                  ('quantity',1,'数量','NUMBER',170,'ACTIVE',1,0,'NONE'),
                  ('remark',1,'备注','TEXT',180,'ACTIVE',1,0,'ALL');
                """);

            // Insert by code (no fixed ID; see AddDataScopesAndCrudPermissions); an empty database gets the catalog
            // row from BootstrapSeedCatalog after migrations run.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'outsource:field_settings','外协栏位设定','ACTION',parent.id,78
                FROM (SELECT 1 AS one) seed
                LEFT JOIN permissions parent ON parent.code='outsource'
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='outsource:field_settings');
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code='outsource:field_settings'
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keeps the outsource:field_settings permission and its grant: reference data survives a rollback.
            migrationBuilder.DropTable(
                name: "outsource_field_editor_roles");

            migrationBuilder.DropTable(
                name: "outsource_item_values");

            migrationBuilder.DropTable(
                name: "outsource_fields");

            migrationBuilder.DropColumn(
                name: "vendor_seen_at",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "vendor_updated_at",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "vendor_updated_signer",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "actor_oem_account_id",
                table: "outsource_item_changes");

            migrationBuilder.DropColumn(
                name: "signer_name",
                table: "outsource_item_changes");
        }
    }
}
