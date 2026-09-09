use migration::MigratorTrait;
use sea_orm::{ConnectionTrait, Database, DbBackend, Statement};

async fn isolated_db() -> sea_orm::DatabaseConnection {
    let url = std::env::var("YF_TEST_DATABASE_URL").expect("请使用隔离测试脚本");
    let database_name = url.rsplit('/').next().unwrap_or_default();
    assert!(
        database_name.starts_with("yf_test_"),
        "组织迁移测试只能使用 yf_test_ 隔离库"
    );
    Database::connect(url).await.unwrap()
}

async fn reset_before_010(db: &sea_orm::DatabaseConnection) {
    reset_before(db, "m20260909_000010_org_structure_kinds").await;
}

async fn reset_before(db: &sea_orm::DatabaseConnection, migration_name: &str) {
    migration::Migrator::fresh(db).await.unwrap();
    let migrations = migration::Migrator::migrations();
    let target = migrations
        .iter()
        .position(|migration| migration.name() == migration_name)
        .expect("target migration must remain registered");
    migration::Migrator::down(db, Some((migrations.len() - target) as u32))
        .await
        .unwrap();
}

async fn kind_column_count(db: &sea_orm::DatabaseConnection) -> i64 {
    db.query_one(Statement::from_string(
        DbBackend::MySql,
        "SELECT COUNT(*) AS n FROM information_schema.columns \
         WHERE table_schema = DATABASE() AND table_name = 'departments' AND column_name = 'kind'"
            .to_string(),
    ))
    .await
    .unwrap()
    .unwrap()
    .try_get("", "n")
    .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_org_010_rejects_four_levels_before_ddl() {
    let db = isolated_db().await;
    reset_before_010(&db).await;
    db.execute(Statement::from_string(
        DbBackend::MySql,
        "INSERT INTO departments (id, name, parent_id) VALUES \
         (1001, '一级', NULL), (1002, '二级', 1001), \
         (1003, '三级', 1002), (1004, '四级', 1003)"
            .to_string(),
    ))
    .await
    .unwrap();

    let err = migration::Migrator::up(&db, Some(1))
        .await
        .expect_err("四层旧组织必须阻止 010");
    let error_text = err.to_string();
    let kind_columns = kind_column_count(&db).await;
    migration::Migrator::fresh(&db).await.unwrap();

    assert!(error_text.contains("超过三级"));
    assert_eq!(kind_columns, 0, "失败前不得开始 DDL");
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_org_010_migrates_valid_three_levels() {
    let db = isolated_db().await;
    reset_before_010(&db).await;
    db.execute(Statement::from_string(
        DbBackend::MySql,
        "INSERT INTO departments (id, name, parent_id) VALUES \
         (1101, '事业部', NULL), (1102, '部门', 1101), (1103, '课别', 1102)"
            .to_string(),
    ))
    .await
    .unwrap();

    migration::Migrator::up(&db, Some(1)).await.unwrap();
    let rows = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT id, kind FROM departments WHERE id BETWEEN 1101 AND 1103 ORDER BY id"
                .to_string(),
        ))
        .await
        .unwrap();
    let kinds: Vec<String> = rows
        .iter()
        .map(|row| row.try_get("", "kind").unwrap())
        .collect();
    db.execute(Statement::from_string(
        DbBackend::MySql,
        "UPDATE departments SET kind = CASE id \
         WHEN 1101 THEN 'SECTION' WHEN 1102 THEN 'DIVISION' ELSE 'DEPARTMENT' END \
         WHERE id BETWEEN 1101 AND 1103"
            .to_string(),
    ))
    .await
    .unwrap();
    migration::Migrator::up(&db, Some(1)).await.unwrap();
    let corrected = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT kind FROM departments WHERE id BETWEEN 1101 AND 1103 ORDER BY id".to_string(),
        ))
        .await
        .unwrap()
        .iter()
        .map(|row| row.try_get::<String>("", "kind").unwrap())
        .collect::<Vec<_>>();
    migration::Migrator::fresh(&db).await.unwrap();

    assert_eq!(kinds, ["DIVISION", "DEPARTMENT", "SECTION"]);
    assert_eq!(corrected, ["DIVISION", "DEPARTMENT", "SECTION"]);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_org_011_detects_invalid_data_after_010() {
    let db = isolated_db().await;
    reset_before(&db, "m20260909_000011_validate_org_structure_kinds").await;
    db.execute(Statement::from_string(
        DbBackend::MySql,
        "INSERT INTO departments (id, name, parent_id, kind) VALUES \
         (1201, '事业部', NULL, 'DIVISION'), (1202, '部门', 1201, 'DEPARTMENT'), \
         (1203, '课别', 1202, 'SECTION'), (1204, '遗留四级', 1203, 'DIVISION')"
            .to_string(),
    ))
    .await
    .unwrap();

    let err = migration::Migrator::up(&db, Some(1))
        .await
        .expect_err("011 必须阻止已应用 010 后的非法层级");
    let error_text = err.to_string();
    let kind: String = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT kind FROM departments WHERE id = 1204".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "kind")
        .unwrap();
    migration::Migrator::fresh(&db).await.unwrap();

    assert!(error_text.contains("超过三级"));
    assert_eq!(kind, "DIVISION", "校验失败时不得先改写 kind");
}
