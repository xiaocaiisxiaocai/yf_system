//! 再校验已应用 010 的环境，并仅修正合法三级树中不一致的 kind。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

use super::m20260909_000010_org_structure_kinds::validate_org_structure;

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000011_validate_org_structure_kinds"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // 校验先于任何 UPDATE，失败时不改写现有业务数据，可在修复层级后重试。
        validate_org_structure(db).await?;
        for sql in [
            "UPDATE departments SET kind = 'DIVISION' WHERE parent_id IS NULL AND kind <> 'DIVISION'",
            "UPDATE departments d INNER JOIN departments p ON d.parent_id = p.id SET d.kind = 'DEPARTMENT' WHERE p.parent_id IS NULL AND d.kind <> 'DEPARTMENT'",
            "UPDATE departments d INNER JOIN departments p ON d.parent_id = p.id INNER JOIN departments gp ON p.parent_id = gp.id SET d.kind = 'SECTION' WHERE gp.parent_id IS NULL AND d.kind <> 'SECTION'",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }

    async fn down(&self, _manager: &SchemaManager) -> Result<(), DbErr> {
        // 011 只恢复由拓扑决定的派生 kind，没有可安全还原的旧值。
        Ok(())
    }
}
