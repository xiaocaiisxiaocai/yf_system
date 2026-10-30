using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 资料确认：负责人确认厂商回传资料 OK 后厂商才能提交验收（主项目与子项目协作契约 2026-10-29）。
    /// 已有数据库补上 notify.event.project_material_reviewed 邮件开关（默认开启，不覆盖已有值）。
    /// </summary>
    public partial class AddProjectMaterialReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "material_review_file_id",
                table: "projects",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "material_review_reason",
                table: "projects",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "material_review_status",
                table: "projects",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql("""
                INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description)
                VALUES ('notify.event.project_material_reviewed', 'true', '资料确认邮件提醒');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 不回收 notify.event.project_material_reviewed：保留管理员已保存的取值，旧版本忽略这一行。

            migrationBuilder.DropColumn(
                name: "material_review_file_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "material_review_reason",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "material_review_status",
                table: "projects");
        }
    }
}
