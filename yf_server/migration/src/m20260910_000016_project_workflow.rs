//! One-way replacement of the round workflow with a project-level workflow.
//!
//! MySQL commits DDL implicitly. Every structural step therefore checks information_schema so a
//! deployment interrupted between statements can safely run the migration again.
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement, TransactionTrait};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260910_000016_project_workflow"
    }
}

async fn execute(db: &impl ConnectionTrait, sql: &str) -> Result<(), DbErr> {
    db.execute(Statement::from_string(DbBackend::MySql, sql.to_owned()))
        .await?;
    Ok(())
}

async fn exists(db: &impl ConnectionTrait, sql: &str, values: Vec<Value>) -> Result<bool, DbErr> {
    Ok(db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            sql,
            values,
        ))
        .await?
        .and_then(|row| row.try_get::<i64>("", "n").ok())
        .unwrap_or_default()
        > 0)
}

async fn table_exists(db: &impl ConnectionTrait, table: &str) -> Result<bool, DbErr> {
    exists(
        db,
        "SELECT COUNT(*) AS n FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?",
        vec![table.into()],
    )
    .await
}

async fn column_exists(
    db: &impl ConnectionTrait,
    table: &str,
    column: &str,
) -> Result<bool, DbErr> {
    exists(
        db,
        "SELECT COUNT(*) AS n FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = ? AND column_name = ?",
        vec![table.into(), column.into()],
    )
    .await
}

async fn foreign_key_exists(
    db: &impl ConnectionTrait,
    table: &str,
    name: &str,
) -> Result<bool, DbErr> {
    exists(
        db,
        "SELECT COUNT(*) AS n FROM information_schema.table_constraints WHERE constraint_schema = DATABASE() AND table_name = ? AND constraint_name = ? AND constraint_type = 'FOREIGN KEY'",
        vec![table.into(), name.into()],
    )
    .await
}

async fn index_exists(db: &impl ConnectionTrait, table: &str, name: &str) -> Result<bool, DbErr> {
    exists(
        db,
        "SELECT COUNT(*) AS n FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = ? AND index_name = ?",
        vec![table.into(), name.into()],
    )
    .await
}

async fn capture_storage_paths(db: &impl ConnectionTrait) -> Result<(), DbErr> {
    // The project aggregate is intentionally removed by this one-way migration. Keep the
    // physical paths in a small, migration-owned queue until the server has a configured storage
    // root and can remove them safely. Running the migration directly therefore never loses the
    // only index of a formal file or an in-progress upload directory.
    execute(
        db,
        r#"CREATE TABLE IF NOT EXISTS project_workflow_cleanup_paths (
            id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
            path_kind VARCHAR(16) NOT NULL,
            path_value VARCHAR(1024) NOT NULL,
            path_hash CHAR(64) CHARACTER SET ascii NOT NULL,
            PRIMARY KEY (id),
            UNIQUE KEY uq_project_workflow_cleanup_path (path_kind, path_hash)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"#,
    )
    .await?;

    if table_exists(db, "files").await? {
        execute(
            db,
            "INSERT IGNORE INTO project_workflow_cleanup_paths (path_kind, path_value, path_hash) SELECT 'FILE', storage_path, SHA2(storage_path, 256) FROM files WHERE storage_path IS NOT NULL AND storage_path <> ''",
        )
        .await?;
    }
    if table_exists(db, "upload_sessions").await? {
        execute(
            db,
            "INSERT IGNORE INTO project_workflow_cleanup_paths (path_kind, path_value, path_hash) SELECT 'TEMP_DIR', temp_dir, SHA2(temp_dir, 256) FROM upload_sessions WHERE temp_dir IS NOT NULL AND temp_dir <> ''",
        )
        .await?;
    }
    Ok(())
}

async fn drop_round_column(
    db: &impl ConnectionTrait,
    table: &str,
    foreign_key: Option<&str>,
    indexes: &[&str],
) -> Result<(), DbErr> {
    if !column_exists(db, table, "round_id").await? {
        return Ok(());
    }
    if let Some(name) = foreign_key {
        if foreign_key_exists(db, table, name).await? {
            execute(db, &format!("ALTER TABLE {table} DROP FOREIGN KEY {name}")).await?;
        }
    }
    for name in indexes {
        if index_exists(db, table, name).await? {
            execute(db, &format!("ALTER TABLE {table} DROP INDEX {name}")).await?;
        }
    }
    execute(db, &format!("ALTER TABLE {table} DROP COLUMN round_id")).await
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();

        capture_storage_paths(db).await?;

        // Clear the complete project aggregate. Accounts, suppliers, roles and settings survive.
        let txn = db.begin().await?;
        for (table, sql) in [
            ("project_status_logs", "DELETE FROM project_status_logs"),
            ("message_reads", "DELETE mr FROM message_reads mr INNER JOIN messages m ON m.id = mr.message_id"),
            ("round_status_logs", "DELETE FROM round_status_logs"),
            ("files", "DELETE FROM files"),
            ("upload_sessions", "DELETE FROM upload_sessions"),
            ("messages", "DELETE FROM messages"),
            ("email_outbox", "DELETE FROM email_outbox WHERE project_id IS NOT NULL"),
            ("project_activities", "DELETE FROM project_activities"),
            ("project_members", "DELETE FROM project_members"),
            ("rounds", "DELETE FROM rounds"),
            (
                "audit_logs",
                "DELETE FROM audit_logs WHERE target_type IN ('project','round','file','message','upload_session') OR LEFT(action, 8) = 'PROJECT_' OR LEFT(action, 6) = 'ROUND_' OR LEFT(action, 5) = 'FILE_' OR LEFT(action, 8) = 'MESSAGE_' OR LEFT(action, 7) = 'UPLOAD_'",
            ),
            ("projects", "DELETE FROM projects"),
        ] {
            if table_exists(&txn, table).await? {
                execute(&txn, sql).await?;
            }
        }
        txn.commit().await?;

        drop_round_column(db, "files", Some("fk_files_round"), &["idx_files_round"]).await?;
        // In the original schema MySQL creates the supporting upload FK index with the FK name.
        drop_round_column(db, "upload_sessions", Some("fk_us_round"), &["fk_us_round"]).await?;
        drop_round_column(db, "messages", Some("fk_msg_round"), &["idx_msg_round"]).await?;
        drop_round_column(db, "email_outbox", None, &[]).await?;
        drop_round_column(db, "project_activities", None, &[]).await?;
        if column_exists(db, "project_activities", "round_no").await? {
            execute(db, "ALTER TABLE project_activities DROP COLUMN round_no").await?;
        }
        if table_exists(db, "round_status_logs").await? {
            execute(db, "DROP TABLE round_status_logs").await?;
        }
        if table_exists(db, "rounds").await? {
            execute(db, "DROP TABLE rounds").await?;
        }

        execute(
            db,
            "ALTER TABLE projects MODIFY COLUMN status VARCHAR(24) NOT NULL DEFAULT 'DRAFT'",
        )
        .await?;
        if !column_exists(db, "projects", "confirm_side").await? {
            execute(
                db,
                "ALTER TABLE projects ADD COLUMN confirm_side VARCHAR(16) NULL AFTER status",
            )
            .await?;
        }
        execute(
            db,
            r#"CREATE TABLE IF NOT EXISTS project_status_logs (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                project_id BIGINT UNSIGNED NOT NULL,
                from_status VARCHAR(24) NULL,
                to_status VARCHAR(24) NOT NULL,
                action VARCHAR(16) NOT NULL,
                operator_id BIGINT UNSIGNED NOT NULL,
                confirm_side VARCHAR(16) NULL,
                reason VARCHAR(1024) NULL,
                created_at DATETIME(3) NOT NULL,
                PRIMARY KEY (id),
                KEY idx_project_status_logs_project_time (project_id, created_at, id),
                CONSTRAINT fk_project_status_logs_project FOREIGN KEY (project_id)
                    REFERENCES projects(id) ON DELETE RESTRICT,
                CONSTRAINT fk_project_status_logs_operator FOREIGN KEY (operator_id)
                    REFERENCES users(id) ON DELETE RESTRICT
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"#,
        )
        .await?;

        for sql in [
            "DELETE rp FROM role_permissions rp INNER JOIN permissions p ON p.id = rp.permission_id WHERE p.code IN ('round:create','round:confirm','round:cancel')",
            "DELETE FROM permissions WHERE code IN ('round:create','round:confirm','round:cancel')",
            "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) SELECT 'project:submit', '提交项目验收', 'ACTION', id, 40 FROM permissions WHERE code = 'project:list' AND type = 'MENU'",
            "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) SELECT 'project:confirm', '确认/驳回项目', 'ACTION', id, 41 FROM permissions WHERE code = 'project:list' AND type = 'MENU'",
            "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) SELECT 'project:withdraw', '撤回项目验收', 'ACTION', id, 42 FROM permissions WHERE code = 'project:list' AND type = 'MENU'",
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT r.id, p.id FROM roles r CROSS JOIN permissions p WHERE r.is_built_in = TRUE AND r.name = '系统管理员' AND p.code IN ('project:submit','project:confirm','project:withdraw')",
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT r.id, p.id FROM roles r CROSS JOIN permissions p WHERE r.is_built_in = TRUE AND r.name = '项目管理员' AND p.code IN ('project:submit','project:confirm','project:withdraw')",
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) SELECT r.id, p.id FROM roles r CROSS JOIN permissions p WHERE r.is_built_in = TRUE AND r.name IN ('内部成员','供应商人员') AND p.code IN ('project:submit','project:confirm')",
        ] {
            execute(db, sql).await?;
        }
        Ok(())
    }

    async fn down(&self, _manager: &SchemaManager) -> Result<(), DbErr> {
        Err(DbErr::Migration(
            "project workflow migration is intentionally one-way; restore from backup instead"
                .to_owned(),
        ))
    }
}
