using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOemApprovalTaskActivatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "activated_at",
                table: "oem_flow_tasks",
                type: "datetime(3)",
                nullable: true);

            // Backfill tasks that are currently waiting on their approver. A reassigned task became
            // pending when it was created; otherwise the node activated when the previous node
            // completed, and for a first node the instance's last change is the closest record.
            migrationBuilder.Sql(@"
UPDATE oem_flow_tasks t
JOIN oem_flow_instances i ON i.id = t.instance_id
JOIN oem_flow_instance_nodes n ON n.id = t.instance_node_id
SET t.activated_at = CASE
    WHEN t.replaces_task_id IS NOT NULL THEN t.created_at
    ELSE COALESCE(
        (SELECT MAX(p.completed_at) FROM oem_flow_instance_nodes p
         WHERE p.instance_id = t.instance_id AND p.sort_no < n.sort_no),
        i.updated_at)
END
WHERE t.status = 'PENDING' AND t.activated_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "activated_at",
                table: "oem_flow_tasks");
        }
    }
}
