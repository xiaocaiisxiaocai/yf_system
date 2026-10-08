using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRobotType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "robot_type_id",
                table: "projects",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "robot_type_id",
                table: "project_groups",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_projects_robot_type",
                table: "projects",
                column: "robot_type_id");

            migrationBuilder.CreateIndex(
                name: "fk_project_groups_robot_type",
                table: "project_groups",
                column: "robot_type_id");

            migrationBuilder.AddForeignKey(
                name: "fk_project_groups_robot_type",
                table: "project_groups",
                column: "robot_type_id",
                principalTable: "project_dictionaries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_robot_type",
                table: "projects",
                column: "robot_type_id",
                principalTable: "project_dictionaries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_project_groups_robot_type",
                table: "project_groups");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_robot_type",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "idx_projects_robot_type",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "fk_project_groups_robot_type",
                table: "project_groups");

            migrationBuilder.DropColumn(
                name: "robot_type_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "robot_type_id",
                table: "project_groups");
        }
    }
}
