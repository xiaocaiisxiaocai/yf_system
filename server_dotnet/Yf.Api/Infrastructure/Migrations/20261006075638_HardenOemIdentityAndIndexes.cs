using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenOemIdentityAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_audit_actor_realm",
                table: "audit_logs");

            migrationBuilder.AddColumn<string>(
                name: "revoke_reason",
                table: "oem_refresh_tokens",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "idx_oem_upload_sessions_uploader",
                table: "oem_upload_sessions",
                columns: new[] { "uploader_realm", "uploader_id" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_closed_by",
                table: "oem_transfers",
                columns: new[] { "closed_by_realm", "closed_by_id" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_first_recipient",
                table: "oem_transfer_files",
                columns: new[] { "first_recipient_realm", "first_recipient_id" });

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_internal_uploader",
                table: "oem_transfer_files",
                column: "uploaded_by_internal_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_files_oem_uploader",
                table: "oem_transfer_files",
                column: "uploaded_by_oem_account_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_flow_templates_default",
                table: "oem_flow_templates",
                column: "is_default");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_recipient_account",
                table: "email_outbox",
                columns: new[] { "recipient_realm", "recipient_account_id" });

            migrationBuilder.CreateIndex(
                name: "idx_audit_actor_account",
                table: "audit_logs",
                columns: new[] { "actor_realm", "actor_account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_oem_upload_sessions_uploader",
                table: "oem_upload_sessions");

            migrationBuilder.DropIndex(
                name: "idx_oem_transfers_closed_by",
                table: "oem_transfers");

            migrationBuilder.DropIndex(
                name: "idx_oem_transfer_files_first_recipient",
                table: "oem_transfer_files");

            migrationBuilder.DropIndex(
                name: "idx_oem_transfer_files_internal_uploader",
                table: "oem_transfer_files");

            migrationBuilder.DropIndex(
                name: "idx_oem_transfer_files_oem_uploader",
                table: "oem_transfer_files");

            migrationBuilder.DropIndex(
                name: "idx_oem_flow_templates_default",
                table: "oem_flow_templates");

            migrationBuilder.DropIndex(
                name: "idx_outbox_recipient_account",
                table: "email_outbox");

            migrationBuilder.DropIndex(
                name: "idx_audit_actor_account",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "revoke_reason",
                table: "oem_refresh_tokens");

            migrationBuilder.CreateIndex(
                name: "idx_audit_actor_realm",
                table: "audit_logs",
                column: "actor_realm");
        }
    }
}
