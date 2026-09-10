//! 身份与供应商停用边界回归；仅允许通过 scripts/test-isolated.py 在临时 MySQL 库执行。

use crate::{
    entity::{message_reads, permissions, projects, suppliers, users},
    error::AppError,
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectOptions, ConnectionTrait, Database, DatabaseConnection,
    DbBackend, EntityTrait, QueryFilter, QuerySelect, Set, Statement, TransactionTrait,
};

async fn grant_user_manage(f: &Fixture) {
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("身份边界角色-{}", uuid::Uuid::new_v4()),
            description: None,
        },
    )
    .await
    .unwrap();
    let permission_id = permissions::Entity::find()
        .filter(permissions::Column::Code.eq("user:manage"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .id;
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role["id"].as_u64().unwrap(),
        &service::role::PermAssign {
            permission_ids: vec![permission_id],
        },
    )
    .await
    .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![role["id"].as_u64().unwrap()],
        },
    )
    .await
    .unwrap();
}

async fn create_supplier_account(f: &Fixture, supplier_id: u64) -> users::Model {
    let employee_no = format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
    let account = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no,
            password: "Regression123".into(),
            real_name: "供应商边界账号".into(),
            email: format!("{}@example.invalid", uuid::Uuid::new_v4()),
        },
    )
    .await
    .unwrap();
    users::Entity::find_by_id(account["id"].as_u64().unwrap())
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_user_manage_cannot_change_supplier_account_status() {
    let f = Fixture::new().await;
    grant_user_manage(&f).await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = create_supplier_account(&f, project.supplier_id).await;

    assert!(matches!(
        service::user::set_status(&f.state.db, &f.member, account.id, "DISABLED").await,
        Err(AppError::BadRequest(_))
    ));
    assert_eq!(
        users::Entity::find_by_id(account.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        crate::entity::enums::CommonStatus::Active
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_user_manage_cannot_reset_supplier_account_password() {
    let f = Fixture::new().await;
    grant_user_manage(&f).await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut account = create_supplier_account(&f, project.supplier_id).await;
    let mut update: users::ActiveModel = account.clone().into();
    update.must_change_password = Set(false);
    account = update.update(&f.state.db).await.unwrap();

    assert!(matches!(
        service::user::reset_password(
            &f.state.db,
            &f.member,
            account.id,
            &service::user::PasswordReset {
                new_password: "Changed123".into(),
            },
        )
        .await,
        Err(AppError::BadRequest(_))
    ));
    let after = users::Entity::find_by_id(account.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(after.password_hash, account.password_hash);
    assert!(!after.must_change_password);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_supplier_without_owner_cannot_login() {
    let f = Fixture::new().await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = create_supplier_account(&f, project.supplier_id).await;
    let mut update: users::ActiveModel = account.clone().into();
    update.supplier_id = Set(None);
    update.update(&f.state.db).await.unwrap();

    let result = service::auth::login(
        &f.state.db,
        &f.state.cfg,
        &crate::dto::LoginRequest {
            employee_no: account.employee_no,
            password: "Regression123".into(),
            captcha_id: None,
            captcha_code: None,
        },
        None,
        &f.state.captchas,
    )
    .await;
    assert!(matches!(result, Err(AppError::Unauthorized(_))));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_scope_rechecks_supplier_status_inside_business_transaction() {
    let f = Fixture::new().await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = create_supplier_account(&f, project.supplier_id).await;
    service::supplier::set_status(&f.state.db, &f.admin, project.supplier_id, "DISABLED")
        .await
        .unwrap();
    let current = CurrentUser {
        id: account.id,
        employee_no: account.employee_no,
        user_type: account.user_type,
        supplier_id: account.supplier_id,
    };
    let txn = f.state.db.begin().await.unwrap();

    assert!(matches!(
        service::scope::ensure_project_access(&txn, &current, f.project_id).await,
        Err(AppError::Forbidden) | Err(AppError::OutOfScope)
    ));
}

async fn await_waiting_on_management_gate(
    db: &DatabaseConnection,
    blocker: u64,
    task: &tokio::task::JoinHandle<crate::error::ApiResult<()>>,
) {
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            assert!(
                !task.is_finished(),
                "已读写入未等待供应商状态事务的管理门禁"
            );
            let row = db
                .query_one(Statement::from_string(
                    DbBackend::MySql,
                    format!(
                        "SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w \
                         JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id \
                         WHERE b.trx_mysql_thread_id = {blocker}"
                    ),
                ))
                .await
                .unwrap()
                .unwrap();
            if row.try_get::<i64>("", "n").unwrap() > 0 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
    })
    .await
    .expect("已读写入应在管理门禁等待供应商状态事务");
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_mark_read_rechecks_supplier_after_management_change() {
    let f = Fixture::new().await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = create_supplier_account(&f, project.supplier_id).await;
    let supplier_user = CurrentUser {
        id: account.id,
        employee_no: account.employee_no,
        user_type: account.user_type,
        supplier_id: account.supplier_id,
    };
    let message_id = service::message::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::message::MessageCreate {
            content: "供应商状态变更期间的已读".into(),
        },
        "http://localhost",
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();

    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    let supplier = suppliers::Entity::find_by_id(project.supplier_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap()
        .unwrap();
    let mut disabled: suppliers::ActiveModel = supplier.into();
    disabled.status = Set(crate::entity::enums::CommonStatus::Disabled);
    disabled.updated_at = Set(chrono::Utc::now());
    disabled.update(&txn).await.unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();

    let db = f.state.db.clone();
    let task = tokio::spawn(async move {
        service::message::mark_read(
            &db,
            &supplier_user,
            &service::message::MarkRead {
                ids: vec![message_id],
            },
        )
        .await
    });
    await_waiting_on_management_gate(&f.state.db, blocker, &task).await;
    txn.commit().await.unwrap();
    task.await.unwrap().unwrap();

    assert!(
        message_reads::Entity::find_by_id((message_id, account.id))
            .one(&f.state.db)
            .await
            .unwrap()
            .is_none(),
        "供应商停用提交后，等待中的已读请求不得落行"
    );
}

async fn management_gate_blocker(f: &Fixture) -> sea_orm::DatabaseTransaction {
    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    txn
}

async fn lock_timeout_connection() -> DatabaseConnection {
    let mut options = ConnectOptions::new(std::env::var("YF_TEST_DATABASE_URL").unwrap());
    options.max_connections(1).min_connections(1);
    let db = Database::connect(options).await.unwrap();
    db.execute(Statement::from_string(
        DbBackend::MySql,
        "SET SESSION innodb_lock_wait_timeout = 1",
    ))
    .await
    .unwrap();
    db
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn identity_review_supplier_and_account_status_changes_use_management_gate() {
    let f = Fixture::new().await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = create_supplier_account(&f, project.supplier_id).await;
    let blocked_db = lock_timeout_connection().await;

    let blocker = management_gate_blocker(&f).await;
    assert!(matches!(
        service::supplier::set_status(&blocked_db, &f.admin, project.supplier_id, "DISABLED").await,
        Err(AppError::Internal(_))
    ));
    blocker.commit().await.unwrap();
    service::supplier::set_status(&blocked_db, &f.admin, project.supplier_id, "DISABLED")
        .await
        .unwrap();

    service::supplier::set_status(&blocked_db, &f.admin, project.supplier_id, "ACTIVE")
        .await
        .unwrap();

    let blocker = management_gate_blocker(&f).await;
    assert!(matches!(
        service::supplier::set_account_status(&blocked_db, &f.admin, account.id, "DISABLED").await,
        Err(AppError::Internal(_))
    ));
    blocker.commit().await.unwrap();
    service::supplier::set_account_status(&blocked_db, &f.admin, account.id, "DISABLED")
        .await
        .unwrap();
}
