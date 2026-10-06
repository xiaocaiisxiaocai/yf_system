using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations;

public partial class AddOemDirectoryDeletePermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing managers do not implicitly receive destructive capabilities.
        // Insert by code so installations with custom permission IDs remain valid.
        migrationBuilder.Sql("""
            INSERT INTO permissions(code,name,type,parent_id,sort_no)
            SELECT seed.code,seed.name,'ACTION',parent.id,seed.sort_no
            FROM (
                SELECT 'oem:company_delete' code,'删除 OEM 厂商' name,63 sort_no
                UNION ALL SELECT 'oem:account_delete','删除 OEM 账号',64
            ) seed
            INNER JOIN permissions parent ON parent.code='oem'
            WHERE NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=seed.code);
            """);
        migrationBuilder.Sql("""
            INSERT IGNORE INTO role_permissions(role_id,permission_id)
            SELECT r.id,p.id FROM roles r
            INNER JOIN permissions p ON p.code IN ('oem:company_delete','oem:account_delete')
            WHERE r.is_built_in=1 AND r.name='系统管理员';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE rp FROM role_permissions rp INNER JOIN permissions p ON p.id=rp.permission_id
            WHERE p.code IN ('oem:company_delete','oem:account_delete');
            """);
        migrationBuilder.Sql("DELETE FROM permissions WHERE code IN ('oem:company_delete','oem:account_delete');");
    }
}
