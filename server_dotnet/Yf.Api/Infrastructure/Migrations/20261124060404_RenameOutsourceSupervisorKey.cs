using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameOutsourceSupervisorKey : Migration
    {
        internal const string RenameKeySql = """
            UPDATE outsource_fields SET field_key='supervisor' WHERE field_key='mechanicalSection' AND built_in=1;
            UPDATE outsource_items
            SET last_sent_snapshot = JSON_REMOVE(JSON_SET(last_sent_snapshot, '$.supervisor', JSON_EXTRACT(last_sent_snapshot, '$.mechanicalSection')), '$.mechanicalSection')
            WHERE last_sent_snapshot IS NOT NULL AND JSON_VALID(last_sent_snapshot)
              AND JSON_CONTAINS_PATH(last_sent_snapshot, 'one', '$.mechanicalSection');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column holds the owner's current supervisor, computed on read; the stored department name is gone.
            // Its key moves with it: column setting and the last-mailed snapshots (which may carry the legacy marker).
            migrationBuilder.Sql(RenameKeySql);
            migrationBuilder.DropColumn(
                name: "mechanical_section",
                table: "outsource_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE outsource_fields SET field_key='mechanicalSection' WHERE field_key='supervisor' AND built_in=1;
                UPDATE outsource_items
                SET last_sent_snapshot = JSON_REMOVE(JSON_SET(last_sent_snapshot, '$.mechanicalSection', JSON_EXTRACT(last_sent_snapshot, '$.supervisor')), '$.supervisor')
                WHERE last_sent_snapshot IS NOT NULL AND JSON_VALID(last_sent_snapshot)
                  AND JSON_CONTAINS_PATH(last_sent_snapshot, 'one', '$.supervisor');
                """);
            migrationBuilder.AddColumn<string>(
                name: "mechanical_section",
                table: "outsource_items",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
