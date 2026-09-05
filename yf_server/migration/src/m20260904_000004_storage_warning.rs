use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260904_000004_storage_warning"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "ALTER TABLE email_outbox DROP FOREIGN KEY fk_outbox_project, MODIFY COLUMN project_id BIGINT UNSIGNED NULL, ADD COLUMN dedupe_key VARCHAR(128) NULL AFTER event_type, ADD UNIQUE INDEX uk_outbox_dedupe_key (dedupe_key), ADD CONSTRAINT fk_outbox_project_v2 FOREIGN KEY (project_id) REFERENCES projects(id)".to_string(),
            ))
            .await?;
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "DELETE FROM email_outbox WHERE event_type = 'STORAGE_WARNING'",
            "ALTER TABLE email_outbox DROP FOREIGN KEY fk_outbox_project_v2, DROP INDEX uk_outbox_dedupe_key, DROP COLUMN dedupe_key, MODIFY COLUMN project_id BIGINT UNSIGNED NOT NULL, ADD CONSTRAINT fk_outbox_project FOREIGN KEY (project_id) REFERENCES projects(id)",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}
