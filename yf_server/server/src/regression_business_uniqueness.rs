//! 业务唯一键回归；必须由 scripts/test-isolated.py 在临时 MySQL 数据库中执行。
use crate::{
    entity::{projects, roles, suppliers},
    error::AppError,
    regression::Fixture,
    service,
};
use migration::MigratorTrait;
use sea_orm::{
    ColumnTrait, ConnectionTrait, Database, DbBackend, EntityTrait, QueryFilter, Statement,
};

fn assert_conflict<T>(result: Result<T, AppError>, expected: &str) {
    match result {
        Err(AppError::Conflict(message)) => assert_eq!(message, expected),
        _ => panic!("expected conflict: {expected}"),
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_business_unique_keys_trim_compare_with_database_collation_and_exclude_self() {
    let f = Fixture::new().await;
    let suffix = uuid::Uuid::new_v4().simple().to_string()[..10].to_string();

    let existing_project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_conflict(
        service::project::create(
            &f.state.db,
            &f.admin,
            &service::project::ProjectUpsert {
                name: format!(" {} ", existing_project.name.to_uppercase()),
                description: None,
                supplier_id: existing_project.supplier_id,
            },
        )
        .await,
        "项目名称已存在",
    );
    service::project::update(
        &f.state.db,
        &f.admin,
        existing_project.id,
        &service::project::ProjectUpsert {
            name: format!(" {} ", existing_project.name),
            description: None,
            supplier_id: existing_project.supplier_id,
        },
    )
    .await
    .unwrap();
    let other_project = service::project::create(
        &f.state.db,
        &f.admin,
        &service::project::ProjectUpsert {
            name: format!(" 另一项目{suffix} "),
            description: None,
            supplier_id: existing_project.supplier_id,
        },
    )
    .await
    .unwrap();
    assert_eq!(other_project["name"], format!("另一项目{suffix}"));
    assert_conflict(
        service::project::update(
            &f.state.db,
            &f.admin,
            other_project["id"].as_u64().unwrap(),
            &service::project::ProjectUpsert {
                name: existing_project.name.to_uppercase(),
                description: None,
                supplier_id: existing_project.supplier_id,
            },
        )
        .await,
        "项目名称已存在",
    );

    let existing_supplier = suppliers::Entity::find_by_id(existing_project.supplier_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_conflict(
        service::supplier::create(
            &f.state.db,
            &f.admin,
            &service::supplier::SupplierUpsert {
                name: format!(" {} ", existing_supplier.name.to_uppercase()),
                remark: None,
            },
        )
        .await,
        "供应商名称已存在",
    );
    service::supplier::update(
        &f.state.db,
        &f.admin,
        existing_supplier.id,
        &service::supplier::SupplierUpsert {
            name: format!(" {} ", existing_supplier.name),
            remark: None,
        },
    )
    .await
    .unwrap();
    let other_supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!(" 另一供应商{suffix} "),
            remark: None,
        },
    )
    .await
    .unwrap();
    assert_eq!(other_supplier["name"], format!("另一供应商{suffix}"));
    assert_conflict(
        service::supplier::update(
            &f.state.db,
            &f.admin,
            other_supplier["id"].as_u64().unwrap(),
            &service::supplier::SupplierUpsert {
                name: existing_supplier.name.to_uppercase(),
                remark: None,
            },
        )
        .await,
        "供应商名称已存在",
    );

    let role_name = format!("唯一角色AbC{suffix}");
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!(" {role_name} "),
            description: None,
        },
    )
    .await
    .unwrap();
    assert_eq!(role["name"], role_name);
    assert_conflict(
        service::role::create(
            &f.state.db,
            &f.admin,
            &service::role::RoleUpsert {
                name: role_name.to_uppercase(),
                description: None,
            },
        )
        .await,
        "角色名称已存在",
    );
    service::role::update(
        &f.state.db,
        &f.admin,
        role["id"].as_u64().unwrap(),
        &service::role::RoleUpsert {
            name: format!(" {role_name} "),
            description: None,
        },
    )
    .await
    .unwrap();
    let other_role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("另一角色{suffix}"),
            description: None,
        },
    )
    .await
    .unwrap();
    assert_conflict(
        service::role::update(
            &f.state.db,
            &f.admin,
            other_role["id"].as_u64().unwrap(),
            &service::role::RoleUpsert {
                name: role_name.to_uppercase(),
                description: None,
            },
        )
        .await,
        "角色名称已存在",
    );

    let division_name = format!("事业部AbC{suffix}");
    let division = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: format!(" {division_name} "),
            parent_id: None,
            sort_no: None,
        },
    )
    .await
    .unwrap();
    assert_conflict(
        service::dept::create(
            &f.state.db,
            &f.admin,
            &service::dept::DeptUpsert {
                name: division_name.to_uppercase(),
                parent_id: None,
                sort_no: None,
            },
        )
        .await,
        "同一上级和层级下组织名称已存在",
    );
    let child_name = format!("共享部门{suffix}");
    let first_child = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: child_name.clone(),
            parent_id: Some(division["id"].as_u64().unwrap()),
            sort_no: None,
        },
    )
    .await
    .unwrap();
    let other_division = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: format!("另一事业部{suffix}"),
            parent_id: None,
            sort_no: None,
        },
    )
    .await
    .unwrap();
    service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: child_name.clone(),
            parent_id: Some(other_division["id"].as_u64().unwrap()),
            sort_no: None,
        },
    )
    .await
    .unwrap();
    let sibling = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: format!("待改名{suffix}"),
            parent_id: Some(division["id"].as_u64().unwrap()),
            sort_no: None,
        },
    )
    .await
    .unwrap();
    assert_conflict(
        service::dept::update(
            &f.state.db,
            &f.admin,
            sibling["id"].as_u64().unwrap(),
            &service::dept::DeptUpsert {
                name: child_name.to_uppercase(),
                parent_id: Some(division["id"].as_u64().unwrap()),
                sort_no: None,
            },
        )
        .await,
        "同一上级和层级下组织名称已存在",
    );
    service::dept::update(
        &f.state.db,
        &f.admin,
        first_child["id"].as_u64().unwrap(),
        &service::dept::DeptUpsert {
            name: format!(" {child_name} "),
            parent_id: Some(division["id"].as_u64().unwrap()),
            sort_no: None,
        },
    )
    .await
    .unwrap();

    let internal_role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_conflict(
        service::user::create(
            &f.state.db,
            &f.admin,
            &service::user::UserCreate {
                employee_no: format!(" {} ", f.member.employee_no.to_uppercase()),
                password: "Regression123".into(),
                real_name: "重复工号".into(),
                email: "duplicate@example.invalid".into(),
                department_id: None,
                role_id: Some(internal_role.id),
                role_ids: None,
            },
        )
        .await,
        "工号已存在",
    );
}

async fn reset_before_015(db: &sea_orm::DatabaseConnection) {
    let migrations = migration::Migrator::migrations();
    let target = migrations
        .iter()
        .position(|item| item.name() == "m20260909_000015_business_name_uniqueness")
        .expect("business uniqueness migration must remain registered");
    migration::Migrator::up(db, Some(target as u32))
        .await
        .unwrap();
}

fn sibling_database_url(base: &str, database: &str) -> String {
    let (without_query, query) = base
        .split_once('?')
        .map_or((base, None), |(head, tail)| (head, Some(tail)));
    let slash = without_query.rfind('/').expect("MySQL URL 缺少数据库路径");
    let suffix = query.map_or_else(String::new, |value| format!("?{value}"));
    format!("{}/{database}{suffix}", &without_query[..slash])
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_uniqueness_migration_cleans_duplicates_rebinds_references_and_adds_indexes() {
    let url = std::env::var("YF_TEST_DATABASE_URL").expect("请使用隔离测试脚本");
    assert!(url.rsplit('/').next().unwrap().starts_with("yf_test_"));
    let outer = Database::connect(&url).await.unwrap();
    let database_name = format!("yf_test_unique_{}", uuid::Uuid::new_v4().simple());
    outer
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!(
                "CREATE DATABASE `{database_name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"
            ),
        ))
        .await
        .unwrap();
    let db = Database::connect(sibling_database_url(&url, &database_name))
        .await
        .unwrap();
    reset_before_015(&db).await;

    for sql in [
        "INSERT INTO suppliers (name) VALUES ('迁移重复供应商'), ('迁移重复供应商')",
        "INSERT INTO roles (name) VALUES ('迁移重复角色'), ('迁移重复角色')",
        "INSERT INTO departments (name, parent_id, kind) VALUES ('迁移重复事业部', NULL, 'DIVISION'), ('迁移重复事业部', NULL, 'DIVISION')",
        "INSERT INTO departments (name, parent_id, kind) SELECT '迁移重复部门', id, 'DEPARTMENT' FROM departments WHERE name = '迁移重复事业部'",
        "UPDATE users SET supplier_id = (SELECT MAX(id) FROM suppliers WHERE name = '迁移重复供应商') WHERE employee_no = 'admin'",
        "UPDATE users SET department_id = (SELECT MAX(id) FROM departments WHERE name = '迁移重复部门') WHERE employee_no = 'admin'",
        "UPDATE user_roles ur JOIN users u ON u.id = ur.user_id SET ur.role_id = (SELECT MAX(id) FROM roles WHERE name = '迁移重复角色') WHERE u.employee_no = 'admin'",
        "INSERT INTO projects (name, supplier_id, created_by) SELECT '迁移重复项目', MAX(s.id), u.id FROM suppliers s CROSS JOIN users u WHERE s.name = '迁移重复供应商' AND u.employee_no = 'admin' GROUP BY u.id",
        "INSERT INTO projects (name, supplier_id, created_by) SELECT '迁移重复项目', MIN(s.id), u.id FROM suppliers s CROSS JOIN users u WHERE s.name = '迁移重复供应商' AND u.employee_no = 'admin' GROUP BY u.id",
        "INSERT INTO rounds (project_id, round_no, confirm_side, created_by) SELECT MAX(id), 1, 'COMPANY', created_by FROM projects WHERE name = '迁移重复项目' GROUP BY created_by",
        "INSERT INTO project_members (project_id, user_id) SELECT p.id, u.id FROM projects p JOIN users u ON u.employee_no = 'admin' WHERE p.name = '迁移重复项目'",
        "INSERT INTO files (project_id, round_id, uploader_id, direction, original_name, stored_name, ext, size_bytes, storage_path) SELECT r.project_id, r.id, p.created_by, 'C2S', 'legacy.txt', CONCAT('legacy-', r.id), 'txt', 1, CONCAT('legacy/', r.id) FROM rounds r JOIN projects p ON p.id = r.project_id WHERE p.name = '迁移重复项目'",
        "INSERT INTO upload_sessions (id, project_id, round_id, uploader_id, file_name, file_size, chunk_size, total_chunks, temp_dir, expires_at) SELECT UUID(), r.project_id, r.id, p.created_by, 'legacy.txt', 1, 1, 1, CONCAT('legacy/', r.id), DATE_ADD(UTC_TIMESTAMP(), INTERVAL 1 DAY) FROM rounds r JOIN projects p ON p.id = r.project_id WHERE p.name = '迁移重复项目'",
    ] {
        db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
            .await
            .unwrap();
    }

    migration::Migrator::up(&db, Some(1)).await.unwrap();
    for (table, key, name) in [
        ("projects", "name", "迁移重复项目"),
        ("suppliers", "name", "迁移重复供应商"),
        ("roles", "name", "迁移重复角色"),
        ("departments", "name", "迁移重复事业部"),
    ] {
        let count: i64 = db
            .query_one(Statement::from_string(
                DbBackend::MySql,
                format!("SELECT COUNT(*) AS n FROM {table} WHERE {key} = '{name}'"),
            ))
            .await
            .unwrap()
            .unwrap()
            .try_get("", "n")
            .unwrap();
        assert_eq!(count, 1, "migration did not clean duplicates in {table}");
    }

    let supplier_id: u64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT id FROM suppliers WHERE name = '迁移重复供应商'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let user_supplier_id: u64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT supplier_id FROM users WHERE employee_no = 'admin'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "supplier_id")
        .unwrap();
    assert_eq!(user_supplier_id, supplier_id);
    let department_id: u64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT id FROM departments WHERE name = '迁移重复部门'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let user_department_id: u64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT department_id FROM users WHERE employee_no = 'admin'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "department_id")
        .unwrap();
    assert_eq!(user_department_id, department_id);
    let role_binding_count: i64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT COUNT(*) AS n FROM user_roles ur JOIN users u ON u.id = ur.user_id JOIN roles r ON r.id = ur.role_id WHERE u.employee_no = 'admin' AND r.name = '迁移重复角色'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "n")
        .unwrap();
    assert_eq!(role_binding_count, 1);
    let child_count: i64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT COUNT(*) AS n FROM departments WHERE name = '迁移重复部门'".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "n")
        .unwrap();
    assert_eq!(
        child_count, 1,
        "parent merge must also converge child duplicates"
    );
    for (table, column) in [("files", "storage_path"), ("upload_sessions", "temp_dir")] {
        let count: i64 = db
            .query_one(Statement::from_string(
                DbBackend::MySql,
                format!("SELECT COUNT(*) AS n FROM {table} WHERE {column} LIKE 'legacy/%'"),
            ))
            .await
            .unwrap()
            .unwrap()
            .try_get("", "n")
            .unwrap();
        assert_eq!(count, 0, "duplicate project dependent remained in {table}");
    }
    let orphan_audits: i64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT COUNT(*) AS n FROM audit_logs WHERE action IN ('MIGRATION_ORPHAN_FILE', 'MIGRATION_ORPHAN_UPLOAD')".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "n")
        .unwrap();
    assert_eq!(
        orphan_audits, 2,
        "physical cleanup paths must remain auditable"
    );

    for sql in [
        "INSERT INTO suppliers (name) VALUES ('迁移重复供应商')",
        "INSERT INTO roles (name) VALUES ('迁移重复角色')",
        "INSERT INTO departments (name, parent_id, kind) VALUES ('迁移重复事业部', NULL, 'DIVISION')",
        "INSERT INTO projects (name, supplier_id, created_by) SELECT '迁移重复项目', s.id, u.id FROM suppliers s JOIN users u ON u.employee_no = 'admin' WHERE s.name = '迁移重复供应商'",
        "INSERT INTO users (employee_no, password_hash, real_name, email, user_type) SELECT UPPER(employee_no), password_hash, '重复工号', 'duplicate@example.invalid', user_type FROM users WHERE employee_no = 'admin'",
    ] {
        db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
            .await
            .expect_err("direct duplicate write must be rejected by a unique index");
    }

    db.execute(Statement::from_string(
        DbBackend::MySql,
        "INSERT INTO departments (name, parent_id, kind) VALUES ('跨父级同名', NULL, 'DIVISION')"
            .to_string(),
    ))
    .await
    .unwrap();
    let parent_id: u64 = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT id FROM departments WHERE name = '跨父级同名' ORDER BY id DESC LIMIT 1"
                .to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    db.execute(Statement::from_string(
        DbBackend::MySql,
        format!("INSERT INTO departments (name, parent_id, kind) VALUES ('迁移重复事业部', {parent_id}, 'DEPARTMENT')"),
    ))
    .await
    .expect("same name under another parent and kind is allowed");

    db.close().await.unwrap();
    outer
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!("DROP DATABASE `{database_name}`"),
        ))
        .await
        .unwrap();
}
