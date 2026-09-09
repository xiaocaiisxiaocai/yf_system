//! 物理删除安全回归；必须由 scripts/test-isolated.py 在临时 MySQL 数据库中执行。

use crate::{
    entity::{departments, message_reads, permissions, roles, suppliers, users},
    error::AppError,
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};
use sea_orm::{ActiveModelTrait, ColumnTrait, EntityTrait, QueryFilter, Set};

async fn await_owned_lock(db: &sea_orm::DatabaseConnection, blocker: u64) {
    use sea_orm::{ConnectionTrait, DbBackend, Statement};
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let row = db
                .query_one(Statement::from_string(
                    DbBackend::MySql,
                    format!(
                        "SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w \
                         JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id \
                         WHERE b.trx_mysql_thread_id={blocker}"
                    ),
                ))
                .await
                .unwrap()
                .unwrap();
            if row.try_get::<i64>("", "n").unwrap() > 0 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        }
    })
    .await
    .expect("delete must wait for the business transaction's shared user lock");
}

async fn internal_role_id(f: &Fixture) -> u64 {
    roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .id
}

async fn create_internal_user(f: &Fixture, label: &str) -> users::Model {
    let employee_no = format!("d{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
    let value = service::user::create(
        &f.state.db,
        &f.admin,
        &service::user::UserCreate {
            employee_no: employee_no.clone(),
            password: "Regression123".into(),
            real_name: label.into(),
            email: format!("{employee_no}@example.invalid"),
            department_id: None,
            role_id: Some(internal_role_id(f).await),
            role_ids: None,
        },
    )
    .await
    .unwrap();
    users::Entity::find_by_id(value["id"].as_u64().unwrap())
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
}

async fn grant_permissions(f: &Fixture, codes: &[&str]) {
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("删除权限隔离角色-{}", uuid::Uuid::new_v4()),
            description: None,
        },
    )
    .await
    .unwrap();
    let role_id = role["id"].as_u64().unwrap();
    let mut permission_ids = Vec::with_capacity(codes.len());
    for code in codes {
        let permission_id = permissions::Entity::find()
            .filter(permissions::Column::Code.eq(*code))
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .id;
        permission_ids.push(permission_id);
    }
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role_id,
        &service::role::PermAssign { permission_ids },
    )
    .await
    .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![role_id],
        },
    )
    .await
    .unwrap();
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_requires_system_admin_for_all_physical_deletes() {
    let f = Fixture::new().await;
    grant_permissions(
        &f,
        &[
            "user:manage",
            "supplier:manage",
            "supplier:account",
            "role:manage",
            "dept:manage",
        ],
    )
    .await;

    let target_user = create_internal_user(&f, "待删用户").await;
    let supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!("待删供应商-{}", uuid::Uuid::new_v4()),
            remark: None,
        },
    )
    .await
    .unwrap();
    let account_supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!("账号供应商-{}", uuid::Uuid::new_v4()),
            remark: None,
        },
    )
    .await
    .unwrap();
    let account = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        account_supplier["id"].as_u64().unwrap(),
        &service::supplier::AccountCreate {
            employee_no: format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]),
            password: "Regression123".into(),
            real_name: "待删供应商账号".into(),
            email: "delete-account@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("待删角色-{}", uuid::Uuid::new_v4()),
            description: None,
        },
    )
    .await
    .unwrap();
    let dept = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: format!("待删事业部-{}", uuid::Uuid::new_v4()),
            parent_id: None,
            sort_no: None,
        },
    )
    .await
    .unwrap();

    assert!(matches!(
        service::user::delete(&f.state.db, &f.member, target_user.id).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::delete(&f.state.db, &f.member, supplier["id"].as_u64().unwrap()).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::delete_account(&f.state.db, &f.member, account["id"].as_u64().unwrap())
            .await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::role::delete(&f.state.db, &f.member, role["id"].as_u64().unwrap()).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::dept::delete(&f.state.db, &f.member, dept["id"].as_u64().unwrap()).await,
        Err(AppError::Forbidden)
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_supplier_with_accounts_must_not_cascade_delete_them() {
    let f = Fixture::new().await;
    let supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!("保留账号供应商-{}", uuid::Uuid::new_v4()),
            remark: None,
        },
    )
    .await
    .unwrap();
    let supplier_id = supplier["id"].as_u64().unwrap();
    let account = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]),
            password: "Regression123".into(),
            real_name: "必须保留的供应商账号".into(),
            email: "keep-account@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let account_id = account["id"].as_u64().unwrap();

    let result = service::supplier::delete(&f.state.db, &f.admin, supplier_id).await;
    assert!(matches!(
        result,
        Err(AppError::BadRequest(ref message)) if message.contains("账号")
    ));
    assert!(suppliers::Entity::find_by_id(supplier_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    assert!(users::Entity::find_by_id(account_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_message_read_history_blocks_user_deletion_and_is_preserved() {
    let f = Fixture::new().await;
    let target = create_internal_user(&f, "有已读历史用户").await;
    let target_user = CurrentUser {
        id: target.id,
        employee_no: target.employee_no.clone(),
        user_type: target.user_type,
        supplier_id: target.supplier_id,
    };
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.admin.id, target.id],
        },
    )
    .await
    .unwrap();
    let message = service::message::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::message::MessageCreate {
            content: "删除账号不能抹除的已读凭证".into(),
            round_id: None,
        },
        "http://localhost",
    )
    .await
    .unwrap();
    let message_id = message["id"].as_u64().unwrap();
    service::message::mark_read(
        &f.state.db,
        &target_user,
        &service::message::MarkRead {
            ids: vec![message_id],
        },
    )
    .await
    .unwrap();

    assert!(matches!(
        service::user::delete(&f.state.db, &f.admin, target.id).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(message_reads::Entity::find_by_id((message_id, target.id))
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    assert!(users::Entity::find_by_id(target.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_business_actor_history_blocks_user_deletion() {
    let f = Fixture::new().await;
    let target = create_internal_user(&f, "有业务历史用户").await;

    let project = crate::entity::projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut project_update: crate::entity::projects::ActiveModel = project.into();
    project_update.created_by = Set(target.id);
    project_update.update(&f.state.db).await.unwrap();

    assert!(matches!(
        service::user::delete(&f.state.db, &f.admin, target.id).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(users::Entity::find_by_id(target.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_waits_for_business_history_commit_before_deciding() {
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};

    let f = Fixture::new().await;
    let target = create_internal_user(&f, "并发业务历史用户").await;
    let target_user = CurrentUser {
        id: target.id,
        employee_no: target.employee_no.clone(),
        user_type: target.user_type,
        supplier_id: target.supplier_id,
    };
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.admin.id, target.id],
        },
    )
    .await
    .unwrap();

    let txn = f.state.db.begin().await.unwrap();
    service::scope::ensure_project_access(&txn, &target_user, f.project_id)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_string(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();

    let (state, admin, target_id) = (f.state.clone(), f.admin.clone(), target.id);
    let pending =
        tokio::spawn(async move { service::user::delete(&state.db, &admin, target_id).await });
    await_owned_lock(&f.state.db, blocker).await;

    let project = crate::entity::projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap()
        .unwrap();
    let mut project_update: crate::entity::projects::ActiveModel = project.into();
    project_update.created_by = Set(target.id);
    project_update.update(&txn).await.unwrap();
    txn.commit().await.unwrap();

    assert!(matches!(
        pending.await.unwrap(),
        Err(AppError::BadRequest(_))
    ));
    assert!(users::Entity::find_by_id(target.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_system_admin_can_delete_unreferenced_records() {
    let f = Fixture::new().await;
    let target_user = create_internal_user(&f, "可删除用户").await;
    let supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!("可删除供应商-{}", uuid::Uuid::new_v4()),
            remark: None,
        },
    )
    .await
    .unwrap();
    let supplier_id = supplier["id"].as_u64().unwrap();
    let account = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]),
            password: "Regression123".into(),
            real_name: "可删除供应商账号".into(),
            email: "deletable-account@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let account_id = account["id"].as_u64().unwrap();
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("可删除角色-{}", uuid::Uuid::new_v4()),
            description: None,
        },
    )
    .await
    .unwrap();
    let role_id = role["id"].as_u64().unwrap();
    let dept = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: format!("可删除事业部-{}", uuid::Uuid::new_v4()),
            parent_id: None,
            sort_no: None,
        },
    )
    .await
    .unwrap();
    let dept_id = dept["id"].as_u64().unwrap();

    service::user::delete(&f.state.db, &f.admin, target_user.id)
        .await
        .unwrap();
    service::supplier::delete_account(&f.state.db, &f.admin, account_id)
        .await
        .unwrap();
    service::supplier::delete(&f.state.db, &f.admin, supplier_id)
        .await
        .unwrap();
    service::role::delete(&f.state.db, &f.admin, role_id)
        .await
        .unwrap();
    service::dept::delete(&f.state.db, &f.admin, dept_id)
        .await
        .unwrap();

    assert!(users::Entity::find_by_id(target_user.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(users::Entity::find_by_id(account_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(suppliers::Entity::find_by_id(supplier_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(roles::Entity::find_by_id(role_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(departments::Entity::find_by_id(dept_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_department_keeps_user_bindings_and_regular_management_permissions() {
    let f = Fixture::new().await;
    grant_permissions(&f, &["dept:manage", "role:manage"]).await;
    let dept = service::dept::create(
        &f.state.db,
        &f.member,
        &service::dept::DeptUpsert {
            name: "仍可普通管理的组织".into(),
            parent_id: None,
            sort_no: None,
        },
    )
    .await
    .unwrap();
    let dept_id = dept["id"].as_u64().unwrap();
    service::dept::update(
        &f.state.db,
        &f.member,
        dept_id,
        &service::dept::DeptUpsert {
            name: "普通管理者可编辑".into(),
            parent_id: None,
            sort_no: Some(2),
        },
    )
    .await
    .unwrap();
    let role = service::role::create(
        &f.state.db,
        &f.member,
        &service::role::RoleUpsert {
            name: format!("普通管理角色{}", f.member.id),
            description: None,
        },
    )
    .await
    .unwrap();
    service::role::set_status(
        &f.state.db,
        &f.member,
        role["id"].as_u64().unwrap(),
        "DISABLED",
    )
    .await
    .unwrap();
    let mut target: users::ActiveModel = create_internal_user(&f, "关联组织用户").await.into();
    target.department_id = Set(Some(dept_id));
    let target = target.update(&f.state.db).await.unwrap();
    assert!(matches!(
        service::dept::delete(&f.state.db, &f.admin, dept_id).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(departments::Entity::find_by_id(dept_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    let kept = users::Entity::find_by_id(target.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(kept.department_id, Some(dept_id));
}
