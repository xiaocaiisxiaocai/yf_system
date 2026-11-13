using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIpmsConnectionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account",
                table: "mes_mirror_state",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "protected_password",
                table: "mes_mirror_state",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "sync_time",
                table: "mes_mirror_state",
                type: "varchar(5)",
                maxLength: 5,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "account",
                table: "mes_mirror_state");

            migrationBuilder.DropColumn(
                name: "protected_password",
                table: "mes_mirror_state");

            migrationBuilder.DropColumn(
                name: "sync_time",
                table: "mes_mirror_state");
        }
    }
}
