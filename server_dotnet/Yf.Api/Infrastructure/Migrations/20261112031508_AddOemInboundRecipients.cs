using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOemInboundRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oem_company_recipient_exclusions",
                columns: table => new
                {
                    oem_company_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_company_recipient_exclusions", x => new { x.oem_company_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_oem_company_recipient_exclusions_company",
                        column: x => x.oem_company_id,
                        principalTable: "oem_companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_oem_company_recipient_exclusions_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_internal_recipient_exclusions",
                columns: table => new
                {
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_internal_recipient_exclusions", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_oem_internal_recipient_exclusions_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "oem_transfer_internal_recipients",
                columns: table => new
                {
                    transfer_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oem_transfer_internal_recipients", x => new { x.transfer_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_oem_transfer_internal_recipients_transfer",
                        column: x => x.transfer_id,
                        principalTable: "oem_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_oem_transfer_internal_recipients_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_oem_company_recipient_exclusions_user",
                table: "oem_company_recipient_exclusions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_oem_transfer_internal_recipients_user",
                table: "oem_transfer_internal_recipients",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oem_company_recipient_exclusions");

            migrationBuilder.DropTable(
                name: "oem_internal_recipient_exclusions");

            migrationBuilder.DropTable(
                name: "oem_transfer_internal_recipients");
        }
    }
}
