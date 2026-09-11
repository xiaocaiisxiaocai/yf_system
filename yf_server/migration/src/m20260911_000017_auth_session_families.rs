//! Bind access tokens to persistent refresh-token session families.
//!
//! MySQL commits DDL implicitly. Each structural step is therefore guarded so an interrupted
//! deployment can safely rerun this migration after only some statements reached the database.
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260911_000017_auth_session_families"
    }
}

async fn session_id_is_nullable(db: &impl ConnectionTrait) -> Result<bool, DbErr> {
    let row = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT IS_NULLABLE AS is_nullable FROM information_schema.columns \
             WHERE table_schema = DATABASE() AND table_name = 'refresh_tokens' \
             AND column_name = 'session_id'"
                .to_owned(),
        ))
        .await?
        .ok_or_else(|| DbErr::Custom("refresh_tokens.session_id is missing".to_owned()))?;
    Ok(row.try_get::<String>("", "is_nullable")? == "YES")
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        if !manager.has_column("refresh_tokens", "session_id").await? {
            db.execute_unprepared(
                "ALTER TABLE refresh_tokens ADD COLUMN session_id VARCHAR(36) NULL AFTER user_id",
            )
            .await?;
        }
        // Existing refresh sessions remain refreshable after migration. Existing access JWTs do
        // not contain sid and are intentionally rejected until the user signs in again. Repeating
        // this deterministic backfill leaves every already assigned family unchanged.
        db.execute_unprepared(
            "UPDATE refresh_tokens SET session_id = LPAD(LOWER(HEX(id)), 36, '0') \
             WHERE session_id IS NULL OR session_id = ''",
        )
        .await?;
        if session_id_is_nullable(db).await? {
            db.execute_unprepared(
                "ALTER TABLE refresh_tokens MODIFY COLUMN session_id VARCHAR(36) NOT NULL",
            )
            .await?;
        }
        if !manager
            .has_index("refresh_tokens", "idx_refresh_tokens_session_state")
            .await?
        {
            db.execute_unprepared(
                "CREATE INDEX idx_refresh_tokens_session_state ON refresh_tokens \
                 (session_id, user_id, revoked, expires_at)",
            )
            .await?;
        }
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        if manager
            .has_index("refresh_tokens", "idx_refresh_tokens_session_state")
            .await?
        {
            db.execute_unprepared("DROP INDEX idx_refresh_tokens_session_state ON refresh_tokens")
                .await?;
        }
        if manager.has_column("refresh_tokens", "session_id").await? {
            db.execute_unprepared("ALTER TABLE refresh_tokens DROP COLUMN session_id")
                .await?;
        }
        Ok(())
    }
}
