using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceItemModifiedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "modified_at",
                table: "outsource_items",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "modified_by",
                table: "outsource_items",
                type: "bigint unsigned",
                nullable: true);

            // Existing rows: the last hand edit recorded in the change history.
            migrationBuilder.Sql("""
                UPDATE outsource_items i
                JOIN (SELECT item_id, MAX(id) AS last_id FROM outsource_item_changes WHERE source = 'EDIT' GROUP BY item_id) latest
                    ON latest.item_id = i.id
                JOIN outsource_item_changes c ON c.id = latest.last_id
                SET i.modified_by = c.actor_user_id, i.modified_at = c.changed_at
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "modified_at",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "modified_by",
                table: "outsource_items");
        }
    }
}
