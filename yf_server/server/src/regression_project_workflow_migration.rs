//! One-way workflow migration regression; isolated MySQL only.
use migration::MigratorTrait;
use sea_orm::{ConnectionTrait, Database, DbBackend, Statement};

fn sibling_database_url(base: &str, database: &str) -> String {
    let (without_query, query) = base
        .split_once('?')
        .map_or((base, None), |(head, tail)| (head, Some(tail)));
    let slash = without_query.rfind('/').expect("MySQL URL 缺少数据库路径");
    let suffix = query.map_or_else(String::new, |value| format!("?{value}"));
    format!("{}/{database}{suffix}", &without_query[..slash])
}

async fn count(db: &sea_orm::DatabaseConnection, sql: &str) -> i64 {
    db.query_one(Statement::from_string(DbBackend::MySql, sql.to_owned()))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "n")
        .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_workflow_migration_clears_project_domain_and_preserves_accounts_and_auth_audit() {
    let outer_url = std::env::var("YF_TEST_DATABASE_URL").expect("请使用隔离测试脚本");
    assert!(outer_url
        .rsplit('/')
        .next()
        .unwrap()
        .starts_with("yf_test_"));
    let outer = Database::connect(&outer_url).await.unwrap();
    let database_name = format!("yf_test_workflow_{}", uuid::Uuid::new_v4().simple());
    outer
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!("CREATE DATABASE `{database_name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"),
        ))
        .await
        .unwrap();
    let database_url = sibling_database_url(&outer_url, &database_name);
    let db = Database::connect(database_url).await.unwrap();
    migration::Migrator::up(&db, Some(15)).await.unwrap();
    let storage_root = std::path::Path::new(&std::env::var("YF_TEST_STORAGE_ROOT").unwrap())
        .join(format!("workflow-migration-{}", uuid::Uuid::new_v4()));
    let formal_file = storage_root.join("files").join("legacy.pdf");
    let upload_temp = storage_root.join("tmp").join("legacy-upload");
    tokio::fs::create_dir_all(formal_file.parent().unwrap())
        .await
        .unwrap();
    tokio::fs::create_dir_all(&upload_temp).await.unwrap();
    tokio::fs::write(&formal_file, b"legacy-file")
        .await
        .unwrap();
    tokio::fs::write(upload_temp.join("0.part"), b"legacy-chunk")
        .await
        .unwrap();
    let upload_temp_value = upload_temp.to_string_lossy().to_string();
    let admin_id = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT id FROM users WHERE employee_no = 'admin'",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    let admin_before = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT employee_no, real_name, email, user_type, status, must_change_password, failed_login_attempts FROM users WHERE id = ?",
            [admin_id.into()],
        ))
        .await
        .unwrap()
        .unwrap();
    let admin_snapshot: (String, String, String, String, String, bool, i32) = (
        admin_before.try_get("", "employee_no").unwrap(),
        admin_before.try_get("", "real_name").unwrap(),
        admin_before.try_get("", "email").unwrap(),
        admin_before.try_get("", "user_type").unwrap(),
        admin_before.try_get("", "status").unwrap(),
        admin_before.try_get("", "must_change_password").unwrap(),
        admin_before.try_get("", "failed_login_attempts").unwrap(),
    );
    let roles_before: Vec<(u64, String, Option<String>, bool, String)> = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT id, name, description, is_built_in, status FROM roles ORDER BY id",
        ))
        .await
        .unwrap()
        .into_iter()
        .map(|row| -> (u64, String, Option<String>, bool, String) {
            (
                row.try_get("", "id").unwrap(),
                row.try_get("", "name").unwrap(),
                row.try_get("", "description").unwrap(),
                row.try_get("", "is_built_in").unwrap(),
                row.try_get("", "status").unwrap(),
            )
        })
        .collect();
    let configs_before: Vec<(String, Option<String>, Option<String>)> = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT cfg_key, cfg_value, description FROM system_configs ORDER BY cfg_key",
        ))
        .await
        .unwrap()
        .into_iter()
        .map(|row| -> (String, Option<String>, Option<String>) {
            (
                row.try_get("", "cfg_key").unwrap(),
                row.try_get("", "cfg_value").unwrap(),
                row.try_get("", "description").unwrap(),
            )
        })
        .collect();
    let supplier_name = format!("迁移供应商-{}", uuid::Uuid::new_v4());
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO suppliers (name, remark, status, created_by, created_at, updated_at) VALUES (?, NULL, 'ACTIVE', ?, NOW(3), NOW(3))",
        [supplier_name.clone().into(), admin_id.into()],
    ))
    .await
    .unwrap();
    let supplier_id = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT MAX(id) AS id FROM suppliers",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    let supplier_before = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT name, remark, status, created_by FROM suppliers WHERE id = ?",
            [supplier_id.into()],
        ))
        .await
        .unwrap()
        .unwrap();
    let supplier_snapshot: (String, Option<String>, String, Option<u64>) = (
        supplier_before.try_get("", "name").unwrap(),
        supplier_before.try_get("", "remark").unwrap(),
        supplier_before.try_get("", "status").unwrap(),
        supplier_before.try_get("", "created_by").unwrap(),
    );
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO projects (name, description, supplier_id, status, created_by, created_at, updated_at) VALUES (?, NULL, ?, 'IN_PROGRESS', ?, NOW(3), NOW(3))",
        [format!("迁移项目-{}", uuid::Uuid::new_v4()).into(), supplier_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    let project_id = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT MAX(id) AS id FROM projects",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO rounds (project_id, round_no, title, confirm_side, status, created_by, created_at, updated_at) VALUES (?, 1, '遗留轮次', 'SUPPLIER', 'PENDING', ?, NOW(3), NOW(3))",
        [project_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    let round_id = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT MAX(id) AS id FROM rounds",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    let file_stored_name = format!("legacy-{}", uuid::Uuid::new_v4());
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO files (project_id, round_id, uploader_id, direction, original_name, stored_name, ext, size_bytes, storage_path, status, created_at) VALUES (?, ?, ?, 'C2S', 'legacy.pdf', ?, 'pdf', 1, 'files/legacy.pdf', 'AVAILABLE', NOW(3))",
        [
            project_id.into(),
            round_id.into(),
            admin_id.into(),
            file_stored_name.clone().into(),
        ],
    ))
    .await
    .unwrap();
    let file_id = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT id FROM files WHERE stored_name = ?",
            [file_stored_name.into()],
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    let upload_session_id = uuid::Uuid::new_v4().to_string();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO upload_sessions (id, project_id, round_id, uploader_id, file_name, file_size, chunk_size, total_chunks, temp_dir, expires_at, created_at, updated_at) VALUES (?, ?, ?, ?, 'legacy.pdf', 1, 1, 1, ?, DATE_ADD(NOW(3), INTERVAL 1 DAY), NOW(3), NOW(3))",
        [
            upload_session_id.clone().into(),
            project_id.into(),
            round_id.into(),
            admin_id.into(),
            upload_temp_value.clone().into(),
        ],
    ))
    .await
    .unwrap();
    let message_content = format!("legacy-message-{}", uuid::Uuid::new_v4());
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO messages (project_id, round_id, sender_id, content, status, created_at) VALUES (?, ?, ?, ?, 'NORMAL', NOW(3))",
        [
            project_id.into(),
            round_id.into(),
            admin_id.into(),
            message_content.clone().into(),
        ],
    ))
    .await
    .unwrap();
    let message_id = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT id FROM messages WHERE content = ?",
            [message_content.into()],
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<u64>("", "id")
        .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO message_reads (message_id, user_id, read_at) VALUES (?, ?, NOW(3))",
        [message_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO round_status_logs (round_id, from_status, to_status, reason, operator_id, created_at) VALUES (?, NULL, 'PENDING', 'legacy round created', ?, NOW(3))",
        [round_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO email_outbox (event_type, project_id, round_id, recipient_user_id, recipient_email, subject, body, status, retry_count, created_at) VALUES ('MESSAGE_CREATED', ?, ?, ?, 'legacy@example.invalid', 'legacy message', 'legacy body', 'PENDING', 0, NOW(3))",
        [project_id.into(), round_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO project_members (project_id, user_id, created_by, created_at) VALUES (?, ?, ?, NOW(3))",
        [project_id.into(), admin_id.into(), admin_id.into()],
    ))
    .await
    .unwrap();
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "INSERT INTO project_activities (project_id, activity_type, action, actor_id, actor_name, occurred_at, title, summary, round_id, round_no, target_id, source_key) VALUES (?, 'ROUND', 'CREATE', ?, '系统管理员', NOW(3), '遗留轮次', 'legacy activity', ?, 1, ?, ?)",
        [
            project_id.into(),
            admin_id.into(),
            round_id.into(),
            round_id.into(),
            format!("legacy:workflow:{}", uuid::Uuid::new_v4()).into(),
        ],
    ))
    .await
    .unwrap();
    for (action, target_type, target_id) in [
        ("PROJECT_CREATE", "project", project_id.to_string()),
        ("ROUND_CREATE", "round", round_id.to_string()),
        ("FILE_UPLOAD", "file", file_id.to_string()),
        ("MESSAGE_CREATE", "message", message_id.to_string()),
        ("UPLOAD_INIT", "upload_session", upload_session_id.clone()),
        ("AUTH_LOGIN", "user", admin_id.to_string()),
    ] {
        db.execute(Statement::from_sql_and_values(
            DbBackend::MySql,
            "INSERT INTO audit_logs (user_id, employee_no, action, target_type, target_id, created_at) VALUES (?, 'admin', ?, ?, ?, NOW(3))",
            [admin_id.into(), action.into(), target_type.into(), target_id.into()],
        ))
        .await
        .unwrap();
    }
    for sql in [
        "SELECT COUNT(*) AS n FROM projects",
        "SELECT COUNT(*) AS n FROM rounds",
        "SELECT COUNT(*) AS n FROM round_status_logs",
        "SELECT COUNT(*) AS n FROM files",
        "SELECT COUNT(*) AS n FROM upload_sessions",
        "SELECT COUNT(*) AS n FROM messages",
        "SELECT COUNT(*) AS n FROM message_reads",
        "SELECT COUNT(*) AS n FROM email_outbox",
        "SELECT COUNT(*) AS n FROM project_activities",
        "SELECT COUNT(*) AS n FROM project_members",
    ] {
        assert_eq!(count(&db, sql).await, 1, "旧 schema 行未成功写入: {sql}");
    }
    assert_eq!(
        count(
            &db,
            "SELECT COUNT(*) AS n FROM audit_logs WHERE action IN ('PROJECT_CREATE','ROUND_CREATE','FILE_UPLOAD','MESSAGE_CREATE','UPLOAD_INIT')",
        )
        .await,
        5
    );

    // Simulate a MySQL DDL interruption: one legacy relation has already been removed,
    // while the migration record itself has not been committed yet.
    for sql in [
        "ALTER TABLE files DROP FOREIGN KEY fk_files_round",
        "ALTER TABLE files DROP INDEX idx_files_round",
        "ALTER TABLE files DROP COLUMN round_id",
    ] {
        db.execute(Statement::from_string(DbBackend::MySql, sql))
            .await
            .unwrap();
    }
    migration::Migrator::up(&db, None).await.unwrap();

    // The structural migration itself must also be safe when invoked again after all DDL landed.
    let workflow = migration::Migrator::migrations()
        .into_iter()
        .find(|migration| migration.name() == "m20260910_000016_project_workflow")
        .expect("project workflow migration must remain registered");
    let manager = migration::SchemaManager::new(&db);
    workflow.up(&manager).await.unwrap();

    // This replacement loses legacy data by design; a down migration must fail before changing
    // the completed schema instead of pretending to restore it.
    let down_error = workflow.down(&manager).await.unwrap_err().to_string();
    assert!(down_error.contains("one-way"));

    let admin_after = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT employee_no, real_name, email, user_type, status, must_change_password, failed_login_attempts FROM users WHERE id = ?",
            [admin_id.into()],
        ))
        .await
        .unwrap()
        .unwrap();
    let admin_snapshot_after: (String, String, String, String, String, bool, i32) = (
        admin_after.try_get("", "employee_no").unwrap(),
        admin_after.try_get("", "real_name").unwrap(),
        admin_after.try_get("", "email").unwrap(),
        admin_after.try_get("", "user_type").unwrap(),
        admin_after.try_get("", "status").unwrap(),
        admin_after.try_get("", "must_change_password").unwrap(),
        admin_after.try_get("", "failed_login_attempts").unwrap(),
    );
    assert_eq!(admin_snapshot_after, admin_snapshot, "账号记录必须保持不变");

    let supplier_after = db
        .query_one(Statement::from_sql_and_values(
            DbBackend::MySql,
            "SELECT name, remark, status, created_by FROM suppliers WHERE id = ?",
            [supplier_id.into()],
        ))
        .await
        .unwrap()
        .unwrap();
    let supplier_snapshot_after: (String, Option<String>, String, Option<u64>) = (
        supplier_after.try_get("", "name").unwrap(),
        supplier_after.try_get("", "remark").unwrap(),
        supplier_after.try_get("", "status").unwrap(),
        supplier_after.try_get("", "created_by").unwrap(),
    );
    assert_eq!(
        supplier_snapshot_after, supplier_snapshot,
        "供应商记录必须保持不变"
    );

    let roles_after: Vec<(u64, String, Option<String>, bool, String)> = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT id, name, description, is_built_in, status FROM roles ORDER BY id",
        ))
        .await
        .unwrap()
        .into_iter()
        .map(|row| -> (u64, String, Option<String>, bool, String) {
            (
                row.try_get("", "id").unwrap(),
                row.try_get("", "name").unwrap(),
                row.try_get("", "description").unwrap(),
                row.try_get("", "is_built_in").unwrap(),
                row.try_get("", "status").unwrap(),
            )
        })
        .collect();
    assert_eq!(roles_after, roles_before, "角色记录必须保持不变");

    let configs_after: Vec<(String, Option<String>, Option<String>)> = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT cfg_key, cfg_value, description FROM system_configs ORDER BY cfg_key",
        ))
        .await
        .unwrap()
        .into_iter()
        .map(|row| -> (String, Option<String>, Option<String>) {
            (
                row.try_get("", "cfg_key").unwrap(),
                row.try_get("", "cfg_value").unwrap(),
                row.try_get("", "description").unwrap(),
            )
        })
        .collect();
    assert_eq!(configs_after, configs_before, "系统参数必须保持不变");

    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM projects").await, 0);
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM files").await, 0);
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM upload_sessions").await,
        0
    );
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM messages").await, 0);
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM message_reads").await,
        0
    );
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM email_outbox").await,
        0
    );
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM project_activities").await,
        0
    );
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM project_members").await,
        0
    );
    assert_eq!(
        count(&db, "SELECT COUNT(*) AS n FROM project_status_logs").await,
        0
    );
    assert_eq!(
        count(
            &db,
            "SELECT COUNT(*) AS n FROM project_workflow_cleanup_paths"
        )
        .await,
        2
    );
    assert_eq!(
        count(
            &db,
            "SELECT COUNT(*) AS n FROM project_workflow_cleanup_paths WHERE path_kind = 'FILE' AND path_value = 'files/legacy.pdf'"
        )
        .await,
        1
    );
    assert_eq!(
        count(
            &db,
            "SELECT COUNT(*) AS n FROM project_workflow_cleanup_paths WHERE path_kind = 'TEMP_DIR'"
        )
        .await,
        1
    );
    assert_eq!(
        count(
            &db,
            "SELECT COUNT(*) AS n FROM audit_logs WHERE action IN ('PROJECT_CREATE','ROUND_CREATE','FILE_UPLOAD','MESSAGE_CREATE','UPLOAD_INIT')"
        )
        .await,
        0
    );
    assert_eq!(
        count(
            &db,
            &format!(
                "SELECT COUNT(*) AS n FROM audit_logs WHERE action = 'AUTH_LOGIN' AND user_id = {admin_id} AND employee_no = 'admin' AND target_type = 'user' AND target_id = '{admin_id}'"
            ),
        )
        .await,
        1
    );
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('rounds','round_status_logs')").await, 0);
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM information_schema.columns WHERE table_schema = DATABASE() AND column_name = 'round_id' AND table_name IN ('files','upload_sessions','messages','email_outbox','project_activities')").await, 0);
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM permissions WHERE code IN ('project:submit','project:confirm','project:withdraw')").await, 3);

    crate::service::migration_cleanup::drain(&db, storage_root.to_str().unwrap())
        .await
        .unwrap();
    assert!(!formal_file.exists(), "正式文件必须由迁移队列清理");
    assert!(!upload_temp.exists(), "上传临时目录必须由迁移队列清理");
    assert_eq!(count(&db, "SELECT COUNT(*) AS n FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'project_workflow_cleanup_paths'").await, 0);
    if storage_root.exists() {
        tokio::fs::remove_dir_all(&storage_root).await.unwrap();
    }

    db.close().await.unwrap();
    outer
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!("DROP DATABASE `{database_name}`"),
        ))
        .await
        .unwrap();
}
