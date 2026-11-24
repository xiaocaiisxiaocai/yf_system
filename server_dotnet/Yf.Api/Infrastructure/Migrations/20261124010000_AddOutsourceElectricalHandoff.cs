using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceElectricalHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "outsource_mails",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(16)",
                oldMaxLength: 16)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AlterColumn<ulong>(
                name: "created_by",
                table: "outsource_mails",
                type: "bigint unsigned",
                nullable: true,
                oldClrType: typeof(ulong),
                oldType: "bigint unsigned");

            migrationBuilder.AddColumn<ulong>(
                name: "created_by_oem_account_id",
                table: "outsource_mails",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_name",
                table: "outsource_mails",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<ulong>(
                name: "created_by",
                table: "outsource_mail_batches",
                type: "bigint unsigned",
                nullable: true,
                oldClrType: typeof(ulong),
                oldType: "bigint unsigned");

            migrationBuilder.AddColumn<ulong>(
                name: "created_by_oem_account_id",
                table: "outsource_mail_batches",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "electrical_handoff",
                table: "outsource_items",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "electrical_handoff_at",
                table: "outsource_items",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "electrical_handoff_signer",
                table: "outsource_items",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "outsource_default_cc_modules",
                columns: table => new
                {
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    module = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_default_cc_modules", x => new { x.user_id, x.module });
                    table.ForeignKey(
                        name: "fk_outsource_default_cc_modules_user",
                        column: x => x.user_id,
                        principalTable: "outsource_default_cc",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            // The pinned status column is at 20. Keep the relative order of all following fields.
            migrationBuilder.Sql("UPDATE outsource_fields SET sort_no=sort_no+10 WHERE sort_no>20;");
            migrationBuilder.Sql("""
                INSERT INTO outsource_fields(field_key,built_in,label,data_type,sort_no,status,vendor_visible,vendor_editable,editor_scope)
                VALUES ('electricalHandoff',1,'是否转迅得电控','BOOLEAN',30,'ACTIVE',1,0,'NONE');
                """);
            migrationBuilder.Sql("""
                INSERT INTO outsource_default_cc_modules(user_id,module)
                SELECT user_id,'OUTSOURCE_NOTICE' FROM outsource_default_cc;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM outsource_fields WHERE field_key='electricalHandoff';");
            migrationBuilder.Sql("UPDATE outsource_fields SET sort_no=sort_no-10 WHERE sort_no>30;");

            // In the old schema every standing recipient receives outsourcing notices.
            migrationBuilder.Sql("""
                DELETE FROM outsource_default_cc
                WHERE NOT EXISTS (
                    SELECT 1 FROM outsource_default_cc_modules notice
                    WHERE notice.user_id=outsource_default_cc.user_id AND notice.module='OUTSOURCE_NOTICE'
                );
                """);

            // The old queue has no vendor actor or handoff kinds. Child recipients and items cascade with the mail.
            migrationBuilder.Sql("DELETE FROM outsource_mails WHERE created_by IS NULL;");
            migrationBuilder.Sql("DELETE FROM outsource_mail_batches WHERE created_by IS NULL;");

            migrationBuilder.DropTable(
                name: "outsource_default_cc_modules");

            migrationBuilder.DropColumn(
                name: "created_by_oem_account_id",
                table: "outsource_mails");

            migrationBuilder.DropColumn(
                name: "signer_name",
                table: "outsource_mails");

            migrationBuilder.DropColumn(
                name: "created_by_oem_account_id",
                table: "outsource_mail_batches");

            migrationBuilder.DropColumn(
                name: "electrical_handoff",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "electrical_handoff_at",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "electrical_handoff_signer",
                table: "outsource_items");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "outsource_mails",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AlterColumn<ulong>(
                name: "created_by",
                table: "outsource_mails",
                type: "bigint unsigned",
                nullable: false,
                oldClrType: typeof(ulong),
                oldType: "bigint unsigned",
                oldNullable: true);

            migrationBuilder.AlterColumn<ulong>(
                name: "created_by",
                table: "outsource_mail_batches",
                type: "bigint unsigned",
                nullable: false,
                oldClrType: typeof(ulong),
                oldType: "bigint unsigned",
                oldNullable: true);
        }
    }
}
