//! 组织架构固定为 事业部 > 部门 > 课别，并为已有节点按深度回填类型。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000010_org_structure_kinds"
    }
}

pub(crate) async fn validate_org_structure(db: &impl ConnectionTrait) -> Result<(), DbErr> {
    let orphan = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT 1 AS invalid FROM departments d \
             LEFT JOIN departments p ON d.parent_id = p.id \
             WHERE d.parent_id IS NOT NULL AND p.id IS NULL LIMIT 1"
                .to_string(),
        ))
        .await?;
    if orphan.is_some() {
        return Err(DbErr::Migration(
            "组织架构迁移失败：检测到父级不存在的组织节点；请先修复组织层级后重试".to_string(),
        ));
    }

    // 第四层节点具有三个非空祖先；循环也必然形成这样的三跳父链。
    let too_deep_or_cyclic = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT 1 AS invalid FROM departments d \
             INNER JOIN departments p ON d.parent_id = p.id \
             INNER JOIN departments gp ON p.parent_id = gp.id \
             WHERE gp.parent_id IS NOT NULL LIMIT 1"
                .to_string(),
        ))
        .await?;
    if too_deep_or_cyclic.is_some() {
        return Err(DbErr::Migration(
            "组织架构迁移失败：检测到超过三级或循环的组织层级；请先修复组织层级后重试".to_string(),
        ));
    }
    Ok(())
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // 必须先于 ADD COLUMN：校验失败时保持旧表结构，清理数据后可安全重试。
        validate_org_structure(db).await?;
        for sql in [
            "ALTER TABLE departments ADD COLUMN kind VARCHAR(16) NOT NULL DEFAULT 'DIVISION'",
            "UPDATE departments SET kind = 'DIVISION' WHERE parent_id IS NULL",
            "UPDATE departments d INNER JOIN departments p ON d.parent_id = p.id SET d.kind = 'DEPARTMENT' WHERE p.parent_id IS NULL",
            "UPDATE departments d INNER JOIN departments p ON d.parent_id = p.id INNER JOIN departments gp ON p.parent_id = gp.id SET d.kind = 'SECTION'",
            "UPDATE permissions SET name = '组织架构' WHERE code = 'org:dept'",
            "UPDATE permissions SET name = '组织架构管理' WHERE code = 'dept:manage'",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for sql in [
            "UPDATE permissions SET name = '部门管理' WHERE code IN ('org:dept', 'dept:manage')",
            "ALTER TABLE departments DROP COLUMN kind",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}
