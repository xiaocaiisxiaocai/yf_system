using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SplitOutsourceSendPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Editing and mailing vendors become separate permissions (2026-11-19). Insert by code, no fixed ID
            // (see AddDataScopesAndCrudPermissions); an empty database gets the catalog row from BootstrapSeedCatalog.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT 'outsource:send','发送外协清单邮件','ACTION',parent.id,79
                FROM (SELECT 1 AS one) seed
                LEFT JOIN permissions parent ON parent.code='outsource'
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code='outsource:send');
                """);
            migrationBuilder.Sql("UPDATE permissions SET name='编辑外协清单' WHERE code='outsource:manage';");
            // Keep today's ability: every role that could edit and send (outsource:manage) can still send.
            // Administrators remove the send grant from roles that should only edit afterwards.
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT grant_row.role_id, send.id
                FROM role_permissions grant_row
                INNER JOIN permissions manage ON manage.id=grant_row.permission_id AND manage.code='outsource:manage'
                CROSS JOIN permissions send
                WHERE send.code='outsource:send';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keeps the outsource:send permission, its grants and the new outsource:manage name: reference data survives a rollback.
        }
    }
}
