using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnreadWindowIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_project_activities_occurred",
                table: "project_activities",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "idx_msg_created",
                table: "messages",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_project_activities_occurred",
                table: "project_activities");

            migrationBuilder.DropIndex(
                name: "idx_msg_created",
                table: "messages");
        }
    }
}
