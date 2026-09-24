using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadFingerprintsSharedBlobsAndIdentityRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "file_fingerprint",
                table: "upload_sessions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "file_last_modified",
                table: "upload_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<ulong>(
                name: "blob_id",
                table: "files",
                type: "bigint unsigned",
                nullable: true);

            // Previously started uploads are never matched by the new fingerprint protocol.
            migrationBuilder.Sql("UPDATE upload_sessions SET file_fingerprint=LOWER(SHA2(CONCAT('retired-upload:',id),256))");
            migrationBuilder.Sql("""
                INSERT INTO system_configs(cfg_key,cfg_value,description,updated_at)
                VALUES('security.identity_revision','0','Internal identity cache revision',UTC_TIMESTAMP(3))
                """);
            foreach (var table in new[] { "users", "suppliers", "refresh_tokens" })
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_identity_{table}_{operation.ToLowerInvariant()}
                    AFTER {operation} ON {table} FOR EACH ROW
                    UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                    WHERE cfg_key='security.identity_revision'
                    """);

            migrationBuilder.CreateTable(
                name: "file_blobs",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false, collation: "ascii_bin"),
                    size_bytes = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    storage_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "ascii_bin"),
                    state = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "READY", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    gc_started_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_blobs", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_us_resume",
                table: "upload_sessions",
                columns: new[] { "project_id", "uploader_id", "file_fingerprint", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "idx_files_blob",
                table: "files",
                column: "blob_id");

            migrationBuilder.CreateIndex(
                name: "idx_file_blobs_state",
                table: "file_blobs",
                columns: new[] { "state", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_file_blobs_sha256",
                table: "file_blobs",
                column: "sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_file_blobs_storage_path",
                table: "file_blobs",
                column: "storage_path",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_files_blob",
                table: "files",
                column: "blob_id",
                principalTable: "file_blobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "users", "suppliers", "refresh_tokens" })
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                migrationBuilder.Sql($"DROP TRIGGER trg_identity_{table}_{operation.ToLowerInvariant()}");
            migrationBuilder.Sql("DELETE FROM system_configs WHERE cfg_key='security.identity_revision'");
            migrationBuilder.DropForeignKey(
                name: "fk_files_blob",
                table: "files");

            migrationBuilder.DropTable(
                name: "file_blobs");

            migrationBuilder.DropIndex(
                name: "idx_us_resume",
                table: "upload_sessions");

            migrationBuilder.DropIndex(
                name: "idx_files_blob",
                table: "files");

            migrationBuilder.DropColumn(
                name: "file_fingerprint",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "file_last_modified",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "blob_id",
                table: "files");
        }
    }
}
