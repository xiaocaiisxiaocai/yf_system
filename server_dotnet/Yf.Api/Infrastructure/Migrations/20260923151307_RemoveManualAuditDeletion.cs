using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveManualAuditDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // idx_audit_action_time (action, created_at, id) already serves every action lookup.
            migrationBuilder.DropIndex(
                name: "idx_audit_action",
                table: "audit_logs");

            // Audit logs are only removed by the retention worker; the manual-deletion grant is retired.
            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE p.code = 'log:delete';
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code = 'log:delete';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO permissions (id, code, name, type, parent_id, sort_no)
                SELECT 35, 'log:delete', '删除日志', 'ACTION', 7, 26
                FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE id = 7)
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE code = 'log:delete');
                """);

            migrationBuilder.CreateIndex(
                name: "idx_audit_action",
                table: "audit_logs",
                column: "action");
        }
    }
}
