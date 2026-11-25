using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameOutsourceHandoffLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE outsource_fields SET label='转迅得研发电控设计'
                WHERE field_key='electricalHandoff' AND built_in=1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE outsource_fields SET label='是否转迅得电控'
                WHERE field_key='electricalHandoff' AND built_in=1;
                """);
        }
    }
}
