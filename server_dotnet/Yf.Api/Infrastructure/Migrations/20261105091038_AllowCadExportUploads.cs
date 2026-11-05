using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// Robot 与 ODM/OEMA 上传白名单补上 SOLIDWORKS 等 CAD 软件的导出格式。已有数据库只追加缺失的扩展名，
    /// 管理员已配置的其他扩展名保持不变。数据迁移，不改结构。
    /// </summary>
    public partial class AllowCadExportUploads : Migration
    {
        private static readonly string[] Keys =
        [
            "upload.allowed_exts",
            "oem.upload.allowed_exts.internal_to_oem",
            "oem.upload.allowed_exts.oem_to_internal",
        ];

        private static readonly string[] Extensions =
        [
            "3dxml", "3mf", "ai", "amf", "eprt", "glb", "gltf", "hcg", "hsf", "ifc", "prt", "prtdot", "ply", "psd",
            "sat", "sldftp", "sldlfp", "sldprt", "tif", "vda", "wrl", "x_b", "x_t", "xaml",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var key in Keys)
            foreach (var extension in Extensions)
            {
                migrationBuilder.Sql($"""
                    UPDATE system_configs
                    SET cfg_value = CONCAT(cfg_value, ',{extension}')
                    WHERE cfg_key = '{key}'
                      AND TRIM(cfg_value) <> ''
                      AND FIND_IN_SET('{extension}', REPLACE(LOWER(cfg_value), ' ', '')) = 0;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 不回收白名单：无法区分管理员手动加入的同名扩展名。旧版本上传时只按白名单比对扩展名，保留无害；
            // 但旧版本保存白名单时不接受 x_t/x_b 这类带下划线的扩展名，回滚后需先删掉它们才能再保存。
        }
    }
}
