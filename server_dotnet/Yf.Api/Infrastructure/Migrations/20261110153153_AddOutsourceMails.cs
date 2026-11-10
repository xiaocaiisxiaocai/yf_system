using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsourceMails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_sent_at",
                table: "outsource_items",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_sent_snapshot",
                table: "outsource_items",
                type: "text",
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<ulong>(
                name: "notified_vendor_id",
                table: "outsource_items",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outsource_mail_batches",
                columns: table => new
                {
                    request_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_mail_batches", x => x.request_key);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_mails",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    batch_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    vendor_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    subject = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    body_html = table.Column<string>(type: "mediumtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    body_text = table.Column<string>(type: "mediumtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attachment_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attachment = table.Column<byte[]>(type: "mediumblob", nullable: true),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    last_error = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn),
                    sent_at = table.Column<DateTime>(type: "datetime(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_mails", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_mails_batch",
                        column: x => x.batch_key,
                        principalTable: "outsource_mail_batches",
                        principalColumn: "request_key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outsource_mails_vendor",
                        column: x => x.vendor_id,
                        principalTable: "outsource_vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_mail_items",
                columns: table => new
                {
                    mail_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    item_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    work_order_no = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_mail_items", x => new { x.mail_id, x.item_id });
                    table.ForeignKey(
                        name: "fk_outsource_mail_items_mail",
                        column: x => x.mail_id,
                        principalTable: "outsource_mails",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "outsource_mail_recipients",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    mail_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    kind = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    email = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    source = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    rejected = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outsource_mail_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_outsource_mail_recipients_mail",
                        column: x => x.mail_id,
                        principalTable: "outsource_mails",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_mail_items_item",
                table: "outsource_mail_items",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_mail_recipients_mail",
                table: "outsource_mail_recipients",
                columns: new[] { "mail_id", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_outsource_mails_batch",
                table: "outsource_mails",
                column: "batch_key");

            migrationBuilder.CreateIndex(
                name: "idx_outsource_mails_queue",
                table: "outsource_mails",
                columns: new[] { "status", "next_attempt_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_outsource_mails_vendor",
                table: "outsource_mails",
                columns: new[] { "vendor_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outsource_mail_items");

            migrationBuilder.DropTable(
                name: "outsource_mail_recipients");

            migrationBuilder.DropTable(
                name: "outsource_mails");

            migrationBuilder.DropTable(
                name: "outsource_mail_batches");

            migrationBuilder.DropColumn(
                name: "last_sent_at",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "last_sent_snapshot",
                table: "outsource_items");

            migrationBuilder.DropColumn(
                name: "notified_vendor_id",
                table: "outsource_items");
        }
    }
}
