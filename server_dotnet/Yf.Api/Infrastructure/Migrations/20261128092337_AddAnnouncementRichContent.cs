using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnouncementRichContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "create_key",
                table: "announcements",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "ascii_bin");

            migrationBuilder.AddColumn<string>(
                name: "document_json",
                table: "announcements",
                type: "longtext",
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "normal_sort_order",
                table: "announcements",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pinned_sort_order",
                table: "announcements",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "announcement_assets",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    file_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    content_type = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false, collation: "ascii_bin"),
                    storage_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "ascii_bin"),
                    thumbnail_storage_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "ascii_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_assets", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "announcement_asset_links",
                columns: table => new
                {
                    announcement_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    asset_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: true),
                    upload_key = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "ascii_bin"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_asset_links", x => new { x.announcement_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_announcement_asset_links_announcement",
                        column: x => x.announcement_id,
                        principalTable: "announcements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_announcement_asset_links_asset",
                        column: x => x.asset_id,
                        principalTable: "announcement_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "uk_announcements_create",
                table: "announcements",
                columns: new[] { "created_by", "create_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_announcement_asset_links_pending",
                table: "announcement_asset_links",
                columns: new[] { "sort_order", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_announcement_asset_links_asset_id",
                table: "announcement_asset_links",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "uk_announcement_asset_links_upload",
                table: "announcement_asset_links",
                columns: new[] { "announcement_id", "upload_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_announcement_assets_path",
                table: "announcement_assets",
                column: "storage_path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "announcement_asset_links");

            migrationBuilder.DropTable(
                name: "announcement_assets");

            migrationBuilder.DropIndex(
                name: "uk_announcements_create",
                table: "announcements");

            migrationBuilder.DropColumn(
                name: "create_key",
                table: "announcements");

            migrationBuilder.DropColumn(
                name: "document_json",
                table: "announcements");

            migrationBuilder.DropColumn(
                name: "normal_sort_order",
                table: "announcements");

            migrationBuilder.DropColumn(
                name: "pinned_sort_order",
                table: "announcements");
        }
    }
}
