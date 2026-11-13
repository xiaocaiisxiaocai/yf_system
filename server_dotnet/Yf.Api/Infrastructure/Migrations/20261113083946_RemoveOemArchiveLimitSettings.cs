using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// ODM/OEMA validation no longer opens archives or Office documents (only the format is checked),
    /// so the archive entry, depth, expanded-size and ratio limits have no effect and are removed.
    /// </summary>
    public partial class RemoveOemArchiveLimitSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM system_configs WHERE cfg_key IN (
                    'oem.validation.archive_max_entries','oem.validation.archive_max_depth',
                    'oem.validation.archive_max_expanded_bytes','oem.validation.archive_max_ratio');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the previous version reads missing archive limits as their defaults,
            // and leaving the rows out keeps the round-trip reference data unchanged.
        }
    }
}
