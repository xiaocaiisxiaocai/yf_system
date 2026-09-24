using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDictionaryAndOwnerTransferPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing databases only: an empty database gets the full catalog from BootstrapSeedCatalog
            // after migrations run, so insert nothing until the base catalog (dashboard, id 1) exists.
            migrationBuilder.Sql("""
                INSERT INTO permissions (id, code, name, type, parent_id, sort_no)
                SELECT 48, 'system:dict', '数据字典', 'MENU', NULL, 8 FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE id = 1)
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE id = 48 OR code = 'system:dict');
                """);
            migrationBuilder.Sql("""
                INSERT INTO permissions (id, code, name, type, parent_id, sort_no)
                SELECT 49, 'dict:manage', '数据字典管理', 'ACTION', 48, 28 FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE id = 48)
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE id = 49 OR code = 'dict:manage');
                """);
            migrationBuilder.Sql("""
                INSERT INTO permissions (id, code, name, type, parent_id, sort_no)
                SELECT 50, 'project:transfer', '变更项目负责人', 'ACTION', 2, 43 FROM DUAL
                WHERE EXISTS (SELECT 1 FROM permissions WHERE id = 2)
                  AND NOT EXISTS (SELECT 1 FROM permissions WHERE id = 50 OR code = 'project:transfer');
                """);

            // Dictionary maintenance used to ride on config:manage; roles that had it keep the ability.
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions (role_id, permission_id)
                SELECT rp.role_id, p.id
                FROM role_permissions rp
                INNER JOIN permissions granted ON granted.id = rp.permission_id AND granted.code = 'config:manage'
                INNER JOIN permissions p ON p.code IN ('system:dict', 'dict:manage');
                """);
            // The built-in administrator keeps full authority.
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions (role_id, permission_id)
                SELECT r.id, p.id FROM roles r
                INNER JOIN permissions p ON p.code IN ('system:dict', 'dict:manage', 'project:transfer')
                WHERE r.is_built_in = 1 AND r.name = '系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE p.code IN ('system:dict', 'dict:manage', 'project:transfer');
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code IN ('dict:manage', 'project:transfer');");
            migrationBuilder.Sql("DELETE FROM permissions WHERE code = 'system:dict';");
        }
    }
}
