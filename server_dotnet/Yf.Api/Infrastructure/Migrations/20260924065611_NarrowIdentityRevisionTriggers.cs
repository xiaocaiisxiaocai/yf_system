using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yf.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NarrowIdentityRevisionTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_identity_users_update");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_users_update
                AFTER UPDATE ON users FOR EACH ROW
                BEGIN
                    IF NOT (OLD.id <=> NEW.id
                        AND BINARY OLD.employee_no <=> BINARY NEW.employee_no
                        AND BINARY OLD.user_type <=> BINARY NEW.user_type
                        AND OLD.supplier_id <=> NEW.supplier_id
                        AND BINARY OLD.status <=> BINARY NEW.status
                        AND OLD.must_change_password <=> NEW.must_change_password) THEN
                        UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                        WHERE cfg_key='security.identity_revision';
                    END IF;
                END
                """);

            migrationBuilder.Sql("DROP TRIGGER trg_identity_suppliers_update");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_suppliers_update
                AFTER UPDATE ON suppliers FOR EACH ROW
                BEGIN
                    IF NOT (OLD.id <=> NEW.id
                        AND BINARY OLD.status <=> BINARY NEW.status) THEN
                        UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                        WHERE cfg_key='security.identity_revision';
                    END IF;
                END
                """);

            migrationBuilder.Sql("DROP TRIGGER trg_identity_refresh_tokens_delete");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_refresh_tokens_delete
                AFTER DELETE ON refresh_tokens FOR EACH ROW
                BEGIN
                    IF OLD.revoked=0
                        AND OLD.expires_at > UTC_TIMESTAMP(6)
                        AND OLD.session_expires_at > UTC_TIMESTAMP(6) THEN
                        UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                        WHERE cfg_key='security.identity_revision';
                    END IF;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_identity_users_update");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_users_update
                AFTER UPDATE ON users FOR EACH ROW
                UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                WHERE cfg_key='security.identity_revision'
                """);

            migrationBuilder.Sql("DROP TRIGGER trg_identity_suppliers_update");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_suppliers_update
                AFTER UPDATE ON suppliers FOR EACH ROW
                UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                WHERE cfg_key='security.identity_revision'
                """);

            migrationBuilder.Sql("DROP TRIGGER trg_identity_refresh_tokens_delete");
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_identity_refresh_tokens_delete
                AFTER DELETE ON refresh_tokens FOR EACH ROW
                UPDATE system_configs SET cfg_value=CAST(cfg_value AS UNSIGNED)+1
                WHERE cfg_key='security.identity_revision'
                """);
        }
    }
}
