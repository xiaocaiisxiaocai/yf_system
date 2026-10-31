using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 单关卡流程（Robot仿真协作流程调整方案 2026-10-31）：负责人资料确认 OK 即完成子项目，
    /// 退出“提交 / 撤回 / 验收”与 PENDING_CONFIRMATION 状态，退役 project:submit / project:confirm / project:withdraw。
    /// </summary>
    public partial class RetireAcceptanceWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Subprojects waiting for acceptance go back to in progress with their material pending review, so the
            // responsible user can approve them directly; their unsent acceptance mail is no longer valid.
            migrationBuilder.Sql("""
                UPDATE projects
                SET status = 'IN_PROGRESS', material_review_status = NULL, material_review_file_id = NULL,
                    material_review_reason = NULL
                WHERE status = 'PENDING_CONFIRMATION';
                """);
            migrationBuilder.Sql("""
                UPDATE email_outbox
                SET status = 'CANCELLED', next_attempt_at = NULL, last_error = '验收流程已退役，通知已取消'
                WHERE event_type = 'PROJECT_SUBMITTED' AND sent_at IS NULL AND status IN ('PENDING', 'FAILED');
                """);

            migrationBuilder.DropColumn(
                name: "confirm_side",
                table: "projects");

            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE p.code IN ('project:submit', 'project:confirm', 'project:withdraw');
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code IN ('project:submit', 'project:confirm', 'project:withdraw');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the column comes back. Like the other catalog migrations, Down leaves reference data alone: the
            // retired permission codes, their grants, PENDING_CONFIRMATION rows and cancelled mail are not reconstructed.
            migrationBuilder.AddColumn<string>(
                name: "confirm_side",
                table: "projects",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
