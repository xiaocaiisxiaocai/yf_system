using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MarkLegacyOutsourceSupervisorBaseline : Migration
    {
        internal const string MarkLegacySnapshotsSql = """
                UPDATE outsource_items
                SET last_sent_snapshot = JSON_SET(last_sent_snapshot, '$.mechanicalSection', '~legacy-section~')
                WHERE last_sent_snapshot IS NOT NULL AND JSON_VALID(last_sent_snapshot)
                  AND JSON_CONTAINS_PATH(last_sent_snapshot, 'one', '$.mechanicalSection');
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Snapshots mailed before 机构课别 became 上级主管 hold a department name for that column. Replace it with a marker
            // so the upgrade neither highlights nor queues every sent item; the next IPMS sync adopts the current supervisor.
            migrationBuilder.Sql(MarkLegacySnapshotsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The department names are not recoverable; the marker is ignored by older code only as a changed value.
        }
    }
}
