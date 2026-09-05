use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260904_000003_integrity_hardening"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "ALTER TABLE files ADD COLUMN deleted_at DATETIME(3) NULL AFTER status, ADD INDEX idx_files_status_deleted_at (status, deleted_at)",
            "ALTER TABLE upload_sessions ADD COLUMN result_file_id BIGINT UNSIGNED NULL AFTER status, ADD INDEX idx_upload_result_file (result_file_id)",
            "ALTER TABLE email_outbox ADD COLUMN next_attempt_at DATETIME(3) NULL AFTER retry_count, ADD INDEX idx_outbox_status_next (status, next_attempt_at)",
            "INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description) VALUES ('storage.warn_percent', '85', '存储使用率告警阈值（百分比）')",
            "DELETE rp FROM role_permissions rp INNER JOIN roles r ON r.id = rp.role_id WHERE r.code = 'SUPPLIER' AND r.is_built_in = 1",
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT r.id, p.id FROM roles r JOIN permissions p ON p.code IN ('dashboard','project:list','file:upload','file:download','file:preview','message:create') WHERE r.code = 'SUPPLIER' AND r.is_built_in = 1",
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT r.id, p.id FROM roles r CROSS JOIN permissions p WHERE r.code = 'ADMIN' AND r.is_built_in = 1",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "DELETE FROM system_configs WHERE cfg_key = 'storage.warn_percent' AND cfg_value = '85'",
            "ALTER TABLE email_outbox DROP INDEX idx_outbox_status_next, DROP COLUMN next_attempt_at",
            "ALTER TABLE upload_sessions DROP INDEX idx_upload_result_file, DROP COLUMN result_file_id",
            "ALTER TABLE files DROP INDEX idx_files_status_deleted_at, DROP COLUMN deleted_at",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}
