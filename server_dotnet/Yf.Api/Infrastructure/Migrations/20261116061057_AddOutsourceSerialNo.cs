using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceSerialNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "serial_no",
                table: "outsource_items",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            // Existing rows are numbered in the order they were imported, before the numbers must be unique.
            migrationBuilder.Sql("SET @serial := 0; UPDATE outsource_items SET serial_no = (@serial := @serial + 1) ORDER BY id;");

            migrationBuilder.CreateIndex(
                name: "uk_outsource_items_serial",
                table: "outsource_items",
                column: "serial_no",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uk_outsource_items_serial",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "serial_no",
                table: "outsource_items");
        }
    }
}
