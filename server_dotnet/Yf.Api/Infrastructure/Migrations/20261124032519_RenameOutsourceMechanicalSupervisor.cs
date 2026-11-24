using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameOutsourceMechanicalSupervisor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE outsource_fields SET label='上级主管'
                WHERE field_key='mechanicalSection' AND built_in=1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE outsource_fields SET label='机构课别'
                WHERE field_key='mechanicalSection' AND built_in=1;
                """);
        }
    }
}
