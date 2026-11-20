using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// BOM时间 and 发图 leave the list editors and are only imported from Excel (外协清单契约 → BOM时间与发图导入,
    /// 2026-11-20) under a permission of their own. Inserted by code, no fixed ID (see AddDataScopesAndCrudPermissions);
    /// an empty database gets the catalog row from BootstrapSeedCatalog. Only the built-in administrator receives it.
    /// </summary>
    public partial class AddOutsourceDateImportPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'outsource:date_import','导入BOM时间与发图','ACTION',parent.id,80
                FROM (SELECT 1 AS one) seed
                LEFT JOIN permissions parent ON parent.code='outsource'
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='outsource:date_import');
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code='outsource:date_import'
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keeps the outsource:date_import permission and its grants: reference data survives a rollback.
        }
    }
}
