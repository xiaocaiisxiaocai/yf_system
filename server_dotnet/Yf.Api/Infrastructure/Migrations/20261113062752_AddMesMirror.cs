using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMesMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mes_mirror_state",
                columns: table => new
                {
                    id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    scheduled_for = table.Column<DateOnly>(type: "date", nullable: true),
                    lease_until = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_started_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_finished_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_error = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_trigger = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mes_mirror_state", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "mes_work_orders",
                columns: table => new
                {
                    work_order_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    batch_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    fetched_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    customer_name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    device_models = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    raw_json = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mes_work_orders", x => x.work_order_no);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "mes_work_orders_staging",
                columns: table => new
                {
                    work_order_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    batch_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    fetched_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    customer_name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    device_models = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    raw_json = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mes_work_orders_staging", x => x.work_order_no);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mes_mirror_state");

            migrationBuilder.DropTable(
                name: "mes_work_orders");

            migrationBuilder.DropTable(
                name: "mes_work_orders_staging");
        }
    }
}
