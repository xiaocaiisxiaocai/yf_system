using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 厂商联络人与签名契约 2026-10-30：供应商联络人、主项目 Robot 负责人多选（联络人关联 + 姓名快照，
    /// robot_owner_name 放宽到 512），以及留言、上传会话、文件的签名联络人与姓名快照。只加结构，不回填历史数据。
    /// </summary>
    public partial class AddSupplierContactsAndSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "signer_contact_id",
                table: "upload_sessions",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                table: "upload_sessions",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "robot_owner_name",
                table: "projects",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AlterColumn<string>(
                name: "robot_owner_name",
                table: "project_groups",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AddColumn<ulong>(
                name: "signer_contact_id",
                table: "messages",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                table: "messages",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<ulong>(
                name: "signer_contact_id",
                table: "files",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                table: "files",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "supplier_contacts",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    supplier_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
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
                    table.PrimaryKey("PK_supplier_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_supplier_contacts_supplier",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "project_group_robot_owners",
                columns: table => new
                {
                    project_group_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    contact_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group_robot_owners", x => new { x.project_group_id, x.contact_id });
                    table.ForeignKey(
                        name: "fk_project_group_robot_owners_contact",
                        column: x => x.contact_id,
                        principalTable: "supplier_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_group_robot_owners_group",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "fk_us_signer_contact",
                table: "upload_sessions",
                column: "signer_contact_id");

            migrationBuilder.CreateIndex(
                name: "fk_msg_signer_contact",
                table: "messages",
                column: "signer_contact_id");

            migrationBuilder.CreateIndex(
                name: "fk_files_signer_contact",
                table: "files",
                column: "signer_contact_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_group_robot_owners_contact",
                table: "project_group_robot_owners",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "idx_supplier_contacts_supplier_status",
                table: "supplier_contacts",
                columns: new[] { "supplier_id", "status", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_supplier_contacts_supplier_name",
                table: "supplier_contacts",
                columns: new[] { "supplier_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_files_signer_contact",
                table: "files",
                column: "signer_contact_id",
                principalTable: "supplier_contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_msg_signer_contact",
                table: "messages",
                column: "signer_contact_id",
                principalTable: "supplier_contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_us_signer_contact",
                table: "upload_sessions",
                column: "signer_contact_id",
                principalTable: "supplier_contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_files_signer_contact",
                table: "files");

            migrationBuilder.DropForeignKey(
                name: "fk_msg_signer_contact",
                table: "messages");

            migrationBuilder.DropForeignKey(
                name: "fk_us_signer_contact",
                table: "upload_sessions");

            migrationBuilder.DropTable(
                name: "project_group_robot_owners");

            migrationBuilder.DropTable(
                name: "supplier_contacts");

            migrationBuilder.DropIndex(
                name: "fk_us_signer_contact",
                table: "upload_sessions");

            migrationBuilder.DropIndex(
                name: "fk_msg_signer_contact",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "fk_files_signer_contact",
                table: "files");

            migrationBuilder.DropColumn(
                name: "signer_contact_id",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "signer_name",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "signer_contact_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "signer_name",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "signer_contact_id",
                table: "files");

            migrationBuilder.DropColumn(
                name: "signer_name",
                table: "files");

            // Multi-owner snapshots can exceed the old 64-character column; keep the leading names.
            migrationBuilder.Sql("UPDATE projects SET robot_owner_name=LEFT(robot_owner_name,64) WHERE CHAR_LENGTH(robot_owner_name)>64");
            migrationBuilder.Sql("UPDATE project_groups SET robot_owner_name=LEFT(robot_owner_name,64) WHERE CHAR_LENGTH(robot_owner_name)>64");

            migrationBuilder.AlterColumn<string>(
                name: "robot_owner_name",
                table: "projects",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(512)",
                oldMaxLength: 512,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AlterColumn<string>(
                name: "robot_owner_name",
                table: "project_groups",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(512)",
                oldMaxLength: 512,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");
        }
    }
}
