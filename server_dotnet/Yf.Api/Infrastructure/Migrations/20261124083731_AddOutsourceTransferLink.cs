using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceTransferLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "outsource_item_id",
                table: "oem_transfers",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfers_outsource_item",
                table: "oem_transfers",
                column: "outsource_item_id");

            migrationBuilder.AddForeignKey(
                name: "fk_oem_transfers_outsource_item",
                table: "oem_transfers",
                column: "outsource_item_id",
                principalTable: "outsource_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Fresh databases receive the permission from BootstrapSeedCatalog after migrations.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'outsource:file_view','外协档案','ACTION',parent.id,81
                FROM permissions parent
                WHERE parent.code='outsource'
                  AND EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='outsource:file_view');
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code='outsource:file_view'
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_oem_transfers_outsource_item",
                table: "oem_transfers");

            migrationBuilder.DropIndex(
                name: "idx_oem_transfers_outsource_item",
                table: "oem_transfers");

            migrationBuilder.DropColumn(
                name: "outsource_item_id",
                table: "oem_transfers");

            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp INNER JOIN permissions p ON p.id=rp.permission_id
                WHERE p.code='outsource:file_view';
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code='outsource:file_view';");
        }
    }
}
