using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UseOemCompaniesAsOutsourceVendors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_outsource_items_vendor",
                table: "outsource_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outsource_mails_vendor",
                table: "outsource_mails");

            migrationBuilder.DropForeignKey(
                name: "fk_outsource_type_vendors_vendor",
                table: "outsource_type_vendors");

            // Vendor ids now point at oem_companies. The outsourcing module was not in use yet, so its vendor-bound
            // rows are cleared instead of being mapped (types, the default copy list and the IPMS settings stay).
            migrationBuilder.Sql("""
                DELETE FROM outsource_mail_items;
                DELETE FROM outsource_mail_recipients;
                DELETE FROM outsource_mails;
                DELETE FROM outsource_mail_batches;
                DELETE FROM outsource_item_changes;
                DELETE FROM outsource_items;
                DELETE FROM outsource_type_vendors;
                """);

            migrationBuilder.DropTable(
                name: "outsource_vendor_contacts");

            migrationBuilder.DropTable(
                name: "outsource_vendors");

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_items_vendor",
                table: "outsource_items",
                column: "vendor_id",
                principalTable: "oem_companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_mails_vendor",
                table: "outsource_mails",
                column: "vendor_id",
                principalTable: "oem_companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_type_vendors_vendor",
                table: "outsource_type_vendors",
                column: "vendor_id",
                principalTable: "oem_companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_outsource_items_vendor",
                table: "outsource_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outsource_mails_vendor",
                table: "outsource_mails");

            migrationBuilder.DropForeignKey(
                name: "fk_outsource_type_vendors_vendor",
                table: "outsource_type_vendors");

            // The old vendor tables come back empty, so rows that point at oem_companies cannot be kept.
            migrationBuilder.Sql("""
                DELETE FROM outsource_mail_items;
                DELETE FROM outsource_mail_recipients;
                DELETE FROM outsource_mails;
                DELETE FROM outsource_mail_batches;
                DELETE FROM outsource_item_changes;
                DELETE FROM outsource_items;
                DELETE FROM outsource_type_vendors;
                """);

            migrationBuilder.CreateTable(
                name: "outsource_vendors",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remark = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_vendors", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_vendor_contacts",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_vendor_contacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_vendor_contacts_vendor",
                        column: x => x.vendor_id,
                        principalTable: "outsource_vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_vendor_contacts_vendor_status",
                table: "outsource_vendor_contacts",
                columns: new[] { "vendor_id", "status", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_outsource_vendor_contacts_vendor_email",
                table: "outsource_vendor_contacts",
                columns: new[] { "vendor_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outsource_vendors_status_sort",
                table: "outsource_vendors",
                columns: new[] { "status", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_outsource_vendors_name",
                table: "outsource_vendors",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_items_vendor",
                table: "outsource_items",
                column: "vendor_id",
                principalTable: "outsource_vendors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_mails_vendor",
                table: "outsource_mails",
                column: "vendor_id",
                principalTable: "outsource_vendors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outsource_type_vendors_vendor",
                table: "outsource_type_vendors",
                column: "vendor_id",
                principalTable: "outsource_vendors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
