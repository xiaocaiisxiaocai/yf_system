//! 账号不再保存电话：内部用户与供应商人员均只保留工号、姓名、邮箱。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000008_drop_users_phone"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "ALTER TABLE users DROP COLUMN phone".to_string(),
            ))
            .await?;
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "ALTER TABLE users ADD COLUMN phone VARCHAR(32) NULL AFTER email".to_string(),
            ))
            .await?;
        Ok(())
    }
}
