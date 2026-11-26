using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountNetworkScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "network_scope",
                table: "users",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ANY",
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            SetUserRevisionTrigger(migrationBuilder, includeNetworkScope: true);
            migrationBuilder.Sql("""
                INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description)
                VALUES ('security.internal_networks', '10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1', '内网网段'),
                       ('security.external_hosts', '183.238.37.108:8090', '外网访问地址');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            SetUserRevisionTrigger(migrationBuilder, includeNetworkScope: false);
            migrationBuilder.DropColumn(
                name: "network_scope",
                table: "users");
        }

        private static void SetUserRevisionTrigger(MigrationBuilder migrationBuilder, bool includeNetworkScope)
        {
            var scopeComparison = includeNetworkScope ? "\n        AND BINARY OLD.network_scope <=> BINARY NEW.network_scope" : "";
            migrationBuilder.Sql("DROP TRIGGER trg_identity_users_update");
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_identity_users_update
                AFTER UPDATE ON users FOR EACH ROW
                BEGIN
                    IF NOT (OLD.id <=> NEW.id
                        AND BINARY OLD.employee_no <=> BINARY NEW.employee_no
                        AND BINARY OLD.user_type <=> BINARY NEW.user_type
                        AND OLD.supplier_id <=> NEW.supplier_id
                        AND BINARY OLD.status <=> BINARY NEW.status
                        AND OLD.must_change_password <=> NEW.must_change_password{scopeComparison}) THEN
                        UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                        WHERE cfg_key='security.identity_revision';
                    END IF;
                END
                """);
        }
    }
}
