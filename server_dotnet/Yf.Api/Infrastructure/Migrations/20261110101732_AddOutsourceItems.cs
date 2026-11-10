using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outsource_items",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    work_order_no = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    outsource_date = table.Column<DateOnly>(type: "date", nullable: false),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    customer = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    machine_model = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mechanical_section = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    mechanical_owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    electrical_owner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    work_order_issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    bom_on = table.Column<DateOnly>(type: "date", nullable: true),
                    drawing_released_on = table.Column<DateOnly>(type: "date", nullable: true),
                    type_code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    quantity = table.Column<int>(type: "int", nullable: true),
                    original_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    latest_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    remark = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "NORMAL", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    send_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "UNSENT", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    closed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    ipms_synced_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_items_vendor",
                        column: x => x.vendor_id,
                        principalTable: "outsource_vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_items_open_date",
                table: "outsource_items",
                columns: new[] { "closed_at", "outsource_date", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_outsource_items_vendor_open",
                table: "outsource_items",
                columns: new[] { "vendor_id", "closed_at", "outsource_date" });

            migrationBuilder.CreateIndex(
                name: "uk_outsource_items_work_order",
                table: "outsource_items",
                column: "work_order_no",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outsource_items");
        }
    }
}
