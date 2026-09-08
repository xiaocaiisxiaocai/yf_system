//! 清理用户唯一索引的历史命名，保持最终数据库标识与 employee_no 一致。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260908_000007_identity_index_cleanup"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "ALTER TABLE users RENAME INDEX username TO uk_users_employee_no".to_string(),
            ))
            .await?;
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .get_connection()
            .execute(Statement::from_string(
                DbBackend::MySql,
                "ALTER TABLE users RENAME INDEX uk_users_employee_no TO username".to_string(),
            ))
            .await?;
        Ok(())
    }
}
