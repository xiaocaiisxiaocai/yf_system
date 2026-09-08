//! 将业务身份字段切换到最终模型：用户使用 employee_no，
//! 项目、供应商、角色不再保存业务编码；权限点 code 仍作为权限匹配键保留。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260908_000006_identity_fields_cleanup"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // CHANGE 保留原有工号值；DROP COLUMN 明确删除不再属于业务模型的历史编码。
        for sql in [
            "ALTER TABLE users CHANGE COLUMN username employee_no VARCHAR(64) NOT NULL",
            "ALTER TABLE audit_logs CHANGE COLUMN username employee_no VARCHAR(64) NULL",
            "ALTER TABLE projects DROP COLUMN code",
            "ALTER TABLE suppliers DROP COLUMN code",
            "ALTER TABLE roles DROP COLUMN code",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // 回滚只能重建唯一占位编码，原业务编码已按本迁移的设计永久删除。
        for sql in [
            "ALTER TABLE roles ADD COLUMN code VARCHAR(64) NULL",
            "UPDATE roles SET code = CONCAT('ROLE_', id)",
            "ALTER TABLE roles MODIFY COLUMN code VARCHAR(64) NOT NULL",
            "ALTER TABLE roles ADD UNIQUE INDEX uk_roles_code (code)",
            "ALTER TABLE suppliers ADD COLUMN code VARCHAR(64) NULL",
            "UPDATE suppliers SET code = CONCAT('SUP_', id)",
            "ALTER TABLE suppliers MODIFY COLUMN code VARCHAR(64) NOT NULL",
            "ALTER TABLE suppliers ADD UNIQUE INDEX uk_suppliers_code (code)",
            "ALTER TABLE projects ADD COLUMN code VARCHAR(64) NULL",
            "UPDATE projects SET code = CONCAT('PRJ_', id)",
            "ALTER TABLE projects MODIFY COLUMN code VARCHAR(64) NOT NULL",
            "ALTER TABLE projects ADD UNIQUE INDEX uk_projects_code (code)",
            "ALTER TABLE audit_logs CHANGE COLUMN employee_no username VARCHAR(64) NULL",
            "ALTER TABLE users CHANGE COLUMN employee_no username VARCHAR(64) NOT NULL",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}
