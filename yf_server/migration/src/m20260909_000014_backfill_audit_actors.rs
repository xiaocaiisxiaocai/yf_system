//! Repair historical audit rows that have a user id but no actor snapshot.
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000014_backfill_audit_actors"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "UPDATE audit_logs a INNER JOIN users u ON u.id = a.user_id \
                 SET a.employee_no = u.employee_no \
                 WHERE a.user_id IS NOT NULL \
                   AND (a.employee_no IS NULL OR TRIM(a.employee_no) = '')"
                    .to_string(),
            ))
            .await?;
        Ok(())
    }

    async fn down(&self, _manager: &SchemaManager) -> Result<(), DbErr> {
        // Correct actor snapshots cannot be distinguished from original values.
        Ok(())
    }
}
