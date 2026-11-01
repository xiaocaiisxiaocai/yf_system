﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 子项目记录本轮资料起点：开始 / 重新开始时写入当时最大的 S2C 文件 id，只有更新的厂商回传才算待确认资料，
    /// 复制件与重新开始的子项目不会沿用旧回传直接确认完成。
    /// </summary>
    public partial class AddProjectMaterialRound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "material_file_floor",
                table: "projects",
                type: "bigint unsigned",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "material_file_floor",
                table: "projects");
        }
    }
}
