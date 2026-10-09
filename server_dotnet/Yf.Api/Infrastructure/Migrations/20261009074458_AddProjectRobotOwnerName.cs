using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRobotOwnerName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "robot_owner_name",
                table: "projects",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "robot_owner_name",
                table: "project_groups",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "robot_owner_name",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "robot_owner_name",
                table: "project_groups");
        }
    }
}
