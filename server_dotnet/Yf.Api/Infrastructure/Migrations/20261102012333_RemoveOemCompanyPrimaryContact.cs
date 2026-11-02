using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOemCompanyPrimaryContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "phone", table: "oem_company_contacts");
            migrationBuilder.DropColumn(
                name: "contact_email",
                table: "oem_companies");

            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "oem_companies");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                table: "oem_companies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "phone", table: "oem_company_contacts", type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci").Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                table: "oem_companies",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "oem_companies",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                table: "oem_companies",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
