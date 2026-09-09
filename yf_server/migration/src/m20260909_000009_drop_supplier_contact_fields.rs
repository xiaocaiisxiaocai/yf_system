//! 供应商只保留名称与备注，删除联系人、电话、邮箱、地址。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000009_drop_supplier_contact_fields"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "ALTER TABLE suppliers DROP COLUMN contact_name",
            "ALTER TABLE suppliers DROP COLUMN contact_phone",
            "ALTER TABLE suppliers DROP COLUMN contact_email",
            "ALTER TABLE suppliers DROP COLUMN address",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "ALTER TABLE suppliers ADD COLUMN contact_name VARCHAR(64) NULL AFTER name",
            "ALTER TABLE suppliers ADD COLUMN contact_phone VARCHAR(32) NULL AFTER contact_name",
            "ALTER TABLE suppliers ADD COLUMN contact_email VARCHAR(128) NULL AFTER contact_phone",
            "ALTER TABLE suppliers ADD COLUMN address VARCHAR(255) NULL AFTER contact_email",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}
