using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// Outsourcing keeps three permissions (外协清单契约 → 权限, 2026-11-19): every internal account sees the list, so
    /// outsource:view goes; importing joins outsource:manage (管理外协清单); field settings join outsource:settings
    /// (外协清单设定). Grants move with them; a view-only role gets nothing new, since seeing the list needs no permission.
    /// </summary>
    public partial class SimplifyOutsourcePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT grant_row.role_id, manage.id
                FROM role_permissions grant_row
                INNER JOIN permissions old ON old.id=grant_row.permission_id AND old.code='outsource:import'
                CROSS JOIN permissions manage
                WHERE manage.code='outsource:manage';
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT grant_row.role_id, settings.id
                FROM role_permissions grant_row
                INNER JOIN permissions old ON old.id=grant_row.permission_id AND old.code='outsource:field_settings'
                CROSS JOIN permissions settings
                WHERE settings.code='outsource:settings';
                """);
            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE p.code IN ('outsource:view', 'outsource:import', 'outsource:field_settings');
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code IN ('outsource:view', 'outsource:import', 'outsource:field_settings');");
            migrationBuilder.Sql("UPDATE permissions SET name='管理外协清单' WHERE code='outsource:manage';");
            migrationBuilder.Sql("UPDATE permissions SET name='外协清单设定' WHERE code='outsource:settings';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Like the other catalog migrations, Down leaves reference data alone: the retired codes, their grants and
            // the old names are not reconstructed.
        }
    }
}
