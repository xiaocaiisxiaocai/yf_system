using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations;

/// <summary>
/// Data-only: adds the 三(四)轴 / SCARA四軸 / 蜘蛛手 Robot parts and the preset Robot types, and retires three
/// 六軸 parts the business crossed out. The fixed seed is an immutable snapshot of
/// Data/robot-catalog-20261008.json (JSON SHA-256 9232f2c6c80c403dd4b91e5f0c023b138decacbb6f524d8c7cfa7575a684e53b;
/// source workbook SHA-256 d4592ee72ba6c45023caff0f26a7adbed2685e8c3ddd37b7fac843165c90aafe).
/// Every insert skips rows that already exist, so administrator-entered suppliers, parts and types
/// (and their status) are kept and a re-run after Down adds nothing twice. Only 安川 and 埃斯顿 may be
/// removed, and only once nothing references them.
/// </summary>
public partial class SeedArmCatalogAndRobotTypes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Same guard as AddRobotPartCatalog: fail when an existing supplier only matches a catalog brand
        // under the accent/case-insensitive collation; reusing or re-inserting it would both be wrong.
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE yf_arm_supplier_seed_guard (marker INT NOT NULL PRIMARY KEY);
            INSERT INTO yf_arm_supplier_seed_guard (marker) VALUES (1);
            INSERT INTO yf_arm_supplier_seed_guard (marker)
            SELECT 1
            FROM suppliers AS s
            INNER JOIN (
                SELECT '珞石' AS name UNION ALL SELECT '海康' UNION ALL SELECT '匯川'
                UNION ALL SELECT '翼菲' UNION ALL SELECT '發那科'
            ) AS seed ON s.name = seed.name
            WHERE BINARY s.name <> BINARY seed.name
            LIMIT 1;
            DROP TEMPORARY TABLE yf_arm_supplier_seed_guard;
            """);

        migrationBuilder.Sql("""
            INSERT INTO suppliers (name, remark, status, created_by, created_at, updated_at)
            SELECT seed.name, 'Robot 料号目录初始化（2026-10-08）', 'ACTIVE', NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM (
                SELECT '珞石' AS name UNION ALL SELECT '海康' UNION ALL SELECT '匯川'
                UNION ALL SELECT '翼菲' UNION ALL SELECT '發那科'
            ) AS seed
            WHERE NOT EXISTS (SELECT 1 FROM suppliers AS s WHERE BINARY s.name = BINARY seed.name);

            INSERT INTO robot_parts (supplier_id, part_number, model, sort_no, status, created_at, updated_at)
            SELECT s.id, seed.part_number, seed.model, seed.sort_no, 'ACTIVE', CURRENT_TIMESTAMP(3), CURRENT_TIMESTAMP(3)
            FROM (
                SELECT '珞石' AS supplier_name, 'C-TNB1216R9533XBC5' AS part_number, '珞石三轴NB12-16KG-R953-3-防酸碱直出线5M-不含示教器' AS model, 28 AS sort_no
                UNION ALL SELECT '珞石', 'C-TNB1216R9534XBC5', '珞石四轴NB12-16KG-R953-4-防酸碱直出线5M-不含示教器', 29
                UNION ALL SELECT '珞石', 'C-TNB12169534XBC5R', '珞石四軸NB12-16KG-R953-4-防酸堿右出線5M-不含示教器', 30
                UNION ALL SELECT '珞石', 'C-TNB1216R9534XBCA', '珞石四轴NB12-16KG-R953-4-防酸碱直出线5M-不含示教器-发那科黄', 31
                UNION ALL SELECT '珞石', 'C-TNB1220R9534XBC5', '珞石四轴NB12-20KG-R953-4-防酸碱直出线5M-不含示教器', 32
                UNION ALL SELECT '珞石', 'C-TNB1225R9534XBC5', '珞石四轴NB12-25KG-R953-4-防酸碱直出线5M-不含示教器', 33
                UNION ALL SELECT '海康', 'C-THSR251000R500AA', '海康SCARA機器人HSR25-1000-R500-A1-含防塵罩-不含示教器', 34
                UNION ALL SELECT '海康', 'C-THSR351000R500AA', '海康SCARA機器人HSR35-1000-R500-A1-含防塵罩-不含示教器', 35
                UNION ALL SELECT '海康', 'C-THSR251000R500A1', '海康SCARA機器人HSR25-1000-R500-A1-含防塵罩-含示教器', 36
                UNION ALL SELECT '海康', 'C-THSR351000R500A1', '海康SCARA機器人HSR35-1000-R500-A1-含防塵罩-含示教器', 37
                UNION ALL SELECT '匯川', 'C-TIRS20100Z42S5', '匯川SCARA機器人IR-CS20-100Z42S5(標配Z軸上罩)', 38
                UNION ALL SELECT '翼菲', 'C-TBAT1600S6', '翼菲蜘蛛手BAT-1600-S6,無CC-LINK,I/O各32點,NPN', 39
                UNION ALL SELECT '翼菲', 'C-TBAT1600S6A', '翼菲蜘蛛手BAT-1600-S6無CCLINK,I/O各32點NPN,三相220V', 40
                UNION ALL SELECT '翼菲', 'C-TBAT1300S6', '並聯機器人BAT1300-S6,無CC-LINK,I/O各32點,NPN型，三相', 41
                UNION ALL SELECT '發那科', 'C-TM2IA3SL', '發那科蜘蛛手 M-2iA/3SL(CC-LINK含30萬像素CCD)', 42
                UNION ALL SELECT '發那科', 'C-TM2IA3SLA', '發那科蜘蛛手 M-2iA/3SL(CC-LINK含130萬像素CCD)', 43
            ) AS seed
            INNER JOIN suppliers AS s ON BINARY s.name = BINARY seed.supplier_name
            WHERE NOT EXISTS (
                SELECT 1 FROM robot_parts AS rp WHERE rp.supplier_id = s.id AND rp.part_number = seed.part_number);

            INSERT INTO project_dictionaries (type, name, parent_id, sort_no, status, created_at, updated_at)
            SELECT 'ROBOT_TYPE', seed.name, NULL, seed.sort_no, 'ACTIVE', CURRENT_TIMESTAMP(3), CURRENT_TIMESTAMP(3)
            FROM (
                SELECT '六轴' AS name, 1 AS sort_no
                UNION ALL SELECT '三轴', 2
                UNION ALL SELECT '四轴', 3
                UNION ALL SELECT 'SCARA四轴', 4
                UNION ALL SELECT '蜘蛛手', 5
            ) AS seed
            WHERE NOT EXISTS (
                SELECT 1 FROM project_dictionaries AS d WHERE d.type = 'ROBOT_TYPE' AND d.name = seed.name);
            """);

        // Retire the crossed-out 六軸 parts: a part a project already uses is only disabled (history keeps
        // showing it, new projects cannot pick it); an unused one is deleted. 安川 and 埃斯顿 then go as
        // well when nothing references them any more (no part, project or account), like a supplier delete.
        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE yf_retired_robot_parts (id BIGINT UNSIGNED NOT NULL PRIMARY KEY);
            INSERT INTO yf_retired_robot_parts (id)
            SELECT rp.id
            FROM robot_parts AS rp
            INNER JOIN suppliers AS s ON s.id = rp.supplier_id
            INNER JOIN (
                SELECT '安川' AS supplier_name, 'C-TGP251730' AS part_number
                UNION ALL SELECT '埃斯顿', 'C-TER151430MI'
                UNION ALL SELECT '埃斯顿', 'C-TER20B1760HI'
            ) AS retired ON BINARY s.name = BINARY retired.supplier_name
                AND BINARY rp.part_number = BINARY retired.part_number;

            UPDATE robot_parts AS rp
            INNER JOIN yf_retired_robot_parts AS r ON r.id = rp.id
            SET rp.status = 'DISABLED'
            WHERE rp.status <> 'DISABLED'
              AND (EXISTS (SELECT 1 FROM projects AS p WHERE p.robot_part_id = rp.id)
                OR EXISTS (SELECT 1 FROM project_groups AS g WHERE g.robot_part_id = rp.id));

            DELETE rp FROM robot_parts AS rp
            INNER JOIN yf_retired_robot_parts AS r ON r.id = rp.id
            WHERE NOT EXISTS (SELECT 1 FROM projects AS p WHERE p.robot_part_id = rp.id)
              AND NOT EXISTS (SELECT 1 FROM project_groups AS g WHERE g.robot_part_id = rp.id);

            DROP TEMPORARY TABLE yf_retired_robot_parts;

            DELETE s FROM suppliers AS s
            WHERE BINARY s.name IN (BINARY '安川', BINARY '埃斯顿')
              AND NOT EXISTS (SELECT 1 FROM robot_parts AS rp WHERE rp.supplier_id = s.id)
              AND NOT EXISTS (SELECT 1 FROM projects AS p WHERE p.supplier_id = s.id)
              AND NOT EXISTS (SELECT 1 FROM project_groups AS g WHERE g.supplier_id = s.id)
              AND NOT EXISTS (SELECT 1 FROM users AS u WHERE u.supplier_id = s.id);
            """);
    }

    // The seeded rows are ordinary catalog data that the previous schema also accepts, and projects may
    // already reference them; rolling back keeps them and does not resurrect the retired 六軸 parts or suppliers
    // (Up skips existing rows and re-retires idempotently, so re-applying is safe).
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
