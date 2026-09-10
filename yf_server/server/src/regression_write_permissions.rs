//! Transaction-final authorization regressions. Run only through scripts/test-isolated.py.

use crate::{
    entity::{audit_logs, permissions, projects, role_permissions},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{
    ColumnTrait, ConnectionTrait, DatabaseTransaction, DbBackend, EntityTrait, PaginatorTrait,
    QueryFilter, QuerySelect, Statement, TransactionTrait,
};

async fn grant_only(f: &Fixture, code: &str) -> u64 {
    let permission = permissions::Entity::find()
        .filter(permissions::Column::Code.eq(code))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("写入终检-{code}-{}", f.member.id),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role,
        &service::role::PermAssign {
            permission_ids: vec![permission.id],
        },
    )
    .await
    .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![role],
        },
    )
    .await
    .unwrap();
    service::perm::check_perm(&f.state.db, f.member.id, code)
        .await
        .unwrap();
    role
}

async fn gate_and_project(f: &Fixture) -> (DatabaseTransaction, u64) {
    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap()
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_owned(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    (txn, blocker)
}

async fn await_owned_lock(db: &sea_orm::DatabaseConnection, blocker: u64) {
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let row = db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
            if row.try_get::<i64>("", "n").unwrap() > 0 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
    })
    .await
    .expect("business writer never reached the owned lock barrier");
}

async fn revoke_and_release(txn: DatabaseTransaction, role: u64) {
    role_permissions::Entity::delete_many()
        .filter(role_permissions::Column::RoleId.eq(role))
        .exec(&txn)
        .await
        .unwrap();
    txn.commit().await.unwrap();
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_project_submit_rechecks_after_lock_wait() {
    use crate::entity::{
        enums::{FileDirection, FileStatus, ProjectStatus},
        files, project_status_logs,
    };
    use chrono::Utc;
    use sea_orm::{ActiveModelTrait, Set};

    let f = Fixture::new().await;
    files::ActiveModel {
        project_id: Set(f.project_id),
        uploader_id: Set(f.member.id),
        direction: Set(FileDirection::C2s),
        original_name: Set("permission-submit.pdf".into()),
        stored_name: Set(uuid::Uuid::new_v4().to_string()),
        ext: Set("pdf".into()),
        size_bytes: Set(1),
        mime_type: Set(Some("application/pdf".into())),
        sha256: Set(None),
        storage_path: Set("regression/permission-submit.pdf".into()),
        status: Set(FileStatus::Available),
        deleted_at: Set(None),
        created_at: Set(Utc::now()),
        ..Default::default()
    }
    .insert(&f.state.db)
    .await
    .unwrap();
    let role = grant_only(&f, "project:submit").await;
    let before = project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(f.project_id))
        .count(&f.state.db)
        .await
        .unwrap();
    let (txn, blocker) = gate_and_project(&f).await;
    let (db, actor, project_id) = (f.state.db.clone(), f.member.clone(), f.project_id);
    let pending = tokio::spawn(async move {
        service::project::submit(
            &db,
            "http://localhost",
            &actor,
            project_id,
            &service::project::SubmitReq {
                confirm_side: "SUPPLIER".into(),
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::InProgress
    );
    assert_eq!(
        project_status_logs::Entity::find()
            .filter(project_status_logs::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        before
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_message_create_rechecks_before_message_outbox_and_audit() {
    let f = Fixture::new().await;
    let audit_before = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("MESSAGE_CREATE"))
        .filter(audit_logs::Column::TargetType.eq("message"))
        .count(&f.state.db)
        .await
        .unwrap();
    let role = grant_only(&f, "message:create").await;
    let (txn, blocker) = gate_and_project(&f).await;
    let (db, actor, project_id) = (f.state.db.clone(), f.member.clone(), f.project_id);
    let pending = tokio::spawn(async move {
        service::message::create(
            &db,
            &actor,
            project_id,
            &service::message::MessageCreate {
                content: "权限撤销后不得留言".into(),
            },
            "http://localhost",
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        crate::entity::messages::Entity::find()
            .filter(crate::entity::messages::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        crate::entity::email_outbox::Entity::find()
            .filter(crate::entity::email_outbox::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        audit_logs::Entity::find()
            .filter(audit_logs::Column::Action.eq("MESSAGE_CREATE"))
            .filter(audit_logs::Column::TargetType.eq("message"))
            .count(&f.state.db)
            .await
            .unwrap(),
        audit_before
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_chunk_upload_rechecks_after_lock_wait() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "file:upload").await;
    let sid = f.init("revoked-chunk.pdf").await;
    let (txn, blocker) = gate_and_project(&f).await;
    let (state, actor, sid_for_write) = (f.state.clone(), f.member.clone(), sid.clone());
    let pending = tokio::spawn(async move {
        service::upload::put_chunk(&state, &actor, &sid_for_write, 0, b"test").await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert!(!crate::storage::chunk_path(&f.state.cfg.storage.root, &sid, 0).exists());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_merge_rechecks_before_file_outbox_and_audit() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "file:upload").await;
    let sid = f.init("revoked-merge.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    let (txn, blocker) = gate_and_project(&f).await;
    let (state, actor, sid_for_merge) = (f.state.clone(), f.member.clone(), sid.clone());
    let pending =
        tokio::spawn(async move { service::upload::merge(&state, &actor, &sid_for_merge).await });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        crate::entity::files::Entity::find()
            .filter(crate::entity::files::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        crate::entity::email_outbox::Entity::find()
            .filter(crate::entity::email_outbox::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn business_gate_is_shared_but_still_blocks_management_changes() {
    let f = Fixture::new().await;
    let first = f.state.db.begin().await.unwrap();
    service::perm::lock_business_state(&first).await.unwrap();
    let blocker = first
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_owned(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();

    let db = f.state.db.clone();
    let second = tokio::spawn(async move {
        let txn = db.begin().await.unwrap();
        service::perm::lock_business_state(&txn).await.unwrap();
        txn.commit().await.unwrap();
    });
    tokio::time::timeout(std::time::Duration::from_secs(1), second)
        .await
        .expect("a second business transaction was blocked by the shared gate")
        .unwrap();

    let db = f.state.db.clone();
    let mut management = tokio::spawn(async move {
        let txn = db.begin().await.unwrap();
        service::perm::lock_management_state(&txn).await.unwrap();
        txn.commit().await.unwrap();
    });
    await_owned_lock(&f.state.db, blocker).await;
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(200), &mut management)
            .await
            .is_err(),
        "management acquired its exclusive gate before the business reader released it"
    );
    first.commit().await.unwrap();
    management.await.unwrap();
}

#[derive(Clone, Copy, Debug)]
enum ManagementWrite {
    ProjectCreate,
    RoleCreate,
    RoleUpdate,
    SupplierCreate,
    SupplierUpdate,
    PasswordReset,
    ConfigUpdate,
}

impl ManagementWrite {
    fn permission(self) -> &'static str {
        match self {
            Self::ProjectCreate => "project:create",
            Self::RoleCreate | Self::RoleUpdate => "role:manage",
            Self::SupplierCreate | Self::SupplierUpdate => "supplier:manage",
            Self::PasswordReset => "user:manage",
            Self::ConfigUpdate => "config:manage",
        }
    }

    async fn run(
        self,
        db: &sea_orm::DatabaseConnection,
        actor: &crate::middleware::auth::CurrentUser,
        supplier_id: u64,
        role_id: u64,
        name: &str,
    ) -> crate::error::ApiResult<()> {
        match self {
            Self::ProjectCreate => service::project::create(
                db,
                actor,
                &service::project::ProjectUpsert {
                    name: name.into(),
                    description: None,
                    supplier_id,
                },
            )
            .await
            .map(|_| ()),
            Self::RoleCreate | Self::RoleUpdate => {
                let req = service::role::RoleUpsert {
                    name: name.into(),
                    description: Some("changed".into()),
                };
                if matches!(self, Self::RoleCreate) {
                    service::role::create(db, actor, &req).await.map(|_| ())
                } else {
                    service::role::update(db, actor, role_id, &req)
                        .await
                        .map(|_| ())
                }
            }
            Self::SupplierCreate | Self::SupplierUpdate => {
                let req = service::supplier::SupplierUpsert {
                    name: name.into(),
                    remark: Some("changed".into()),
                };
                if matches!(self, Self::SupplierCreate) {
                    service::supplier::create(db, actor, &req).await.map(|_| ())
                } else {
                    service::supplier::update(db, actor, supplier_id, &req)
                        .await
                        .map(|_| ())
                }
            }
            Self::PasswordReset => {
                service::user::reset_password(
                    db,
                    actor,
                    actor.id,
                    &service::user::PasswordReset {
                        new_password: "ChangedRegression123".into(),
                    },
                )
                .await
            }
            Self::ConfigUpdate => {
                service::config::update(
                    db,
                    actor,
                    &service::config::ConfigBatch {
                        items: vec![service::config::ConfigUpdate {
                            key: "storage.warn_percent".into(),
                            value: "79".into(),
                        }],
                    },
                )
                .await
            }
        }
    }

    // Compare persisted state without logging password hashes or other account data.
    async fn snapshot(self, f: &Fixture, supplier_id: u64, role_id: u64, name: &str) -> String {
        use crate::entity::{roles, suppliers, system_configs, users};
        let db = &f.state.db;
        match self {
            Self::ProjectCreate => format!(
                "{:?}",
                projects::Entity::find()
                    .filter(projects::Column::Name.eq(name))
                    .one(db)
                    .await
                    .unwrap()
            ),
            Self::RoleCreate => format!(
                "{:?}",
                roles::Entity::find()
                    .filter(roles::Column::Name.eq(name))
                    .one(db)
                    .await
                    .unwrap()
            ),
            Self::RoleUpdate => format!(
                "{:?}",
                roles::Entity::find_by_id(role_id).one(db).await.unwrap()
            ),
            Self::SupplierCreate => format!(
                "{:?}",
                suppliers::Entity::find()
                    .filter(suppliers::Column::Name.eq(name))
                    .one(db)
                    .await
                    .unwrap()
            ),
            Self::SupplierUpdate => format!(
                "{:?}",
                suppliers::Entity::find_by_id(supplier_id)
                    .one(db)
                    .await
                    .unwrap()
            ),
            Self::PasswordReset => {
                let user = users::Entity::find_by_id(f.member.id)
                    .one(db)
                    .await
                    .unwrap()
                    .unwrap();
                format!(
                    "{:?}",
                    (
                        user.password_hash,
                        user.must_change_password,
                        user.failed_login_attempts,
                        user.locked_until
                    )
                )
            }
            Self::ConfigUpdate => format!(
                "{:?}",
                system_configs::Entity::find_by_id("storage.warn_percent")
                    .one(db)
                    .await
                    .unwrap()
            ),
        }
    }
}

async fn management_writes_after_authorization_change(disable_account: bool) {
    use crate::entity::{enums::CommonStatus, users};
    use sea_orm::{ActiveModelTrait, Set};
    let mut outcomes = Vec::new();
    for operation in [
        ManagementWrite::ProjectCreate,
        ManagementWrite::RoleCreate,
        ManagementWrite::RoleUpdate,
        ManagementWrite::SupplierCreate,
        ManagementWrite::SupplierUpdate,
        ManagementWrite::PasswordReset,
        ManagementWrite::ConfigUpdate,
    ] {
        let f = Fixture::new().await;
        let role_id = grant_only(&f, operation.permission()).await;
        let supplier_id = projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .supplier_id;
        let name = format!("权限终检-{}", f.member.employee_no);
        let before = operation.snapshot(&f, supplier_id, role_id, &name).await;
        let audit_before = audit_logs::Entity::find()
            .filter(audit_logs::Column::UserId.eq(f.member.id))
            .count(&f.state.db)
            .await
            .unwrap();
        let (txn, blocker) = gate_and_project(&f).await;
        let (db, actor, request_name) = (f.state.db.clone(), f.member.clone(), name.clone());
        let mut pending = tokio::spawn(async move {
            operation
                .run(&db, &actor, supplier_id, role_id, &request_name)
                .await
        });
        // Before the fix, a write can complete while management owns the gate.
        // After the fix, observe the actual MySQL wait before revoking access.
        let premature = tokio::select! {
            result = &mut pending => Some(result.unwrap()),
            () = await_owned_lock(&f.state.db, blocker) => None,
        };
        if disable_account {
            let mut user: users::ActiveModel = users::Entity::find_by_id(f.member.id)
                .one(&txn)
                .await
                .unwrap()
                .unwrap()
                .into();
            user.status = Set(CommonStatus::Disabled);
            user.update(&txn).await.unwrap();
        } else {
            role_permissions::Entity::delete_many()
                .filter(role_permissions::Column::RoleId.eq(role_id))
                .exec(&txn)
                .await
                .unwrap();
        }
        txn.commit().await.unwrap();
        let waited = premature.is_none();
        let result = match premature {
            Some(result) => result,
            None => pending.await.unwrap(),
        };
        let unchanged = before == operation.snapshot(&f, supplier_id, role_id, &name).await;
        let audit_unchanged = audit_before
            == audit_logs::Entity::find()
                .filter(audit_logs::Column::UserId.eq(f.member.id))
                .count(&f.state.db)
                .await
                .unwrap();
        outcomes.push((
            operation,
            waited,
            matches!(result, Err(AppError::Forbidden)),
            unchanged,
            audit_unchanged,
        ));
    }
    assert!(
        outcomes
            .iter()
            .all(|(_, waited, denied, unchanged, audit_unchanged)| *waited
                && *denied
                && *unchanged
                && *audit_unchanged),
        "operation, waited, forbidden, data unchanged, audit unchanged: {outcomes:?}"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_management_writes_recheck_revoked_permission() {
    management_writes_after_authorization_change(false).await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_management_writes_recheck_disabled_account() {
    management_writes_after_authorization_change(true).await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_project_create_rechecks_supplier_after_management_wait() {
    use crate::entity::{enums::CommonStatus, suppliers};
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    grant_only(&f, "project:create").await;
    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let name = format!("供应商禁用终检-{}", f.member.employee_no);
    let (txn, blocker) = gate_and_project(&f).await;
    let (db, actor, request_name) = (f.state.db.clone(), f.member.clone(), name.clone());
    let pending = tokio::spawn(async move {
        service::project::create(
            &db,
            &actor,
            &service::project::ProjectUpsert {
                name: request_name,
                description: None,
                supplier_id,
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    let mut supplier: suppliers::ActiveModel = suppliers::Entity::find_by_id(supplier_id)
        .one(&txn)
        .await
        .unwrap()
        .unwrap()
        .into();
    supplier.status = Set(CommonStatus::Disabled);
    supplier.update(&txn).await.unwrap();
    txn.commit().await.unwrap();
    assert!(
        matches!(pending.await.unwrap(), Err(AppError::BadRequest(message)) if message == "供应商已被禁用")
    );
    assert_eq!(
        projects::Entity::find()
            .filter(projects::Column::Name.eq(name))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        audit_logs::Entity::find()
            .filter(audit_logs::Column::UserId.eq(f.member.id))
            .filter(audit_logs::Column::Action.eq("PROJECT_CREATE"))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}
