using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>
    /// 是否有RobotV leaves the list editors (外协清单契约 → 栏位设定, 2026-11-20): its editor setting becomes 内部不填写,
    /// which from now on holds for system administrators too. Administrators can open it again on the field settings page.
    /// </summary>
    public partial class StopInternalRobotVEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE r FROM outsource_field_editor_roles r
                INNER JOIN outsource_fields f ON f.id=r.field_id
                WHERE f.field_key='hasRobotV';
                """);
            migrationBuilder.Sql("""
                DELETE u FROM outsource_field_editor_users u
                INNER JOIN outsource_fields f ON f.id=u.field_id
                WHERE f.field_key='hasRobotV';
                """);
            migrationBuilder.Sql("UPDATE outsource_fields SET editor_scope='NONE' WHERE field_key='hasRobotV';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Field settings are reference data an administrator may have changed since: a rollback leaves them as they are.
        }
    }
}
