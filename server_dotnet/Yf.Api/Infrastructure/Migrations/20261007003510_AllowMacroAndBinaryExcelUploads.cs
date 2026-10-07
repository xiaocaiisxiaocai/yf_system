using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 上传资料要求契约 2026-10-06：.xls/.xlsx/.xlsm/.xlsb 同等对待。已有数据库的上传白名单只要允许 xlsx，
    /// 就补上缺失的 xlsm/xlsb；管理员已去掉 xlsx（不允许 Excel）的白名单保持不变。数据迁移，不改结构。
    /// </summary>
    public partial class AllowMacroAndBinaryExcelUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var extension in new[] { "xlsm", "xlsb" })
            {
                migrationBuilder.Sql($"""
                    UPDATE system_configs
                    SET cfg_value = CONCAT(cfg_value, ',{extension}')
                    WHERE cfg_key = 'upload.allowed_exts'
                      AND FIND_IN_SET('xlsx', REPLACE(LOWER(cfg_value), ' ', '')) > 0
                      AND FIND_IN_SET('{extension}', REPLACE(LOWER(cfg_value), ' ', '')) = 0;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 不回收白名单：无法区分管理员手动加入的 xlsm/xlsb，保留它们对旧版本也无害
            // （旧版本的内部上传命名规则只接受 .xlsx，供应商方向本就不限制）。
        }
    }
}
