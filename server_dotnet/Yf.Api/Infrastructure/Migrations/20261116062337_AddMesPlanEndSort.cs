using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMesPlanEndSort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "plan_end_sort",
                table: "mes_work_orders",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                computedColumnSql: "LEFT(COALESCE(IF(JSON_TYPE(JSON_EXTRACT(raw_json,'$.planEndDate'))='NULL','',JSON_UNQUOTE(JSON_EXTRACT(raw_json,'$.planEndDate'))),''),32)",
                stored: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "idx_mes_work_orders_plan_end",
                table: "mes_work_orders",
                columns: new[] { "plan_end_sort", "work_order_no" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_mes_work_orders_plan_end",
                table: "mes_work_orders");

            migrationBuilder.DropColumn(
                name: "plan_end_sort",
                table: "mes_work_orders");
        }
    }
}
