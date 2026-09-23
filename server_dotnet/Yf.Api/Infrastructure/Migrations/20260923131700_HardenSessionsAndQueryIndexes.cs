using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenSessionsAndQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "session_created_at",
                table: "refresh_tokens",
                type: "datetime",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "session_expires_at",
                table: "refresh_tokens",
                type: "datetime",
                nullable: true);

            // Preserve the age of existing token families; upgrading must not renew sessions.
            // The grouped derived table is materialized by MySQL 5.7 (contains GROUP BY).
            migrationBuilder.Sql("""
                UPDATE refresh_tokens AS token
                JOIN (SELECT user_id, session_id, MIN(created_at) AS started_at
                      FROM refresh_tokens GROUP BY user_id, session_id) AS family
                  ON token.user_id = family.user_id AND token.session_id = family.session_id
                SET token.session_created_at = family.started_at,
                    token.session_expires_at = DATE_ADD(family.started_at, INTERVAL 30 DAY),
                    token.expires_at = LEAST(token.expires_at, DATE_ADD(family.started_at, INTERVAL 30 DAY));
                """);
            migrationBuilder.AlterColumn<DateTime>(
                name: "session_created_at", table: "refresh_tokens", type: "datetime", nullable: false,
                oldClrType: typeof(DateTime), oldType: "datetime", oldNullable: true);
            migrationBuilder.AlterColumn<DateTime>(
                name: "session_expires_at", table: "refresh_tokens", type: "datetime", nullable: false,
                oldClrType: typeof(DateTime), oldType: "datetime", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_refresh_tokens_session_expires",
                table: "refresh_tokens",
                column: "session_expires_at");

            migrationBuilder.CreateIndex(
                name: "idx_msg_project_status_id",
                table: "messages",
                columns: new[] { "project_id", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_audit_action_time",
                table: "audit_logs",
                columns: new[] { "action", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_audit_target",
                table: "audit_logs",
                columns: new[] { "target_type", "target_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_refresh_tokens_session_expires",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "idx_msg_project_status_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "idx_audit_action_time",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "idx_audit_target",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "session_created_at",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "session_expires_at",
                table: "refresh_tokens");
        }
    }
}
