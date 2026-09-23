using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCopyJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_copy_jobs",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    source_project_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    project_group_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    requested_by = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    idempotency_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    target_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    request_ip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    execution_token = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    worker_epoch = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    files_total = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    files_copied = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    bytes_total = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    bytes_copied = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    error = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    result_project_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    result_copy_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    result_copy_file_count = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_copy_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_copy_jobs_group",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_copy_jobs_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_copy_jobs_result_copy",
                        column: x => x.result_copy_id,
                        principalTable: "project_copies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_copy_jobs_result_project",
                        column: x => x.result_project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_copy_jobs_source",
                        column: x => x.source_project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "project_copy_worker_state",
                columns: table => new
                {
                    id = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    epoch = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_project_copy_worker_state", x => x.id))
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "project_copy_worker_state",
                columns: new[] { "id", "epoch" },
                values: new object[] { (byte)1, 0UL });

            migrationBuilder.CreateIndex(
                name: "idx_project_copy_jobs_group_actor_status_time",
                table: "project_copy_jobs",
                columns: new[] { "project_group_id", "requested_by", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_project_copy_jobs_queue",
                table: "project_copy_jobs",
                columns: new[] { "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_copy_jobs_result_copy_id",
                table: "project_copy_jobs",
                column: "result_copy_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_copy_jobs_result_project_id",
                table: "project_copy_jobs",
                column: "result_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_copy_jobs_source_project_id",
                table: "project_copy_jobs",
                column: "source_project_id");

            migrationBuilder.CreateIndex(
                name: "uk_project_copy_jobs_actor_key",
                table: "project_copy_jobs",
                columns: new[] { "requested_by", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_copy_jobs");
            migrationBuilder.DropTable(
                name: "project_copy_worker_state");
        }
    }
}
