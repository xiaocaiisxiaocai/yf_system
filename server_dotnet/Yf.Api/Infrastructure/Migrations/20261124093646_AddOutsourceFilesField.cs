using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceFilesField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use the existing gap where possible. On customised layouts, open a gap while keeping the (sort_no, id) order.
            migrationBuilder.Sql("""
                UPDATE outsource_fields AS field
                JOIN outsource_fields AS work_order ON work_order.field_key='workOrderNo'
                JOIN outsource_fields AS successor ON successor.sort_no=work_order.sort_no+1
                    OR (successor.sort_no=work_order.sort_no AND successor.id>work_order.id)
                SET field.sort_no=field.sort_no+2
                WHERE field.sort_no>work_order.sort_no
                    OR (field.sort_no=work_order.sort_no AND field.id>work_order.id);
                """);
            migrationBuilder.Sql("""
                INSERT INTO outsource_fields(field_key,built_in,label,data_type,sort_no,status,vendor_visible,vendor_editable,editor_scope)
                SELECT 'files',1,'档案','TEXT',sort_no+1,'ACTIVE',1,0,'NONE'
                FROM outsource_fields WHERE field_key='workOrderNo';
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM outsource_fields WHERE field_key='files';");

        }
    }
}
