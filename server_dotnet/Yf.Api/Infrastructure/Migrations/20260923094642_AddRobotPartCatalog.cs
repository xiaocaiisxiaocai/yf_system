using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations;

/// <summary>
/// Replaces the legacy robot dictionaries with a supplier-owned part catalog. The fixed seed is
/// an immutable snapshot of Data/robot-parts-20260923.json (JSON SHA-256
/// daced1ae638796f5a4f097b9e1025834ab6abe409266d20096f0ff73d26de384; source workbook SHA-256
/// aa2256021de680a4b03c8bff32d9df2684ef56d5b47efadb86c47583429b11ab).
/// </summary>
public partial class AddRobotPartCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Fail before persistent DDL when an existing supplier only matches a catalog brand under
        // the database's accent/case-insensitive collation. Reusing it would violate exact-name ownership,
        // while inserting the intended spelling would later collide with uk_suppliers_name.
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE yf_robot_supplier_seed_guard (marker INT NOT NULL PRIMARY KEY);
            INSERT INTO yf_robot_supplier_seed_guard (marker) VALUES (1);
            INSERT INTO yf_robot_supplier_seed_guard (marker)
            SELECT 1
            FROM suppliers AS s
            INNER JOIN (
                SELECT '發那科' AS name UNION ALL SELECT '海康' UNION ALL SELECT '珞石'
                UNION ALL SELECT '川崎' UNION ALL SELECT '安川' UNION ALL SELECT '埃斯顿'
                UNION ALL SELECT '匯川'
            ) AS seed ON s.name = seed.name
            WHERE BINARY s.name <> BINARY seed.name
            LIMIT 1;
            DROP TEMPORARY TABLE yf_robot_supplier_seed_guard;
            """);

        migrationBuilder.AddColumn<string>(name: "legacy_robot_model_name", table: "projects",
            type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
            .Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.AddColumn<string>(name: "legacy_robot_model_name", table: "project_groups",
            type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_unicode_ci")
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.Sql("""
            UPDATE projects AS p
            INNER JOIN project_dictionaries AS d ON d.id = p.robot_model_id AND d.type = 'ROBOT_MODEL'
            SET p.legacy_robot_model_name = d.name, p.updated_at = p.updated_at
            WHERE p.robot_model_id IS NOT NULL;
            UPDATE project_groups AS g
            INNER JOIN project_dictionaries AS d ON d.id = g.robot_model_id AND d.type = 'ROBOT_MODEL'
            SET g.legacy_robot_model_name = d.name, g.updated_at = g.updated_at
            WHERE g.robot_model_id IS NOT NULL;
            """);

        migrationBuilder.DropForeignKey(name: "fk_project_groups_robot_model", table: "project_groups");
        migrationBuilder.DropForeignKey(name: "fk_project_groups_robot_vendor", table: "project_groups");
        migrationBuilder.DropForeignKey(name: "fk_projects_robot_model", table: "projects");
        migrationBuilder.DropForeignKey(name: "fk_projects_robot_vendor", table: "projects");
        migrationBuilder.DropIndex(name: "fk_project_groups_robot_model", table: "project_groups");
        migrationBuilder.DropIndex(name: "fk_project_groups_robot_vendor", table: "project_groups");
        migrationBuilder.DropIndex(name: "idx_projects_robot_model", table: "projects");
        migrationBuilder.DropIndex(name: "idx_projects_robot_vendor", table: "projects");
        migrationBuilder.DropColumn(name: "robot_model_id", table: "project_groups");
        migrationBuilder.DropColumn(name: "robot_vendor_id", table: "project_groups");
        migrationBuilder.DropColumn(name: "robot_model_id", table: "projects");
        migrationBuilder.DropColumn(name: "robot_vendor_id", table: "projects");

        migrationBuilder.CreateTable(
            name: "robot_parts",
            columns: table => new
            {
                id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                supplier_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                part_number = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_unicode_ci")
                    .Annotation("MySql:CharSet", "utf8mb4"),
                model = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_unicode_ci")
                    .Annotation("MySql:CharSet", "utf8mb4"),
                sort_no = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "ACTIVE", collation: "utf8mb4_unicode_ci")
                    .Annotation("MySql:CharSet", "utf8mb4"),
                created_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)"),
                updated_at = table.Column<DateTime>(type: "datetime(3)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(3)")
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_robot_parts", x => x.id);
                table.ForeignKey(name: "fk_robot_parts_supplier", column: x => x.supplier_id,
                    principalTable: "suppliers", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySql:CharSet", "utf8mb4")
            .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

        migrationBuilder.AddColumn<ulong>(name: "robot_part_id", table: "project_groups",
            type: "bigint unsigned", nullable: true);
        migrationBuilder.AddColumn<ulong>(name: "robot_part_id", table: "projects",
            type: "bigint unsigned", nullable: true);
        migrationBuilder.CreateIndex(name: "idx_project_groups_robot_part", table: "project_groups", column: "robot_part_id");
        migrationBuilder.CreateIndex(name: "idx_projects_robot_part", table: "projects", column: "robot_part_id");
        migrationBuilder.CreateIndex(name: "idx_robot_parts_supplier_status_sort", table: "robot_parts",
            columns: new[] { "supplier_id", "status", "sort_no", "id" });
        migrationBuilder.CreateIndex(name: "uk_robot_parts_supplier_part_number", table: "robot_parts",
            columns: new[] { "supplier_id", "part_number" }, unique: true);

        migrationBuilder.Sql("""
            INSERT INTO suppliers (name, remark, status, created_by, created_at, updated_at)
            SELECT seed.name, 'Robot 料号目录初始化（2026-09-23）', 'ACTIVE', NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM (
                SELECT '發那科' AS name UNION ALL SELECT '海康' UNION ALL SELECT '珞石'
                UNION ALL SELECT '川崎' UNION ALL SELECT '安川' UNION ALL SELECT '埃斯顿'
                UNION ALL SELECT '匯川'
            ) AS seed
            WHERE NOT EXISTS (SELECT 1 FROM suppliers AS s WHERE BINARY s.name = BINARY seed.name);

            INSERT INTO robot_parts (supplier_id, part_number, model, sort_no, status, created_at, updated_at)
            SELECT s.id, seed.part_number, seed.model, seed.sort_no, 'ACTIVE', CURRENT_TIMESTAMP(3), CURRENT_TIMESTAMP(3)
            FROM (
                SELECT '發那科' AS supplier_name, 'C-T200ID7LA' AS part_number, '發那科六軸機械人200iD/7L-7KG(無CC-LINK)' AS model, 1 AS sort_no
                UNION ALL SELECT '發那科', 'C-TM10ID12A', '發那科六軸機器人M-10iD-12KG(無CC-LINK)', 2
                UNION ALL SELECT '發那科', 'C-TM20ID25A', '發那科六軸機器人M-20iD-25KG(無CC-LINK)', 3
                UNION ALL SELECT '發那科', 'C-TM20ID35A', '發那科六軸機器人M-20iD-35KG(無CC-LINK)', 4
                UNION ALL SELECT '發那科', 'C-TM20ID35AA', '發那科六軸機器人M-20iD-35KG(無CC-LINK)配A柜', 5
                UNION ALL SELECT '海康', 'C-THAR15144015AA', '海康六軸機器人 HAR15-1440-15KG(無CC-LINK)統一為緊湊型電櫃', 6
                UNION ALL SELECT '海康', 'C-THAR251830A', '海康六軸機器人 HAR25-1830-25KG(無CC-LINK)只有標準大電櫃', 7
                UNION ALL SELECT '海康', 'C-THAR351830A', '海康六軸機器人 HAR35-1830-35KG(無CC-LINK)只有標準大電櫃', 8
                UNION ALL SELECT '海康', 'C-THAR251830S', '海康HAR251830-S-25KG-3PH220V無CCLINK只有標準大電櫃', 9
                UNION ALL SELECT '珞石', 'C-TXB7LR9066B', '珞石六軸機器人XB7L-7KG-R906-6(無CC-LINK)重載連接器彎頭', 10
                UNION ALL SELECT '珞石', 'C-TNB1215A', '珞石六軸機器人NB12-15KG-1.4米臂展(無CC-LINK)', 11
                UNION ALL SELECT '珞石', 'C-TXB25A', '珞石六軸機器人升級成NB25-30KG-1.6米臂展(無CC-LINK)', 12
                UNION ALL SELECT '珞石', 'C-TNB252518A', '珞石六軸機器人NB25-25KG-1.8米臂展(無CC-LINK)', 13
                UNION ALL SELECT '珞石', 'C-TNB35A', '珞石六軸機器人NB25-35KG-1.8米臂展(無CC-LINK)', 14
                UNION ALL SELECT '珞石', 'C-TNB25H2518', '珞石六軸機器人NB25h-2518(25KG)1.8米臂展無CC-LINK中空軸', 15
                UNION ALL SELECT '珞石', 'C-TNB12H1514', '珞石六軸機器人NB12h-1514(15KG)1.4米臂展無CC-LINK中空軸', 16
                UNION ALL SELECT '珞石', 'C-TNB101009A', '珞石六軸機器人NB10-10KG-0.9米臂展-折彎線-無CC-LINK', 17
                UNION ALL SELECT '珞石', 'C-TNB101009', '珞石六軸機器人NB10-10KG-0.9米臂展-直出線-無CC-LINK', 18
                UNION ALL SELECT '川崎', 'C-TRS013N', '川崎六軸機器人 RS013N-13KG-1.4米臂展(CC-LINK)', 19
                UNION ALL SELECT '川崎', 'C-TRS020N', '川崎六軸機器人 RS020N-20KG-1.7米臂展(CC-LINK)', 20
                UNION ALL SELECT '川崎', 'C-TRS025N', '川崎六軸機器人 RS025N-25KG-1.8米臂展(CC-LINK)', 21
                UNION ALL SELECT '川崎', 'C-TRS025NA', '川崎六軸RS025N-25KG-1.8米(CC-LINK無示教器，需下堵頭)僅越南健鼎用', 22
                UNION ALL SELECT '安川', 'C-TGP251730', '安川六軸機械手GP25-25KG-1730臂展', 23
                UNION ALL SELECT '埃斯顿', 'C-TER151430MI', '埃斯顿六軸機器人 ER15-1430-15KG-MI 潔淨型(無CC-LINK)', 24
                UNION ALL SELECT '埃斯顿', 'C-TER20B1760HI', '埃斯顿六軸機器人 ER20B-1760-20KG-HI 立式電櫃(無CC-LINK)', 25
                UNION ALL SELECT '匯川', 'C-TIRCR10140S5', '匯川六軸機器人IR-CR10-140S5-10KG', 26
                UNION ALL SELECT '匯川', 'C-TIRR35185S7', '匯川六軸機器人IR-R35-185-S7-35KG(無CC-LINK和變壓器)', 27
            ) AS seed
            INNER JOIN suppliers AS s ON BINARY s.name = BINARY seed.supplier_name;

            DELETE FROM project_dictionaries WHERE type = 'ROBOT_MODEL';
            DELETE FROM project_dictionaries WHERE type = 'ROBOT_VENDOR';
            """);

        migrationBuilder.AddForeignKey(name: "fk_project_groups_robot_part", table: "project_groups",
            column: "robot_part_id", principalTable: "robot_parts", principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(name: "fk_projects_robot_part", table: "projects",
            column: "robot_part_id", principalTable: "robot_parts", principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "AddRobotPartCatalog removes legacy vendor/model dictionaries and introduces new catalog data; restore a pre-migration database backup to roll back without fabricating or losing business data.");
}
