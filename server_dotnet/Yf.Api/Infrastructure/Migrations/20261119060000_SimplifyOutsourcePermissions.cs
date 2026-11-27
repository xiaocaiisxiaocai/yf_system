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
            // Down migrations retain permission selections. A database that already acquired
            // the later split actions must keep its independent view/import grants on re-upgrade.
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT grant_row.role_id, manage.id
                FROM role_permissions grant_row
                INNER JOIN permissions old ON old.id=grant_row.permission_id AND old.code='outsource:import'
                CROSS JOIN permissions manage
                WHERE manage.code='outsource:manage'
                  AND NOT EXISTS (SELECT 1 FROM permissions marker WHERE marker.code='outsource:delete');
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
                LEFT JOIN permissions marker ON marker.code='outsource:delete'
                WHERE p.code='outsource:field_settings'
                   OR (marker.id IS NULL AND p.code IN ('outsource:view','outsource:import'));
                """);
            migrationBuilder.Sql("""
                DELETE p FROM permissions p
                LEFT JOIN permissions marker ON marker.code='outsource:delete'
                WHERE p.code='outsource:field_settings'
                   OR (marker.id IS NULL AND p.code IN ('outsource:view','outsource:import'));
                """);
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
