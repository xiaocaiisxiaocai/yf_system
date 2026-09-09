//! 清理历史重复业务键，并建立直接唯一约束。
//!
//! 名称比较沿用各列现有排序规则；每组保留最小 id。项目从属数据库记录按外键
//! 依赖顺序清理。数据库迁移不能可靠删除磁盘文件，部署前应清理已删除 files 或
//! upload_sessions 所指向的测试文件与临时目录。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement, TransactionTrait};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000015_business_name_uniqueness"
    }
}

async fn execute_all(db: &impl ConnectionTrait, statements: &[&str]) -> Result<(), DbErr> {
    for sql in statements {
        db.execute(Statement::from_string(DbBackend::MySql, (*sql).to_string()))
            .await?;
    }
    Ok(())
}

async fn merge_departments_once(db: &impl ConnectionTrait) -> Result<(), DbErr> {
    execute_all(
        db,
        &[
            "DROP TEMPORARY TABLE IF EXISTS tmp_duplicate_departments",
            "CREATE TEMPORARY TABLE tmp_duplicate_departments (duplicate_id BIGINT UNSIGNED PRIMARY KEY, keep_id BIGINT UNSIGNED NOT NULL) ENGINE=InnoDB SELECT d.id AS duplicate_id, MIN(k.id) AS keep_id FROM departments d JOIN departments k ON d.parent_id <=> k.parent_id AND d.kind = k.kind AND d.name = k.name AND k.id < d.id GROUP BY d.id",
            "UPDATE users u JOIN tmp_duplicate_departments m ON u.department_id = m.duplicate_id SET u.department_id = m.keep_id",
            "UPDATE departments d JOIN tmp_duplicate_departments m ON d.parent_id = m.duplicate_id SET d.parent_id = m.keep_id",
            "DELETE d FROM departments d JOIN tmp_duplicate_departments m ON d.id = m.duplicate_id",
            "DROP TEMPORARY TABLE tmp_duplicate_departments",
        ],
    )
    .await
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let txn = manager.get_connection().begin().await?;

        execute_all(
            &txn,
            &[
                "DROP TEMPORARY TABLE IF EXISTS tmp_duplicate_suppliers",
                "CREATE TEMPORARY TABLE tmp_duplicate_suppliers (duplicate_id BIGINT UNSIGNED PRIMARY KEY, keep_id BIGINT UNSIGNED NOT NULL) ENGINE=InnoDB SELECT s.id AS duplicate_id, MIN(k.id) AS keep_id FROM suppliers s JOIN suppliers k ON s.name = k.name AND k.id < s.id GROUP BY s.id",
                "UPDATE projects p JOIN tmp_duplicate_suppliers m ON p.supplier_id = m.duplicate_id SET p.supplier_id = m.keep_id",
                "UPDATE users u JOIN tmp_duplicate_suppliers m ON u.supplier_id = m.duplicate_id SET u.supplier_id = m.keep_id",
                "DELETE s FROM suppliers s JOIN tmp_duplicate_suppliers m ON s.id = m.duplicate_id",
                "DROP TEMPORARY TABLE tmp_duplicate_suppliers",
            ],
        )
        .await?;

        execute_all(
            &txn,
            &[
                "DROP TEMPORARY TABLE IF EXISTS tmp_duplicate_roles",
                "CREATE TEMPORARY TABLE tmp_duplicate_roles (duplicate_id BIGINT UNSIGNED PRIMARY KEY, keep_id BIGINT UNSIGNED NOT NULL) ENGINE=InnoDB SELECT r.id AS duplicate_id, MIN(k.id) AS keep_id FROM roles r JOIN roles k ON r.name = k.name AND k.id < r.id GROUP BY r.id",
                "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT m.keep_id, rp.permission_id FROM role_permissions rp JOIN tmp_duplicate_roles m ON rp.role_id = m.duplicate_id",
                "UPDATE user_roles ur JOIN tmp_duplicate_roles m ON ur.role_id = m.duplicate_id SET ur.role_id = m.keep_id",
                "DELETE rp FROM role_permissions rp JOIN tmp_duplicate_roles m ON rp.role_id = m.duplicate_id",
                "DELETE r FROM roles r JOIN tmp_duplicate_roles m ON r.id = m.duplicate_id",
                "DROP TEMPORARY TABLE tmp_duplicate_roles",
            ],
        )
        .await?;

        merge_departments_once(&txn).await?;
        merge_departments_once(&txn).await?;
        merge_departments_once(&txn).await?;

        execute_all(
            &txn,
            &[
                "DROP TEMPORARY TABLE IF EXISTS tmp_duplicate_projects",
                "CREATE TEMPORARY TABLE tmp_duplicate_projects (duplicate_id BIGINT UNSIGNED PRIMARY KEY) ENGINE=InnoDB SELECT p.id AS duplicate_id FROM projects p JOIN projects k ON p.name = k.name AND k.id < p.id GROUP BY p.id",
                "DELETE mr FROM message_reads mr JOIN messages m ON mr.message_id = m.id JOIN tmp_duplicate_projects d ON m.project_id = d.duplicate_id",
                "DELETE rsl FROM round_status_logs rsl JOIN rounds r ON rsl.round_id = r.id JOIN tmp_duplicate_projects d ON r.project_id = d.duplicate_id",
                "INSERT INTO audit_logs (action, target_type, target_id, detail) SELECT 'MIGRATION_ORPHAN_FILE', 'file', CAST(f.id AS CHAR), JSON_OBJECT('projectId', f.project_id, 'storagePath', f.storage_path) FROM files f JOIN tmp_duplicate_projects d ON f.project_id = d.duplicate_id",
                "INSERT INTO audit_logs (action, target_type, target_id, detail) SELECT 'MIGRATION_ORPHAN_UPLOAD', 'upload_session', us.id, JSON_OBJECT('projectId', us.project_id, 'tempDir', us.temp_dir) FROM upload_sessions us JOIN tmp_duplicate_projects d ON us.project_id = d.duplicate_id",
                "DELETE f FROM files f JOIN tmp_duplicate_projects d ON f.project_id = d.duplicate_id",
                "DELETE us FROM upload_sessions us JOIN tmp_duplicate_projects d ON us.project_id = d.duplicate_id",
                "DELETE m FROM messages m JOIN tmp_duplicate_projects d ON m.project_id = d.duplicate_id",
                "DELETE eo FROM email_outbox eo JOIN tmp_duplicate_projects d ON eo.project_id = d.duplicate_id",
                "DELETE pa FROM project_activities pa JOIN tmp_duplicate_projects d ON pa.project_id = d.duplicate_id",
                "DELETE pm FROM project_members pm JOIN tmp_duplicate_projects d ON pm.project_id = d.duplicate_id",
                "DELETE r FROM rounds r JOIN tmp_duplicate_projects d ON r.project_id = d.duplicate_id",
                "DELETE p FROM projects p JOIN tmp_duplicate_projects d ON p.id = d.duplicate_id",
                "DROP TEMPORARY TABLE tmp_duplicate_projects",
            ],
        )
        .await?;

        txn.commit().await?;

        execute_all(
            manager.get_connection(),
            &[
                "ALTER TABLE projects ADD UNIQUE INDEX uk_projects_name (name)",
                "ALTER TABLE suppliers ADD UNIQUE INDEX uk_suppliers_name (name)",
                "ALTER TABLE roles ADD UNIQUE INDEX uk_roles_name (name)",
                "ALTER TABLE departments ADD COLUMN business_parent_scope BIGINT UNSIGNED GENERATED ALWAYS AS (IFNULL(parent_id, 0)) STORED",
                "ALTER TABLE departments ADD UNIQUE INDEX uk_departments_parent_kind_name (business_parent_scope, kind, name)",
            ],
        )
        .await
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        execute_all(
            manager.get_connection(),
            &[
                "ALTER TABLE departments DROP INDEX uk_departments_parent_kind_name",
                "ALTER TABLE departments DROP COLUMN business_parent_scope",
                "ALTER TABLE roles DROP INDEX uk_roles_name",
                "ALTER TABLE suppliers DROP INDEX uk_suppliers_name",
                "ALTER TABLE projects DROP INDEX uk_projects_name",
            ],
        )
        .await
    }
}
