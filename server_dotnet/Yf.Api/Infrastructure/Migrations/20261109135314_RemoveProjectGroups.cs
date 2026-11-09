using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProjectGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Projects become a single level: the main project's data already lives on every project row.
            // Carry what lives only on the main project (Robot owner contacts, completion time) to its projects first.
            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "projects",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_robot_owners",
                columns: table => new
                {
                    project_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    contact_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_robot_owners", x => new { x.project_id, x.contact_id });
                    table.ForeignKey(
                        name: "fk_project_robot_owners_contact",
                        column: x => x.contact_id,
                        principalTable: "supplier_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_robot_owners_project",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_project_robot_owners_contact",
                table: "project_robot_owners",
                column: "contact_id");

            migrationBuilder.Sql("""
                INSERT INTO project_robot_owners (project_id, contact_id, sort_no)
                SELECT p.id, o.contact_id, o.sort_no
                FROM projects p
                INNER JOIN project_group_robot_owners o ON o.project_group_id = p.project_group_id;
                """);
            // Assigning updated_at to itself keeps its ON UPDATE CURRENT_TIMESTAMP from firing.
            migrationBuilder.Sql("""
                UPDATE projects p
                SET p.completed_at = (SELECT MAX(l.created_at) FROM project_status_logs l
                                      WHERE l.project_id = p.id AND l.to_status = 'COMPLETED'),
                    p.updated_at = p.updated_at
                WHERE p.status = 'COMPLETED';
                """);
            // Drafts no longer exist: a project starts when it is created.
            migrationBuilder.Sql("""
                UPDATE projects SET status = 'TERMINATED', updated_at = updated_at WHERE status = 'DRAFT';
                """);
            // Every internal account sees every project, so the view-all grant is retired.
            migrationBuilder.Sql("""
                DELETE rp FROM role_permissions rp
                INNER JOIN permissions p ON p.id = rp.permission_id
                WHERE p.code = 'project:view_all';
                """);
            migrationBuilder.Sql("DELETE FROM permissions WHERE code = 'project:view_all';");

            migrationBuilder.DropForeignKey(
                name: "fk_project_copy_jobs_group",
                table: "project_copy_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_group",
                table: "projects");

            migrationBuilder.DropTable(
                name: "project_group_robot_owners");

            migrationBuilder.DropTable(
                name: "project_group_status_logs");

            migrationBuilder.DropTable(
                name: "project_group_work_orders");

            migrationBuilder.DropTable(
                name: "project_groups");

            migrationBuilder.DropIndex(
                name: "idx_projects_group",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "idx_project_copy_jobs_group_actor_status_time",
                table: "project_copy_jobs");

            migrationBuilder.DropColumn(
                name: "project_group_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "project_group_id",
                table: "project_copy_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "projects",
                type: "varchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "IN_PROGRESS",
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(24)",
                oldMaxLength: 24,
                oldDefaultValue: "DRAFT")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_project_copy_jobs_requester",
                table: "project_copy_jobs",
                columns: new[] { "requested_by", "status", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_project_copy_jobs_requester",
                table: "project_copy_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "projects",
                type: "varchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "DRAFT",
                collation: "utf8mb4_unicode_ci",
                oldClrType: typeof(string),
                oldType: "varchar(24)",
                oldMaxLength: 24,
                oldDefaultValue: "IN_PROGRESS")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.AddColumn<ulong>(
                name: "project_group_id",
                table: "projects",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<ulong>(
                name: "project_group_id",
                table: "project_copy_jobs",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.CreateTable(
                name: "project_groups",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    completed_at = table.Column<DateTime>(type: "datetime(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    created_by = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    description = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    expected_completion_date = table.Column<DateOnly>(type: "date", nullable: true),
                    legacy_robot_model_name = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    machine_model = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    priority_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    responsible_user_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    robot_owner_name = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    robot_part_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    robot_type_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    section_id = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, defaultValue: "DRAFT", collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    supplier_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_groups_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_priority",
                        column: x => x.priority_id,
                        principalTable: "project_dictionaries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_responsible_user",
                        column: x => x.responsible_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_robot_part",
                        column: x => x.robot_part_id,
                        principalTable: "robot_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_robot_type",
                        column: x => x.robot_type_id,
                        principalTable: "project_dictionaries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_section",
                        column: x => x.section_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_groups_supplier",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "project_group_robot_owners",
                columns: table => new
                {
                    project_group_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    contact_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group_robot_owners", x => new { x.project_group_id, x.contact_id });
                    table.ForeignKey(
                        name: "fk_project_group_robot_owners_contact",
                        column: x => x.contact_id,
                        principalTable: "supplier_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_group_robot_owners_group",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "project_group_status_logs",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    action = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false),
                    from_status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    operator_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    project_group_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    to_status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    trigger_project_id = table.Column<ulong>(type: "bigint unsigned", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group_status_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_group_status_logs_group",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_group_status_logs_operator",
                        column: x => x.operator_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_group_status_logs_project",
                        column: x => x.trigger_project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "project_group_work_orders",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                    project_group_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    work_order_no = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_group_work_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_group_work_orders_group",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "idx_projects_group",
                table: "projects",
                columns: new[] { "project_group_id", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_project_copy_jobs_group_actor_status_time",
                table: "project_copy_jobs",
                columns: new[] { "project_group_id", "requested_by", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_project_group_robot_owners_contact",
                table: "project_group_robot_owners",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_group_status_logs_group_time",
                table: "project_group_status_logs",
                columns: new[] { "project_group_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_project_group_status_logs_operator",
                table: "project_group_status_logs",
                column: "operator_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_group_status_logs_project",
                table: "project_group_status_logs",
                column: "trigger_project_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_group_work_orders_group_sort",
                table: "project_group_work_orders",
                columns: new[] { "project_group_id", "sort_no", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_project_group_work_orders_group_no",
                table: "project_group_work_orders",
                columns: new[] { "project_group_id", "work_order_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "fk_project_groups_created_by",
                table: "project_groups",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "fk_project_groups_priority",
                table: "project_groups",
                column: "priority_id");

            migrationBuilder.CreateIndex(
                name: "fk_project_groups_robot_type",
                table: "project_groups",
                column: "robot_type_id");

            migrationBuilder.CreateIndex(
                name: "fk_project_groups_section",
                table: "project_groups",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_groups_expected_completion",
                table: "project_groups",
                column: "expected_completion_date");

            migrationBuilder.CreateIndex(
                name: "idx_project_groups_responsible_user",
                table: "project_groups",
                column: "responsible_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_groups_robot_part",
                table: "project_groups",
                column: "robot_part_id");

            migrationBuilder.CreateIndex(
                name: "idx_project_groups_status",
                table: "project_groups",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_project_groups_supplier",
                table: "project_groups",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "uk_project_groups_name",
                table: "project_groups",
                column: "name",
                unique: true);

            // Rebuild one main project per project (same id) so the restored foreign keys hold.
            migrationBuilder.Sql("""
                INSERT INTO project_groups (id, name, description, supplier_id, status, created_by, machine_model,
                    robot_part_id, legacy_robot_model_name, responsible_user_id, robot_owner_name, section_id,
                    priority_id, robot_type_id, expected_completion_date, completed_at, created_at, updated_at)
                SELECT id, name, description, supplier_id, status, created_by, machine_model, robot_part_id,
                    legacy_robot_model_name, responsible_user_id, robot_owner_name, section_id, priority_id,
                    robot_type_id, expected_completion_date, completed_at, created_at, updated_at
                FROM projects;
                """);
            migrationBuilder.Sql("UPDATE projects SET project_group_id = id, updated_at = updated_at;");
            migrationBuilder.Sql("""
                UPDATE project_copy_jobs j
                INNER JOIN projects p ON p.id = j.source_project_id
                SET j.project_group_id = p.project_group_id;
                """);
            migrationBuilder.Sql("""
                INSERT INTO project_group_work_orders (project_group_id, work_order_no, sort_no, created_at)
                SELECT project_id, work_order_no, sort_no, created_at FROM project_work_orders;
                """);
            migrationBuilder.Sql("""
                INSERT INTO project_group_robot_owners (project_group_id, contact_id, sort_no)
                SELECT project_id, contact_id, sort_no FROM project_robot_owners;
                """);
            // Like RetireAcceptanceWorkflow, the retired project:view_all grant is not brought back.

            migrationBuilder.DropTable(
                name: "project_robot_owners");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "projects");

            migrationBuilder.AddForeignKey(
                name: "fk_project_copy_jobs_group",
                table: "project_copy_jobs",
                column: "project_group_id",
                principalTable: "project_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_group",
                table: "projects",
                column: "project_group_id",
                principalTable: "project_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
