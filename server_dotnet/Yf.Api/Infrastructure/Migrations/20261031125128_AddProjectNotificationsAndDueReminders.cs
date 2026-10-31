using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// Robot 仿真协作第 2、3 批（Robot仿真协作流程调整方案 2026-10-31 §5）：
    /// 子项目记录到期 / 逾期提醒已发送的需求完成时间；补齐开始、终止、重新开始、重开、即将到期、已逾期邮件开关与
    /// 到期提醒参数（默认开启，不覆盖已有值）；删除已退役的验收邮件开关。
    /// </summary>
    public partial class AddProjectNotificationsAndDueReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "due_soon_notified_for",
                table: "projects",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "overdue_notified_for",
                table: "projects",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description)
                VALUES ('notify.event.project_started', 'true', '开始子项目邮件提醒'),
                       ('notify.event.project_terminated', 'true', '终止子项目邮件提醒'),
                       ('notify.event.project_restarted', 'true', '重新开始子项目邮件提醒'),
                       ('notify.event.project_reopened', 'true', '重开子项目邮件提醒'),
                       ('notify.event.project_due_soon', 'true', '即将到期邮件提醒'),
                       ('notify.event.project_overdue', 'true', '已逾期邮件提醒'),
                       ('notify.due.internal.enabled', 'true', '到期提醒是否发给负责人'),
                       ('notify.due.supplier.enabled', 'true', '到期提醒是否发给厂商'),
                       ('notify.due.remind_days_before', '3', '需求完成时间前几天发送即将到期提醒（1–30）');
                """);

            migrationBuilder.Sql("""
                DELETE FROM system_configs
                WHERE cfg_key IN ('notify.event.project_submitted', 'notify.event.project_confirmed',
                                  'notify.event.project_rejected', 'notify.event.project_withdrawn');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 不回收新增的邮件参数、不恢复已退役的验收开关：保留参考数据，旧版本对缺失开关按默认开启处理。

            migrationBuilder.DropColumn(
                name: "due_soon_notified_for",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "overdue_notified_for",
                table: "projects");
        }
    }
}
