using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outsource_sync_state",
                columns: table => new
                {
                    id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    sync_time = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false, defaultValue: "02:00", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    scheduled_for = table.Column<DateOnly>(type: "date", nullable: true),
                    lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_started_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_finished_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_error = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_trigger = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_processed = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    last_changed = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    last_closed = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    last_missing = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_sync_state", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outsource_sync_state");
        }
    }
}
