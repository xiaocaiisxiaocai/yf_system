using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_message_id",
                table: "messages",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "sha256",
                table: "message_images",
                type: "char(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "uk_messages_sender_client_id",
                table: "messages",
                columns: new[] { "project_id", "sender_id", "client_message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uk_messages_sender_client_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "client_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "sha256",
                table: "message_images");
        }
    }
}
