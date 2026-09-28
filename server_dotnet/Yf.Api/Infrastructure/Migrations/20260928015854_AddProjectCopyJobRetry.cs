using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCopyJobRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "project_copy_jobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "retry_count",
                table: "project_copy_jobs",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "project_copy_jobs");

            migrationBuilder.DropColumn(
                name: "retry_count",
                table: "project_copy_jobs");
        }
    }
}
