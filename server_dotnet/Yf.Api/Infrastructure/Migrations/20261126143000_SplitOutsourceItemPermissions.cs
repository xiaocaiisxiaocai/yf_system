using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations;

public partial class SplitOutsourceItemPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Migrations precede the catalog on an empty installation. On upgrade, keep the set of
        // newly introduced codes so a subsequent Down/Up never restores deliberately revoked grants.
        // LIKE takes the actual code column's charset and collation from the installed catalog.
        // A table inferred from string literals instead takes the connection's collation on MySQL 5.7.
        migrationBuilder.Sql("""
            DROP TEMPORARY TABLE IF EXISTS yf_new_outsource_actions;
            CREATE TEMPORARY TABLE yf_new_outsource_actions LIKE permissions;
            INSERT INTO yf_new_outsource_actions(code,name,type,sort_no) VALUES
                ('outsource:view','查看外发工令','ACTION',82),
                ('outsource:export','导出外发工令','ACTION',83),
                ('outsource:import','导入外发工令','ACTION',84),
                ('outsource:delete','删除外发工令','ACTION',85);

            DELETE added FROM yf_new_outsource_actions added
            JOIN permissions existing ON existing.code=added.code;
            UPDATE yf_new_outsource_actions
            SET parent_id=(SELECT id FROM permissions WHERE code='outsource');

            INSERT INTO permissions(code,name,type,parent_id,sort_no)
            SELECT code,name,type,parent_id,sort_no FROM yf_new_outsource_actions WHERE parent_id IS NOT NULL;
            """);

        // A role with no grants could still read/export when assigned to an employee. Unassigned
        // roles are preserved only when their existing grants or field assignments establish an
        // internal outsourcing purpose. A supplier-bound role must keep its supplier allow-list.
        migrationBuilder.Sql("""
            INSERT IGNORE INTO role_permissions(role_id,permission_id)
            SELECT r.id, target.id
            FROM roles r
            CROSS JOIN yf_new_outsource_actions new_action
            JOIN permissions target ON target.code=new_action.code
                OR (new_action.code='outsource:view' AND target.id=new_action.parent_id)
            WHERE NOT (r.is_built_in=1 AND r.name='供应商人员')
              AND NOT EXISTS (
                  SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id
                  WHERE ur.role_id=r.id AND u.user_type='SUPPLIER'
              )
              AND (
                  EXISTS (
                      SELECT 1 FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
                      WHERE rp.role_id=r.id AND p.code='outsource:manage'
                  )
                  OR (new_action.code IN ('outsource:view','outsource:export') AND (
                      (r.is_built_in=1 AND r.name='系统管理员')
                      OR EXISTS (
                          SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id
                          WHERE ur.role_id=r.id AND u.user_type='INTERNAL'
                      )
                      OR EXISTS (
                          SELECT 1 FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
                          WHERE rp.role_id=r.id AND p.code IN
                              ('outsource','outsource:send','outsource:settings','outsource:date_import','outsource:file_view')
                      )
                      OR EXISTS (
                          SELECT 1 FROM outsource_field_editor_roles er JOIN outsource_fields f ON f.id=er.field_id
                          WHERE er.role_id=r.id AND f.editor_scope='ROLES' AND f.status='ACTIVE'
                      )
                  ))
              );

            DROP TEMPORARY TABLE yf_new_outsource_actions;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Earlier application versions ignore the separate actions. Preserve exact role selections.
    }
}
