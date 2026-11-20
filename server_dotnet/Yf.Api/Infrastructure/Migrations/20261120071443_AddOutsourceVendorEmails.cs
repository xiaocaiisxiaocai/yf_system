using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <summary>Outsourcing-list mail addresses per vendor, apart from the ODM/OEMA contacts (2026-11-20).</summary>
    public partial class AddOutsourceVendorEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outsource_vendor_emails",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_vendor_emails", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_vendor_emails_vendor",
                        column: x => x.vendor_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "uk_outsource_vendor_emails_vendor_email",
                table: "outsource_vendor_emails",
                columns: new[] { "vendor_id", "email" },
                unique: true);

            // Outsourcing mail used to go to the ODM/OEMA contacts: start each vendor's list with its active contact
            // addresses, so nothing changes until purchasing edits it (外协清单契约 → 外协邮箱).
            migrationBuilder.Sql("""
                INSERT IGNORE INTO outsource_vendor_emails(vendor_id,email,created_by,created_at,updated_at)
                SELECT c.oem_company_id, TRIM(c.email), NULL, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                FROM oem_company_contacts c
                WHERE c.status='ACTIVE' AND c.email IS NOT NULL AND TRIM(c.email)<>''
                ORDER BY c.oem_company_id, c.sort_no, c.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outsource_vendor_emails");
        }
    }
}
