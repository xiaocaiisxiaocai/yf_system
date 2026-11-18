using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataScopesAndCrudPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_data_scopes",
                columns: table => new
                {
                    role_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    domain = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    scope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_data_scopes", x => new { x.role_id, x.domain });
                    table.ForeignKey(
                        name: "fk_rds_role",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "user_data_scopes",
                columns: table => new
                {
                    user_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    domain = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    scope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_data_scopes", x => new { x.user_id, x.domain });
                    table.ForeignKey(
                        name: "fk_uds_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            // CRUD permission points that were missing or had been folded into "manage" permissions.
            // Insert by code and let MySQL allocate the ID like every other permission migration: a fixed ID here
            // would collide with whatever the auto_increment counter reached on an installation that already ran
            // the earlier code-based migrations. An empty database gets the catalog (fixed IDs) from
            // BootstrapSeedCatalog after migrations run.
            migrationBuilder.Sql("""
                INSERT INTO permissions(code,name,type,parent_id,sort_no)
                SELECT seed.code,seed.name,'ACTION',parent.id,seed.sort_no
                FROM (
                    SELECT 'user:view' code,'查看用户' name,'org:user' parent_code,71 sort_no
                    UNION ALL SELECT 'supplier:view','查看供应商','supplier:list',72
                    UNION ALL SELECT 'role:view','查看角色','rbac:role',73
                    UNION ALL SELECT 'oem:transfer_update','编辑 ODM/OEMA 传递单','oem',74
                    UNION ALL SELECT 'oem:transfer_delete','删除 ODM/OEMA 传递单','oem',75
                    UNION ALL SELECT 'oem:directory_view','查看 ODM/OEMA 厂商与账号','oem',76
                    UNION ALL SELECT 'dict:delete','删除数据字典项','system:dict',77
                ) seed
                -- Keep the row even when a minimal installation predates the parent menu; the parent is only
                -- for the permission tree, and a real installation always has it.
                LEFT JOIN permissions parent ON parent.code=seed.parent_code
                WHERE EXISTS (SELECT 1 FROM permissions WHERE code='dashboard')
                  AND NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.code=seed.code);
                """);

            // Backfill: roles that could already read/manage keep exactly the same ability after the split.
            // Only the managing code is a source — a role that held just the MENU code (`org:user`,
            // `supplier:list`, `rbac:role`) could not pass the old read endpoint and must keep that state.
            // `oem:transfer_view` is deliberately not a source for `oem:directory_view`: seeing transfers never
            // implied reading vendor contacts or shared login accounts.
            foreach (var (grantedCodes, targetCode) in new[]
                     {
                         (new[] { "user:manage" }, "user:view"),
                         (new[] { "supplier:manage", "supplier:account" }, "supplier:view"),
                         (new[] { "role:manage" }, "role:view"),
                         (new[] { "oem:transfer_create" }, "oem:transfer_update"),
                         (new[] { "oem:transfer_create" }, "oem:transfer_delete"),
                         // `oem:directory_view` is a new, explicit grant only. The old read endpoints already let
                         // `oem:company_manage` read companies/contacts and `oem:account_manage` read accounts, so a
                         // backfill would hand each side the other's data (contacts e-mail, shared account names).
                         (new[] { "dict:manage" }, "dict:delete"),
                     })
            {
                migrationBuilder.Sql($"""
                    INSERT IGNORE INTO role_permissions(role_id,permission_id)
                    SELECT DISTINCT granted.role_id,target.id
                    FROM role_permissions granted
                    INNER JOIN permissions source ON source.id=granted.permission_id
                        AND source.code IN ({string.Join(",", grantedCodes.Select(code => $"'{code}'"))})
                    INNER JOIN permissions target ON target.code='{targetCode}';
                    """);
            }
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r
                INNER JOIN permissions p ON p.code IN
                    ('user:view','supplier:view','role:view','oem:transfer_update','oem:transfer_delete',
                     'oem:directory_view','dict:delete')
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);

            // Initial data ranges: keep today's project visibility (every role that can list projects sees all)
            // so deploying this migration does not take projects away from anybody; administrators narrow it
            // through the user/role range settings afterwards. OEM keeps the historical admin-only "see all".
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_data_scopes(role_id,`domain`,`scope`)
                SELECT DISTINCT granted.role_id,'project','ALL'
                FROM role_permissions granted
                INNER JOIN permissions p ON p.id=granted.permission_id AND p.code='project:list';
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_data_scopes(role_id,`domain`,`scope`)
                SELECT r.id,'oem_transfer','ALL' FROM roles r
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO role_data_scopes(role_id,`domain`,`scope`)
                SELECT r.id,'project','ALL' FROM roles r
                WHERE r.is_built_in=1 AND r.name='系统管理员';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema rollback only: every configured range is dropped together with the tables, so rolling back
            // means re-entering the SELF/ORG/ALL settings afterwards. Like the other permission migrations, the
            // permission rows and grants are kept, and re-applying this migration is an idempotent no-op for them.
            migrationBuilder.DropTable(
                name: "role_data_scopes");

            migrationBuilder.DropTable(
                name: "user_data_scopes");
        }
    }
}
