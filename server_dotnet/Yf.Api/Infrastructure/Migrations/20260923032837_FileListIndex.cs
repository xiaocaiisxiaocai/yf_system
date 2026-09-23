using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FileListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // fk_files_project needs an index led by project_id at all times: create the replacement first.
            migrationBuilder.CreateIndex(
                name: "idx_files_project_status",
                table: "files",
                columns: new[] { "project_id", "status", "id" });

            migrationBuilder.DropIndex(
                name: "idx_files_project",
                table: "files");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_files_project",
                table: "files",
                column: "project_id");

            migrationBuilder.DropIndex(
                name: "idx_files_project_status",
                table: "files");
        }
    }
}
